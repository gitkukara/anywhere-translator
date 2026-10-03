using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Native;
using TranslatorAnywhere.Services;
using TranslatorAnywhere.Views;
using Forms = System.Windows.Forms;

namespace TranslatorAnywhere;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private AppSettings _settings = new();
    private SettingsStore _store = null!;
    private TranslationService _translationService = null!;
    private SelectionCapture _capture = null!;
    private MouseMonitor _mouse = null!;
    private GlobalShortcut? _shortcut;
    private SelectionButtonWindow _button = null!;
    private TranslationWindow _translation = null!;
    private SettingsWindow? _settingsWindow;
    private Window? _messageWindow;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _pauseMenu;
    private CancellationTokenSource? _selectionCancellation;
    private readonly DispatcherTimer _buttonTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTimeOffset _buttonShown;
    private int _requestGeneration;
    private string _startupWarning = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticLog.Write("UI operation failed", args.Exception);
            args.Handled = true;
            _settingsWindow?.SetStatus("操作未完成，请重试。详细信息已写入本地诊断日志。");
        };
        if (e.Args.Contains("--fixture", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new SelectionFixtureWindow();
            MainWindow.Show();
            return;
        }
        bool background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        DiagnosticLog.Write(background ? "Application launch requested (background)" : "Application launch requested (interactive)");
        _mutex = new Mutex(true, "Local\\TranslatorAnywhere.Desktop.v1", out _ownsMutex);
        if (!_ownsMutex)
        {
            DiagnosticLog.Write(background ? "Application duplicate instance exited (background)" : "Application duplicate instance exited (interactive)");
            if (!background)
                MessageBox.Show("划词翻译已经在运行，请从任务栏托盘打开设置。", "Anywhere Translator");
            Shutdown(); return;
        }
        _store = new SettingsStore();
        _settings = _store.Load();
        var startup = StartupRegistration.GetStatus();
        if (_settings.LaunchAtStartup && startup.ReadSucceeded && startup.RequiresMigration)
        {
            var migration = StartupRegistration.SetEnabled(true);
            startup = StartupRegistration.GetStatus();
            if (!migration.Success) _startupWarning = "开机启动登记更新失败，请关闭后重新开启。";
        }
        if (startup.ReadSucceeded) _settings.LaunchAtStartup = startup.IsEnabledForCurrentExecutable;
        if ((!startup.ReadSucceeded || startup.HasRegistration && !startup.IsEnabledForCurrentExecutable) && string.IsNullOrEmpty(_startupWarning))
            _startupWarning = startup.Message;
        Resources["AppIcon"] = AppIconService.LoadWindowIcon(_store.DataDirectory);
        _translationService = new TranslationService();
        _capture = new SelectionCapture();
        _button = new SelectionButtonWindow();
        _translation = new TranslationWindow(_translationService, () => _settings, ReadApiKeySafely);
        _translation.SettingsRequested += ShowSettings;
        _translation.PinnedChanged += pinned =>
        {
            _settings.TranslationPinned = pinned;
            try { _store.Save(_settings); }
            catch (Exception ex) { DiagnosticLog.Write("Pin preference save failed", ex); }
        };
        _button.TranslationRequested += () =>
        {
            var selection = _button.Selection;
            HideButton();
            if (selection is not null) ShowTranslation(selection);
        };
        _messageWindow = new Window { Width = 0, Height = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Visibility = Visibility.Hidden };
        new System.Windows.Interop.WindowInteropHelper(_messageWindow).EnsureHandle();
        _shortcut = new GlobalShortcut(_messageWindow);
        _shortcut.Pressed += () => Dispatcher.BeginInvoke(() => CaptureHotkey());
        if (!_shortcut.Register())
        {
            const string shortcutWarning = "Ctrl+Alt+T 已被其他软件占用，拖选翻译仍可使用。";
            _startupWarning = string.IsNullOrEmpty(_startupWarning) ? shortcutWarning : _startupWarning + " " + shortcutWarning;
        }
        _mouse = new MouseMonitor();
        _mouse.SelectionFinished += gesture => Dispatcher.BeginInvoke(() => CaptureGesture(gesture, false));
        _mouse.PointerPressed += point => Dispatcher.BeginInvoke(() =>
        {
            // Keep the cached selection alive for the non-activating button's click.
            if (_button.IsVisible && DesktopInterop.GetWindowBounds(_button).Contains(point)) return;
            HideButton();
            InvalidateSelection();
        });
        _mouse.DismissRequested += () => Dispatcher.BeginInvoke(() => { HideButton(); InvalidateSelection(); });
        _buttonTimer.Tick += (_, _) =>
        {
            if (!_button.IsVisible) return;
            var selection = _button.Selection;
            if (DateTimeOffset.Now - _buttonShown > TimeSpan.FromSeconds(_settings.AutoHideSeconds)
                || (selection is not null && selection.SourceWindow != IntPtr.Zero
                    && DesktopInterop.ForegroundWindow != selection.SourceWindow
                    && !DesktopInterop.IsOwnWindow(DesktopInterop.ForegroundWindow)))
                HideButton();
        };
        BuildTray();
        _mouse.Start();
        _buttonTimer.Start();
        if (!background) ShowSettings();
        DiagnosticLog.Write(background ? "Application started (background)" : "Application started (interactive)");
    }

    private void CaptureHotkey()
    {
        var foreground = DesktopInterop.ForegroundWindow;
        if (DesktopInterop.IsOwnWindow(foreground)) return;
        var point = DesktopInterop.Cursor;
        CaptureGesture(new SelectionGesture(foreground, point, point, false), true);
    }

    private async void CaptureGesture(SelectionGesture gesture, bool fromHotkey)
    {
        if ((!_settings.Enabled && !fromHotkey) || DesktopInterop.IsOwnWindow(gesture.SourceWindow)) return;
        var appName = DesktopInterop.GetApplicationName(gesture.SourceWindow);
        if (_settings.ExcludedApplications.Any(name => AppNameEquals(name, appName))) return;
        InvalidateSelection();
        var generation = _requestGeneration;
        var cancellation = _selectionCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        try
        {
            await Task.Delay(fromHotkey ? 50 : Math.Clamp(_settings.SelectionDelayMs, 50, 1500), token);
            var allowClipboard = fromHotkey ? _settings.ClipboardFallbackOnHotkey
                : _settings.AutomaticClipboardFallback && _settings.ClipboardFallbackApplications.Any(name => AppNameEquals(name, appName));
            var selection = await _capture.CaptureAsync(gesture, allowClipboard, token);
            if (generation != _requestGeneration || cancellation.IsCancellationRequested) return;
            if (selection is null || string.IsNullOrWhiteSpace(selection.Text)) return;
            if (DesktopInterop.ForegroundWindow != gesture.SourceWindow) return;
            if (fromHotkey) ShowTranslation(selection);
            else { _button.ShowFor(selection, _settings); _buttonShown = DateTimeOffset.Now; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Selection capture failed", ex);
        }
    }

    private static bool AppNameEquals(string configured, string actual) =>
        string.Equals(Path.GetFileNameWithoutExtension(configured.Trim()), Path.GetFileNameWithoutExtension(actual), StringComparison.OrdinalIgnoreCase);

    private void InvalidateSelection()
    {
        ++_requestGeneration;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
    }

    private void HideButton() { if (_button?.IsVisible == true) _button.Hide(); }

    private async void ShowTranslation(SelectionSnapshot selection)
    {
        _translation.ShowNearSelection(selection);
        await _translation.TranslateSelectionAsync();
    }

    private void ShowSettings()
    {
        HideButton();
        if (_settingsWindow is not null) { _settingsWindow.Show(); _settingsWindow.Activate(); return; }
        var startup = StartupRegistration.GetStatus();
        if (startup.ReadSucceeded) _settings.LaunchAtStartup = startup.IsEnabledForCurrentExecutable;
        if ((!startup.ReadSucceeded || startup.HasRegistration && !startup.IsEnabledForCurrentExecutable) && string.IsNullOrEmpty(_startupWarning))
            _startupWarning = startup.Message;
        var window = new SettingsWindow(_settings, _store.ReadProviderApiKey);
        _settingsWindow = window;
        window.ImportIcon = _store.ImportIcon;
        window.SettingsSaved += (settings, pendingApiKeys) =>
        {
            try
            {
                settings.TranslationPinned = _translation.Topmost;
                bool previousStartup = _settings.LaunchAtStartup;
                bool startupChanged = settings.LaunchAtStartup != previousStartup;
                bool translationChanged = TranslationConfiguration(settings) != TranslationConfiguration(_settings)
                    || settings.ActiveProviderId is Guid activeId && pendingApiKeys.ContainsKey(activeId);
                _store.SaveConfiguration(settings, pendingApiKeys);
                _settings = settings;
                if (translationChanged) _translation.Cancel();
                UpdateTray();
                InvalidateSelection(); HideButton();
                // Autosaving appearance/text fields never rewrites the login registration.
                var startupResult = !startupChanged
                    ? new StartupRegistrationResult(true, "")
                    : StartupRegistration.SetEnabled(settings.LaunchAtStartup);
                if (!startupResult.Success)
                {
                    var actual = StartupRegistration.GetStatus();
                    _settings.LaunchAtStartup = actual.ReadSucceeded ? actual.IsEnabledForCurrentExecutable : previousStartup;
                    window.SetStartupState(_settings.LaunchAtStartup);
                    _store.Save(_settings);
                    window.SetStatus(startupResult.Message);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("Settings save failed", ex);
                throw;
            }
        };
        window.TestSelectionRequested += () =>
        {
            try
            {
                var path = Environment.ProcessPath!;
                var start = new ProcessStartInfo(path) { UseShellExecute = true };
                start.ArgumentList.Add("--fixture");
                Process.Start(start);
            }
            catch (Exception ex) { DiagnosticLog.Write("Test window launch failed", ex); window.SetStatus("测试窗口未打开，请直接在其他软件中拖选文字。"); }
        };
        window.Closed += (_, _) => { _settingsWindow = null; HideButton(); };
        if (!string.IsNullOrEmpty(_startupWarning)) window.SetStatus(_startupWarning);
        _startupWarning = "";
        window.Show();
        window.Activate();
    }

    private static string TranslationConfiguration(AppSettings settings)
    {
        var provider = settings.Providers.FirstOrDefault(item => item.Id == settings.ActiveProviderId);
        return JsonSerializer.Serialize(new
        {
            settings.TargetLanguage, settings.ActiveProviderId,
            provider?.BaseUrl, provider?.Protocol, provider?.SelectedModel,
            provider?.Enabled, provider?.RequiresApiKey, provider?.DisableThinking
        });
    }

    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => Dispatcher.BeginInvoke(ShowSettings));
        _pauseMenu = new Forms.ToolStripMenuItem("暂停划词", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _settings.Enabled = !_settings.Enabled;
            try { _store.Save(_settings); } catch (Exception ex) { DiagnosticLog.Write("Pause preference save failed", ex); }
            _settingsWindow?.SetEnabledState(_settings.Enabled);
            InvalidateSelection(); HideButton(); UpdateTray();
        }));
        menu.Items.Add(_pauseMenu);
        menu.Items.Add("翻译剪贴板文字", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!Clipboard.ContainsText()) { ShowSettings(); _settingsWindow?.SetStatus("剪贴板中没有文字，请先复制需要翻译的内容。"); return; }
                var point = DesktopInterop.Cursor;
                ShowTranslation(new SelectionSnapshot(Clipboard.GetText(), "剪贴板", IntPtr.Zero, Rect.Empty, point, "剪贴板", DateTimeOffset.Now));
            }
            catch (Exception ex) { DiagnosticLog.Write("Clipboard read failed", ex); }
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_settingsWindow?.FlushPendingChanges() == false) { ShowSettings(); return; }
            Shutdown();
        }));
        _tray = new Forms.NotifyIcon { Text = "Anywhere Translator", Icon = AppIconService.LoadTrayIcon(_store.DataDirectory), ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowSettings);
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_pauseMenu is not null) _pauseMenu.Text = _settings.Enabled ? "暂停划词" : "恢复划词";
        if (_tray is not null) _tray.Text = _settings.Enabled ? "Anywhere Translator - 划词已启用" : "Anywhere Translator - 划词已暂停";
    }

    private string ReadApiKeySafely(Guid providerId)
    {
        try { return _store.ReadProviderApiKey(providerId); }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Stored API credential could not be read", ex);
            return "";
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _buttonTimer.Stop();
        InvalidateSelection();
        _mouse?.Dispose();
        _shortcut?.Dispose();
        _capture?.Dispose();
        _translation?.Cancel();
        _translationService?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Icon?.Dispose(); _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
