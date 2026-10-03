using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

namespace TranslatorAnywhere.Native;

public sealed class SelectionCapture : IDisposable
{
    private const int MaxTextLength = 32_000;
    private const long MaxClipboardBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan AutomationBudget = TimeSpan.FromMilliseconds(1100);
    private static readonly HashSet<string> ConsoleApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "wt", "mintty", "wezterm-gui", "alacritty"
    };
    private static readonly int[] ModifierKeys = { 0x10, 0x11, 0x12, 0x5B, 0x5C };
    private readonly BlockingCollection<AutomationRequest> _requests = new(1);
    private readonly SemaphoreSlim _clipboardGate = new(1, 1);
    private readonly Thread _worker;
    private AutomationRequest? _active;
    private volatile bool _disposed;

    public SelectionCapture()
    {
        // A single persistent worker also bounds damage from a provider that stalls inside COM.
        // The caller's deadline does not cancel an in-flight UIA call. Never create replacement threads.
        _worker = new Thread(RunAutomation) { Name = "Selection UI Automation", IsBackground = true };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public async Task<SelectionSnapshot?> CaptureAsync(SelectionGesture gesture, bool allowClipboard, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (!HasSourceFocus(gesture) || DesktopInterop.IsOwnWindow(gesture.SourceWindow)) { DiagnosticLog.Write("Capture: source focus changed before capture"); return null; }
        IntPtr nativeFocus = GetFocusedWindow(gesture.SourceWindow);
        var request = new AutomationRequest(gesture, token, allowClipboard);
        bool queued;
        try { queued = _requests.TryAdd(request); }
        catch (InvalidOperationException) { return null; }
        SelectionSnapshot? result = null;
        bool automationCompleted = false;
        if (queued)
        {
            try
            {
                result = await request.Completion.Task.WaitAsync(AutomationBudget, token).ConfigureAwait(false);
                automationCompleted = true;
            }
            catch (TimeoutException) { DiagnosticLog.Write("Capture: UI Automation timed out"); }
            finally { request.Expire(); }
        }
        if (result is not null && HasSourceFocus(gesture) && (nativeFocus == IntPtr.Zero || GetFocusedWindow(gesture.SourceWindow) == nativeFocus)) return result;
        // An unreadable password property must never be interpreted as permission to copy.
        // A timed-out provider may have checked a different control before blocking. Only completed checks qualify.
        if (!automationCompleted || !allowClipboard || request.ClipboardSafety != ClipboardSafety.Safe ||
            nativeFocus == IntPtr.Zero || !HasCopyFocus(gesture, nativeFocus))
        {
            DiagnosticLog.Write($"Capture: fallback blocked; completed={automationCompleted}; enabled={allowClipboard}; safety={request.ClipboardSafety}; nativeFocus={nativeFocus != IntPtr.Zero}; sameFocus={HasCopyFocus(gesture, nativeFocus)}");
            return null;
        }
        var application = DesktopInterop.GetApplicationName(gesture.SourceWindow);
        if (ConsoleApplications.Contains(application)) return null;
        DiagnosticLog.Write("Capture: starting clipboard fallback");
        return await CaptureClipboardAsync(gesture, application, nativeFocus, token).ConfigureAwait(false);
    }

    private void RunAutomation()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            if (_disposed || request.IsExpired || request.Token.IsCancellationRequested)
            {
                request.Completion.TrySetResult(null);
                continue;
            }
            _active = request;
            try { request.Completion.TrySetResult(CaptureAutomation(request)); }
            catch (Exception error)
            {
                Trace.TraceWarning("UI Automation capture: {0}", error.Message);
                request.Completion.TrySetResult(null);
            }
            finally { _active = null; }
        }
    }

    private SelectionSnapshot? CaptureAutomation(AutomationRequest request)
    {
        if (!IsCurrent(request)) return null;
        var gesture = request.Gesture;
        var sourceProcess = DesktopInterop.GetProcessId(gesture.SourceWindow);
        if (sourceProcess == 0 || sourceProcess == Environment.ProcessId) return null;
        AutomationElement? focused = null;
        if (request.AllowClipboard) request.ClipboardSafety = ReadLegacySafety(gesture);
        if (request.ClipboardSafety == ClipboardSafety.Denied) return null;
        var application = DesktopInterop.GetApplicationName(gesture.SourceWindow);
        if (request.AllowClipboard && request.ClipboardSafety == ClipboardSafety.Safe &&
            (application.Equals("FoxitPDFEditor", StringComparison.OrdinalIgnoreCase) || application.Equals("zotero", StringComparison.OrdinalIgnoreCase)))
            return null; // These configured readers use MSAA; avoid their slow UIA bridge.
        try
        {
            focused = AutomationElement.FocusedElement;
            if (focused is not null)
            {
                var info = focused.Current;
                if (info.ProcessId == sourceProcess)
                {
                    // Current.IsPassword substitutes false for unsupported properties. Unknown is not safe.
                    object passwordValue = focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
                    if (passwordValue is bool isPassword)
                    {
                        request.ClipboardSafety = isPassword ? ClipboardSafety.Denied : ClipboardSafety.Safe;
                        if (isPassword) return null;
                    }
                }
            }
        }
        catch (Exception error) when (IsAutomationError(error)) { }
        if (!IsCurrent(request)) return null;

        AutomationElement? pointed = null;
        try
        {
            pointed = AutomationElement.FromPoint(gesture.End);
            if (pointed is not null && pointed.Current.ProcessId == sourceProcess && pointed.Current.IsPassword)
            {
                request.ClipboardSafety = ClipboardSafety.Denied;
                return null;
            }
        }
        catch (Exception error) when (IsAutomationError(error)) { }

        foreach (var initial in new[] { focused, pointed })
        {
            var element = initial;
            for (int level = 0; element is not null && level < 8 && IsCurrent(request); level++)
            {
                try
                {
                    var info = element.Current;
                    if (info.ProcessId != sourceProcess) break;
                    if (info.IsPassword)
                    {
                        request.ClipboardSafety = ClipboardSafety.Denied;
                        return null;
                    }
                    var snapshot = ReadSelectedText(element, request);
                    if (snapshot is not null) return snapshot;
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
                catch (Exception error) when (IsAutomationError(error)) { break; }
            }
        }
        // Some controls expose the selection on the application's HWND itself.
        if (IsCurrent(request))
        {
            try
            {
                var source = AutomationElement.FromHandle(gesture.SourceWindow);
                if (source.Current.IsPassword) { request.ClipboardSafety = ClipboardSafety.Denied; return null; }
                return ReadSelectedText(source, request);
            }
            catch (Exception error) when (IsAutomationError(error)) { }
        }
        return null;
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible);

    private static ClipboardSafety ReadLegacySafety(SelectionGesture gesture)
    {
        // Some PDF/Gecko controls expose MSAA but no UIA password property.
        // Only a focused object with a readable non-protected state qualifies for copy fallback.
        var nativeFocus = GetFocusedWindow(gesture.SourceWindow);
        if (nativeFocus == IntPtr.Zero || !HasSourceFocus(gesture)) return ClipboardSafety.Unknown;
        var references = new List<object>();
        try
        {
            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            if (AccessibleObjectFromWindow(nativeFocus, unchecked((uint)-4), ref iid, out var current) < 0 || current is null)
                return ClipboardSafety.Unknown;
            references.Add(current);
            object child = 0;
            for (int depth = 0; depth < 8; depth++)
            {
                object focus = current.accFocus;
                if (focus is Accessibility.IAccessible next)
                {
                    references.Add(next);
                    current = next;
                    if (depth == 7) return ClipboardSafety.Unknown;
                    continue;
                }
                if (focus is int id) child = id;
                break;
            }
            if (current.get_accState(child) is not int state) return ClipboardSafety.Unknown;
            if ((state & 0x20000000) != 0) return ClipboardSafety.Denied;
            bool focused = (state & 4) != 0;
            DiagnosticLog.Write($"Capture: MSAA focused={focused}; sameFocus={HasCopyFocus(gesture, nativeFocus)}");
            return focused && HasCopyFocus(gesture, nativeFocus) ? ClipboardSafety.Safe : ClipboardSafety.Unknown;
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException)
        {
            DiagnosticLog.Write("Capture: MSAA safety unavailable", error);
            return ClipboardSafety.Unknown;
        }
        finally
        {
            foreach (object reference in references)
                if (Marshal.IsComObject(reference)) Marshal.ReleaseComObject(reference);
        }
    }

    private SelectionSnapshot? ReadSelectedText(AutomationElement element, AutomationRequest request)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var value) || value is not TextPattern pattern ||
            pattern.SupportedTextSelection == SupportedTextSelection.None) return null;
        var ranges = pattern.GetSelection();
        if (ranges is null || ranges.Length == 0 || ranges.Length > 32) return null;
        var text = new StringBuilder();
        var bounds = Rect.Empty;
        foreach (var range in ranges)
        {
            if (!IsCurrent(request)) return null;
            // Never use DocumentRange. A degenerate insertion point yields empty text.
            string selected = range.GetText(MaxTextLength + 1 - text.Length);
            if (string.IsNullOrWhiteSpace(selected)) continue;
            if (text.Length > 0) text.AppendLine();
            text.Append(selected);
            if (text.Length > MaxTextLength) return null;
            foreach (var rect in range.GetBoundingRectangles())
            {
                if (!rect.IsEmpty && double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
                    double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && rect.Width > 0 && rect.Height > 0)
                    bounds.Union(rect);
            }
        }
        string content = text.ToString().Trim();
        if (content.Length == 0 || !IsCurrent(request)) return null;
        var gesture = request.Gesture;
        bool mouseSelection = gesture.IsDoubleClick || Math.Abs(gesture.End.X - gesture.Start.X) >= 4 || Math.Abs(gesture.End.Y - gesture.Start.Y) >= 4;
        if (mouseSelection && !bounds.IsEmpty)
        {
            var vicinity = bounds;
            vicinity.Inflate(24, 24);
            // A scrollbar drag must not reactivate an unrelated old selection elsewhere in the document.
            if (!vicinity.Contains(gesture.Start) && !vicinity.Contains(gesture.End)) return null;
        }
        if (bounds.IsEmpty) bounds = new Rect(gesture.End.X, gesture.End.Y, 1, 1);
        return new SelectionSnapshot(content, DesktopInterop.GetApplicationName(gesture.SourceWindow),
            gesture.SourceWindow, bounds, gesture.End, "UI Automation", DateTimeOffset.Now);
    }

    private async Task<SelectionSnapshot?> CaptureClipboardAsync(SelectionGesture gesture, string application, IntPtr nativeFocus, CancellationToken token)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return null;
        await _clipboardGate.WaitAsync(token).ConfigureAwait(false);
        ClipboardBackup? backup = null;
        uint copiedSequence = 0;
        bool injected = false;
        try
        {
            var releaseBudget = Stopwatch.StartNew();
            while (AnyModifierPressed())
            {
                if (releaseBudget.ElapsedMilliseconds >= 800 || !HasCopyFocus(gesture, nativeFocus)) return null;
                await Task.Delay(20, token).ConfigureAwait(false);
            }
            if (!HasCopyFocus(gesture, nativeFocus) || _disposed) { DiagnosticLog.Write("Capture: copy focus changed"); return null; }
            backup = await OnDispatcher(dispatcher, BackupClipboard, token).ConfigureAwait(false);
            if (backup is null) DiagnosticLog.Write("Capture: clipboard backup unavailable");
            if (backup is null || !HasCopyFocus(gesture, nativeFocus) || AnyModifierPressed() ||
                Win32.GetClipboardSequenceNumber() != backup.Sequence) return null;
            token.ThrowIfCancellationRequested();
            var inputs = new[] { Key(0x11, false), Key(0x43, false), Key(0x43, true), Key(0x11, true) };
            injected = true;
            uint inserted = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.Input>());
            if (inserted != inputs.Length)
            {
                DiagnosticLog.Write("Capture: copy key injection incomplete");
                if (inserted > 0)
                {
                    var release = new[] { Key(0x43, true), Key(0x11, true) };
                    Win32.SendInput((uint)release.Length, release, Marshal.SizeOf<Win32.Input>());
                }
                return null;
            }
            var copyBudget = Stopwatch.StartNew();
            while (copyBudget.ElapsedMilliseconds < 1000)
            {
                token.ThrowIfCancellationRequested();
                if (!HasCopyResultFocus(gesture, nativeFocus, application) || _disposed) { DiagnosticLog.Write($"Capture: result focus changed; sameWindow={HasSourceFocus(gesture)}"); return null; }
                if (!HasSourceFocus(gesture))
                {
                    // Foxit's short-lived owned window must close before showing a result.
                    await Task.Delay(25, token).ConfigureAwait(false);
                    continue;
                }
                uint sequence = Win32.GetClipboardSequenceNumber();
                if (copiedSequence != 0 && sequence != copiedSequence) return null;
                if (sequence != backup.Sequence)
                {
                    if (!ClipboardBelongsToSource(gesture.SourceWindow)) { DiagnosticLog.Write("Capture: clipboard owner differs from source"); return null; }
                    copiedSequence = sequence;
                    string? text = null;
                    try
                    {
                        text = await OnDispatcher(dispatcher, () =>
                        {
                            if (Win32.GetClipboardSequenceNumber() != sequence) return null;
                            return Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
                        }, token).ConfigureAwait(false);
                    }
                    catch (ExternalException) { }
                    if (Win32.GetClipboardSequenceNumber() != sequence || !HasCopyResultFocus(gesture, nativeFocus, application)) return null;
                    if (!HasSourceFocus(gesture))
                    {
                        await Task.Delay(25, token).ConfigureAwait(false);
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        text = text.Trim();
                        if (text.Length > MaxTextLength) return null;
                        DiagnosticLog.Write("Capture: clipboard selection captured");
                        return new SelectionSnapshot(text, application, gesture.SourceWindow,
                            new Rect(gesture.End.X, gesture.End.Y, 1, 1), gesture.End, "Clipboard", DateTimeOffset.Now);
                    }
                }
                await Task.Delay(25, token).ConfigureAwait(false);
            }
            DiagnosticLog.Write("Capture: copy produced no readable text before timeout");
            return null;
        }
        catch (ExternalException error)
        {
            Trace.TraceWarning("Clipboard capture: {0}", error.Message);
            return null;
        }
        finally
        {
            try
            {
                // Restore even after cancellation, but never overwrite a later copy made by the user or another app.
                if (injected && backup is not null && !dispatcher.HasShutdownStarted)
                {
                    if (copiedSequence != 0)
                    {
                        uint expected = copiedSequence;
                        await OnDispatcher(dispatcher, () =>
                        {
                            if (Win32.GetClipboardSequenceNumber() != expected || !ClipboardBelongsToSource(gesture.SourceWindow)) return false;
                            if (backup.Empty) Clipboard.Clear();
                            else Clipboard.SetDataObject(backup.Data, true);
                            return true;
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error) { Trace.TraceWarning("Clipboard restore: {0}", error.Message); }
            _clipboardGate.Release();
        }
    }

    private static ClipboardBackup? BackupClipboard()
    {
        uint sequence = Win32.GetClipboardSequenceNumber();
        var original = Clipboard.GetDataObject();
        var snapshot = new DataObject();
        bool empty = original is null;
        int saved = 0;
        long copiedBytes = 0;
        if (original is not null)
        {
            var formats = original.GetFormats(false);
            empty = formats.Length == 0;
            if (formats.Length > 128) return null;
            foreach (string format in formats)
            {
                try
                {
                    object? source = original.GetData(format, false);
                    object? value = CloneClipboardValue(source, ref copiedBytes);
                    if (value is null)
                    {
                        Trace.TraceWarning("Clipboard copy skipped: format {0}, type {1} cannot be backed up safely.",
                            format, source?.GetType().FullName ?? "null");
                        return null;
                    }
                    snapshot.SetData(format, value, false);
                    saved++;
                }
                catch (Exception error) when (error is ExternalException or IOException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException)
                {
                    Trace.TraceWarning("Clipboard backup format {0}: {1}", format, error.Message);
                    return null;
                }
            }
        }
        if ((!empty && saved == 0) || Win32.GetClipboardSequenceNumber() != sequence) return null;
        return new ClipboardBackup(snapshot, sequence, empty);
    }

    private static object? CloneClipboardValue(object? value, ref long copiedBytes)
    {
        if (value is null) return null;
        if (value is string text)
        {
            if (!ReserveClipboardBytes(checked(text.Length * 2L), ref copiedBytes)) return null;
            return text;
        }
        if (value.GetType().IsValueType)
        {
            if (!ReserveClipboardBytes(64, ref copiedBytes)) return null;
            return value;
        }
        if (value is BitmapSource bitmap)
        {
            if (!ReserveClipboardBytes(checked(bitmap.PixelWidth * (long)bitmap.PixelHeight * 8), ref copiedBytes)) return null;
            var clone = bitmap.CloneCurrentValue();
            if (!clone.CanFreeze) return null;
            clone.Freeze();
            return clone;
        }
        if (value is string[] strings)
        {
            long size = checked(strings.LongLength * IntPtr.Size);
            foreach (string? entry in strings) size = checked(size + (entry?.Length ?? 0) * 2L);
            if (!ReserveClipboardBytes(size, ref copiedBytes)) return null;
            return strings.Clone();
        }
        if (value is Array array && array.GetType().GetElementType()?.IsPrimitive == true)
        {
            if (!ReserveClipboardBytes(Buffer.ByteLength(array), ref copiedBytes)) return null;
            return array.Clone();
        }
        if (value is Stream stream)
        {
            if (!stream.CanSeek) return null;
            long length = stream.Length;
            if (!ReserveClipboardBytes(length, ref copiedBytes)) return null;
            long position = stream.Position;
            try
            {
                stream.Position = 0;
                var copy = new MemoryStream((int)length);
                var buffer = new byte[8192];
                long remaining = length;
                while (remaining > 0)
                {
                    int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count <= 0) { copy.Dispose(); return null; }
                    copy.Write(buffer, 0, count);
                    remaining -= count;
                }
                // A changing or unusual source stream must not bypass the 64MB snapshot cap.
                if (stream.ReadByte() != -1) { copy.Dispose(); return null; }
                copy.Position = 0;
                return copy;
            }
            finally { stream.Position = position; }
        }
        if (value is System.Drawing.Image image)
        {
            if (!ReserveClipboardBytes(checked(image.Width * (long)image.Height * 8), ref copiedBytes)) return null;
            return image.Clone();
        }
        // Private COM/owner-rendered formats cannot be safely kept after the owner changes.
        return null;
    }

    private static bool ReserveClipboardBytes(long bytes, ref long total)
    {
        if (bytes < 0 || bytes > MaxClipboardBytes - total) return false;
        total += bytes;
        return true;
    }

    private static Task<T> OnDispatcher<T>(Dispatcher dispatcher, Func<T> action, CancellationToken token)
        => dispatcher.InvokeAsync(action, DispatcherPriority.Normal, token).Task;

    private static bool ClipboardBelongsToSource(IntPtr source)
    {
        var owner = Win32.GetClipboardOwner();
        return owner != IntPtr.Zero && DesktopInterop.GetProcessId(owner) == DesktopInterop.GetProcessId(source);
    }
    private static bool AnyModifierPressed()
    {
        foreach (int key in ModifierKeys) if ((Win32.GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }
    private static Win32.Input Key(ushort key, bool release) => new()
    {
        Type = 1,
        Data = new Win32.InputUnion { Keyboard = new Win32.KeyboardInput { VirtualKey = key, Flags = release ? 2u : 0u } }
    };
    private static bool HasSourceFocus(SelectionGesture gesture)
        => gesture.SourceWindow != IntPtr.Zero && DesktopInterop.ForegroundWindow == gesture.SourceWindow;
    private static bool HasCopyFocus(SelectionGesture gesture, IntPtr nativeFocus)
        => HasSourceFocus(gesture) && GetFocusedWindow(gesture.SourceWindow) == nativeFocus;
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    private static bool HasCopyResultFocus(SelectionGesture gesture, IntPtr nativeFocus, string application)
    {
        bool foxit = application.Equals("FoxitPDFEditor", StringComparison.OrdinalIgnoreCase);
        if (HasSourceFocus(gesture))
            return foxit || GetFocusedWindow(gesture.SourceWindow) == nativeFocus;
        if (!foxit) return false;
        // Ctrl+C temporarily activates a Foxit-owned window. Wait only within the
        // original copy deadline, never for an unrelated document or application.
        IntPtr window = DesktopInterop.ForegroundWindow;
        uint sourceProcess = DesktopInterop.GetProcessId(gesture.SourceWindow);
        if (window == IntPtr.Zero || sourceProcess == 0) return false;
        for (int depth = 0; depth < 8 && window != IntPtr.Zero; depth++)
        {
            if (DesktopInterop.GetProcessId(window) != sourceProcess) return false;
            window = GetWindow(window, 4); // GW_OWNER
            if (window == gesture.SourceWindow) return true;
        }
        return false;
    }

    private static IntPtr GetFocusedWindow(IntPtr source)
    {
        uint threadId = Win32.GetWindowThreadProcessId(source, out _);
        if (threadId == 0) return IntPtr.Zero;
        var info = new Win32.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Win32.GuiThreadInfo>() };
        return Win32.GetGUIThreadInfo(threadId, ref info) ? info.Focus : IntPtr.Zero;
    }
    private bool IsCurrent(AutomationRequest request)
        => !_disposed && !request.IsExpired && !request.Token.IsCancellationRequested && HasSourceFocus(request.Gesture);
    private static bool IsAutomationError(Exception error)
        => error is ElementNotAvailableException or InvalidOperationException or ExternalException or ArgumentException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active?.Expire();
        _requests.CompleteAdding();
        while (_requests.TryTake(out var request)) request.Expire();
        // A provider may still be blocked inside COM. Do not join or abort that thread on application exit.
    }

    private enum ClipboardSafety { Unknown, Safe, Denied }
    private sealed class AutomationRequest
    {
        private int _expired;
        private int _safety;
        internal SelectionGesture Gesture { get; }
        internal bool AllowClipboard { get; }
        internal CancellationToken Token { get; }
        internal TaskCompletionSource<SelectionSnapshot?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsExpired => Volatile.Read(ref _expired) != 0;
        internal ClipboardSafety ClipboardSafety { get => (ClipboardSafety)Volatile.Read(ref _safety); set => Volatile.Write(ref _safety, (int)value); }
        internal AutomationRequest(SelectionGesture gesture, CancellationToken token, bool allowClipboard) { Gesture = gesture; Token = token; AllowClipboard = allowClipboard; }
        internal void Expire() { Volatile.Write(ref _expired, 1); Completion.TrySetResult(null); }
    }
    private sealed record ClipboardBackup(DataObject Data, uint Sequence, bool Empty);
}
