using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TranslatorAnywhere.Native;

public static class DesktopInterop
{
    private static readonly ConditionalWeakTable<Window, object> NonActivatingWindows = new();

    public static IntPtr ForegroundWindow => Win32.GetForegroundWindow();
    public static Point Cursor => Win32.GetCursorPos(out var point) ? new Point(point.X, point.Y) : new Point();

    public static string GetApplicationName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        Win32.GetWindowThreadProcessId(hwnd, out var processId);
        try { using var process = Process.GetProcessById((int)processId); return process.ProcessName; }
        catch (ArgumentException) { return ""; }
        catch (InvalidOperationException) { return ""; }
        catch (System.ComponentModel.Win32Exception) { return ""; }
    }

    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    public static Rect GetWorkArea(Point point)
    {
        var monitor = Win32.MonitorFromPoint(ToNative(point), 2);
        var info = new Win32.MonitorInfo { Size = (uint)Marshal.SizeOf<Win32.MonitorInfo>() };
        if (Win32.GetMonitorInfo(monitor, ref info)) return ToRect(info.Work);
        return new Rect(0, 0, Win32.GetSystemMetrics(0), Win32.GetSystemMetrics(1));
    }

    public static double GetDpiScale(Point point)
    {
        var monitor = Win32.MonitorFromPoint(ToNative(point), 2);
        try
        {
            if (Win32.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) return dpi / 96d;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1;
    }

    public static void PlaceWindow(Window window, Point physicalPosition)
    {
        window.Dispatcher.VerifyAccess();
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var scale = GetDpiScale(physicalPosition);
        var width = Math.Max(1, Dimension(window.ActualWidth, window.Width) * scale);
        var height = Math.Max(1, Dimension(window.ActualHeight, window.Height) * scale);
        var work = GetWorkArea(physicalPosition);
        var x = Math.Clamp(physicalPosition.X, work.Left, Math.Max(work.Left, work.Right - width));
        var y = Math.Clamp(physicalPosition.Y, work.Top, Math.Max(work.Top, work.Bottom - height));
        Win32.SetWindowPos(hwnd, window.Topmost ? Win32.HwndTopmost : Win32.HwndNotTopmost, Round(x), Round(y), Round(width), Round(height),
            Win32.SwpNoActivate | Win32.SwpNoOwnerZOrder);
    }

    public static Rect GetWindowBounds(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && Win32.GetWindowRect(hwnd, out var rect) ? ToRect(rect) : Rect.Empty;
    }

    public static void ClampWindowToWorkArea(Window window)
    {
        window.Dispatcher.VerifyAccess();
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var nativeBounds)) return;
        var bounds = ToRect(nativeBounds);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        // Window DPI can belong to a different monitor than its top-left corner. Keep its existing
        // physical size and choose the work area by its center, so content growth cannot rescale it.
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var work = GetWorkArea(center);
        int x = Round(Math.Clamp(bounds.Left, work.Left, Math.Max(work.Left, work.Right - bounds.Width)));
        int y = Round(Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)));
        if (x == nativeBounds.Left && y == nativeBounds.Top) return;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Win32.SwpNoSize | Win32.SwpNoActivate | Win32.SwpNoZOrder | Win32.SwpNoOwnerZOrder);
    }

    public static void MakeNonActivating(Window window)
    {
        window.Dispatcher.VerifyAccess();
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var styles = Win32.GetWindowLongPtr(hwnd, -20).ToInt64();
        Win32.SetWindowLongPtr(hwnd, -20, new IntPtr(styles | 0x08000000L | 0x00000080L));
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoZOrder | Win32.SwpNoActivate | Win32.SwpFrameChanged);
        if (!NonActivatingWindows.TryGetValue(window, out _))
        {
            HwndSource.FromHwnd(hwnd)?.AddHook(PreventActivation);
            NonActivatingWindows.Add(window, new object());
        }
    }

    internal static uint GetProcessId(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var id);
        return id;
    }
    internal static IntPtr WindowAt(Point point) => Win32.WindowFromPoint(ToNative(point));
    internal static Win32.NativePoint ToNative(Point point) => new() { X = Round(point.X), Y = Round(point.Y) };
    private static int Round(double value) => (int)Math.Round(Math.Clamp(value, int.MinValue, int.MaxValue));
    private static double Dimension(double actual, double declared) => double.IsFinite(actual) && actual > 0 ? actual : double.IsFinite(declared) && declared > 0 ? declared : 1;
    private static Rect ToRect(Win32.NativeRect rect) => new(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    private static IntPtr PreventActivation(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != Win32.WmMouseActivate) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE: allow the click, keep the source application's selection.
    }
}
