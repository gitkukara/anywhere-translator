using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using TranslatorAnywhere.Models;

namespace TranslatorAnywhere.Native;

public sealed class MouseMonitor : IDisposable
{
    private readonly BlockingCollection<MouseEvent> _events = new(128);
    private readonly ManualResetEventSlim _ready = new();
    private readonly Win32.HookProc _callback;
    private Thread? _hookThread;
    private Thread? _gestureThread;
    private IntPtr _hook;
    private uint _threadId;
    private Exception? _startupError;
    private volatile bool _disposed;
    private bool _started;

    public event Action<SelectionGesture>? SelectionFinished;
    public event Action<Point>? PointerPressed;
    public event Action? DismissRequested;

    public MouseMonitor() => _callback = OnHook;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        _gestureThread = new Thread(ProcessEvents) { Name = "Selection gesture monitor", IsBackground = true };
        _hookThread = new Thread(RunHook) { Name = "Global mouse hook", IsBackground = true };
        _gestureThread.Start();
        _hookThread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("The global mouse hook did not start.");
        if (_startupError is not null) throw new InvalidOperationException("Cannot install the global mouse hook.", _startupError);
    }

    private void RunHook()
    {
        _threadId = Win32.GetCurrentThreadId();
        // Create the message queue before another thread can post WM_QUIT.
        Win32.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        try
        {
            _hook = Win32.SetWindowsHookEx(14, _callback, Win32.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.Set();
            while (!_disposed && Win32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        catch (Exception error) { _startupError = error; _ready.Set(); }
        finally
        {
            if (_hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _events.CompleteAdding();
        }
    }

    private IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !_disposed)
        {
            var message = wParam.ToInt32();
            if (message is Win32.WmLeftDown or Win32.WmLeftUp or Win32.WmRightDown or Win32.WmMiddleDown or Win32.WmXDown or Win32.WmMouseWheel or Win32.WmMouseHWheel)
            {
                var data = Marshal.PtrToStructure<Win32.MouseHookData>(lParam);
                // LButtonDown precedes the target's activation. Capture its root HWND under the pointer,
                // rather than the previous foreground window. Synthetic/accessibility input is supported.
                // The hook performs only cheap HWND queries and enqueues; never UIA or callbacks.
                var source = message == Win32.WmLeftDown
                    ? Win32.GetAncestor(Win32.WindowFromPoint(data.Point), 2)
                    : Win32.GetForegroundWindow();
                try { _events.TryAdd(new MouseEvent(message, new Point(data.Point.X, data.Point.Y), data.Time, source)); }
                catch (InvalidOperationException) { }
            }
        }
        return Win32.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void ProcessEvents()
    {
        MouseEvent? down = null;
        MouseEvent? lastDown = null;
        bool doubleClick = false;
        var doubleClickTime = Win32.GetDoubleClickTime();
        var clickWidth = Math.Max(4, Win32.GetSystemMetrics(36));
        var clickHeight = Math.Max(4, Win32.GetSystemMetrics(37));
        var dragWidth = Math.Max(4, Win32.GetSystemMetrics(68));
        var dragHeight = Math.Max(4, Win32.GetSystemMetrics(69));
        foreach (var item in _events.GetConsumingEnumerable())
        {
            if (_disposed) break;
            try
            {
                bool ownPointer = DesktopInterop.IsOwnWindow(DesktopInterop.WindowAt(item.Point));
                if (item.Message is Win32.WmMouseWheel or Win32.WmMouseHWheel)
                {
                    down = null;
                    if (!ownPointer) DismissRequested?.Invoke();
                    continue;
                }
                if (item.Message == Win32.WmLeftDown)
                {
                    if (ownPointer || DesktopInterop.IsOwnWindow(item.Foreground)) { down = null; lastDown = null; continue; }
                    PointerPressed?.Invoke(item.Point);
                    doubleClick = lastDown is { } previous && previous.Foreground == item.Foreground &&
                        unchecked(item.Time - previous.Time) <= doubleClickTime &&
                        Math.Abs(item.Point.X - previous.Point.X) <= clickWidth / 2d && Math.Abs(item.Point.Y - previous.Point.Y) <= clickHeight / 2d;
                    down = item;
                    lastDown = item;
                }
                else if (item.Message == Win32.WmLeftUp)
                {
                    if (down is not { } start) continue;
                    down = null;
                    bool dragged = Math.Abs(item.Point.X - start.Point.X) >= dragWidth || Math.Abs(item.Point.Y - start.Point.Y) >= dragHeight;
                    if ((dragged || doubleClick) && start.Foreground != IntPtr.Zero && start.Foreground == item.Foreground && !ownPointer)
                        SelectionFinished?.Invoke(new SelectionGesture(start.Foreground, start.Point, item.Point, doubleClick));
                }
                else
                {
                    down = null;
                    lastDown = null;
                    if (!ownPointer && !DesktopInterop.IsOwnWindow(item.Foreground)) PointerPressed?.Invoke(item.Point);
                }
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Mouse gesture callback: {0}", error.Message); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WmQuit, UIntPtr.Zero, IntPtr.Zero);
        _events.CompleteAdding();
        // Both threads are background threads. Never block the UI while waiting for a native callback to finish.
    }

    private readonly record struct MouseEvent(int Message, Point Point, uint Time, IntPtr Foreground);
}
