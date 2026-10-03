using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TranslatorAnywhere.Models;

namespace TranslatorAnywhere.Services;

/// <summary>Owns the application palette. It never changes the Windows theme.</summary>
public sealed class ThemeService : IDisposable
{
    private readonly Application _application;
    private readonly Func<bool> _readSystemDark;
    private ResourceDictionary? _palette;
    private bool _disposed;
    public AppTheme Mode { get; private set; } = AppTheme.System;
    public bool IsDark { get; private set; }
    public event Action? Changed;

    public ThemeService(Application application, Func<bool>? readSystemDark = null)
    {
        _application = application;
        _readSystemDark = readSystemDark ?? ReadSystemDark;
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
    }

    public void Apply(AppTheme mode)
    {
        _application.Dispatcher.VerifyAccess();
        if (_disposed) return;
        Mode = Enum.IsDefined(mode) ? mode : AppTheme.System;
        bool dark = Mode == AppTheme.Dark || Mode == AppTheme.System && _readSystemDark();
        if (_palette is not null && IsDark == dark) return;
        var palette = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/TranslatorAnywhere;component/Views/Themes/" + (dark ? "Dark" : "Light") + ".xaml")
        };
        if (_palette is not null) _application.Resources.MergedDictionaries.Remove(_palette);
        _application.Resources.MergedDictionaries.Add(palette);
        _palette = palette;
        IsDark = dark;
        _application.Resources["IsDarkTheme"] = dark;
        foreach (Window window in _application.Windows) UpdateWindowFrame(window);
        Changed?.Invoke();
    }

    public void RefreshSystemTheme()
    {
        if (_disposed || _application.Dispatcher.HasShutdownStarted) return;
        if (!_application.Dispatcher.CheckAccess())
        {
            _application.Dispatcher.BeginInvoke(new Action(RefreshSystemTheme));
            return;
        }
        if (Mode == AppTheme.System) Apply(Mode);
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => RefreshSystemTheme();

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // Follow the Windows application mode, which can differ from the taskbar mode.
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            DiagnosticLog.Write("Windows app theme could not be read; using light mode", ex);
            return false;
        }
    }

    public static void TrackWindow(Window window)
    {
        window.SourceInitialized += (_, _) => UpdateWindowFrame(window);
    }

    private static void UpdateWindowFrame(Window window)
    {
        if (window.WindowStyle == WindowStyle.None) return;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || Application.Current is not { } application) return;
        uint dark = application.Resources["IsDarkTheme"] is true ? 1u : 0u;
        try
        {
            // Unsupported attributes are ignored by DWM on older Windows versions.
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(uint));
            if (application.TryFindResource("BackgroundBrush") is SolidColorBrush background)
            {
                uint caption = ColorRef(background.Color);
                DwmSetWindowAttribute(handle, 35, ref caption, sizeof(uint));
            }
            if (application.TryFindResource("InkBrush") is SolidColorBrush foreground)
            {
                uint text = ColorRef(foreground.Color);
                DwmSetWindowAttribute(handle, 36, ref text, sizeof(uint));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The content palette still works without native title-bar support.
        }
    }

    private static uint ColorRef(Color color) => (uint)(color.R | color.G << 8 | color.B << 16);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
    }
}