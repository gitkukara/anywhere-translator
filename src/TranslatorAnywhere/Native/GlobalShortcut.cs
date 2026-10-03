using System;
using System.Windows;
using System.Windows.Interop;

namespace TranslatorAnywhere.Native;

public sealed class GlobalShortcut : IDisposable
{
    private const int HotkeyId = 0x54A1;
    private readonly Window _host;
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public event Action? Pressed;

    public GlobalShortcut(Window host)
    {
        _host = host;
        _handle = new WindowInteropHelper(host).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("The shortcut host has no native window.");
        _source.AddHook(OnMessage);
    }

    public bool Register()
    {
        _host.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered) return true;
        _registered = Win32.RegisterHotKey(_handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, 0x54); // Ctrl+Alt+T, MOD_NOREPEAT.
        return _registered;
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Win32.WmHotkey && wParam.ToInt32() == HotkeyId && !_disposed)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered) Win32.UnregisterHotKey(_handle, HotkeyId);
        _source.RemoveHook(OnMessage);
    }
}
