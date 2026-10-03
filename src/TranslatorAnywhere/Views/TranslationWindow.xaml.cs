using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Native;
using TranslatorAnywhere.Services;

namespace TranslatorAnywhere.Views;

public partial class TranslationWindow : Window
{
    private readonly TranslationService _service;
    private readonly Func<AppSettings> _settings;
    private readonly Func<Guid, string> _apiKey;
    private SelectionSnapshot? _selection;
    private CancellationTokenSource? _requestCancellation;
    private long _requestVersion;
    private bool _busy;
    private bool _positioning;
    private bool _clampPending;
    private string _requestSource = "";
    private DispatcherTimer? _copyFeedbackTimer;
    private long _copyFeedbackVersion;

    public event Action? SettingsRequested;
    public event Action<bool>? PinnedChanged;

    public TranslationWindow(TranslationService service, Func<AppSettings> settings, Func<Guid, string> apiKey)
    {
        _service = service;
        _settings = settings;
        _apiKey = apiKey;
        InitializeComponent();
        PinnedBox.IsChecked = settings().TranslationPinned;
    }

    // The application positions/shows the window, then calls TranslateSelectionAsync.
    public void ShowSelection(SelectionSnapshot snapshot)
    {
        Cancel();
        ResetCopyFeedback();
        _selection = snapshot;
        _requestSource = "";
        ResultTextBox.Clear();
        LanguageLabel.Text = _settings().TargetLanguage;
        LanguageLabel.ToolTip = _settings().TargetLanguage;
        EmptyResultPanel.Visibility = Visibility.Visible;
        EmptyResultLabel.Text = "准备翻译";
        ConfigureButton.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        SetStatus("准备翻译", false);
    }

    public void ShowNearSelection(SelectionSnapshot snapshot)
    {
        _positioning = true;
        try
        {
            ShowSelection(snapshot);
            var work = DesktopInterop.GetWorkArea(snapshot.Pointer);
            var scale = DesktopInterop.GetDpiScale(snapshot.Pointer);
            Width = Math.Min(400, work.Width / scale);
            MaxHeight = Math.Max(MinHeight, work.Height / scale - 12);
            ResultTextBox.MaxHeight = Math.Max(52, Math.Min(360, MaxHeight - 220));
            Measure(new Size(Width, double.PositiveInfinity));
            var position = new Point(snapshot.Pointer.X + 4 * scale, snapshot.Pointer.Y + 6 * scale);
            // Give the hidden HWND its measured height before placing it on the target monitor.
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            Arrange(new Rect(new Size(Width, DesiredSize.Height)));
            DesktopInterop.PlaceWindow(this, position);
            Show();
            UpdateLayout();
            DesktopInterop.PlaceWindow(this, position);
            Activate();
        }
        finally { _positioning = false; }
    }

    public async Task TranslateSelectionAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(TranslateSelectionAsync).Task.Unwrap();
            return;
        }
        Cancel();
        ResetCopyFeedback();
        var selection = _selection;
        if (selection is null || string.IsNullOrWhiteSpace(selection.Text))
        {
            SetStatus("请先选中需要翻译的文字。", true);
            return;
        }

        string key;
        AppSettings settings;
        ProviderConfiguration provider;
        try
        {
            // An active request keeps the language/provider with which it was started.
            settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings())) ?? new AppSettings();
            provider = ProviderRegistry.ResolveActive(settings);
            key = _apiKey(provider.Id);
        }
        catch (InvalidOperationException ex)
        {
            ShowConfigurationError(ex.Message);
            return;
        }
        catch (Exception)
        {
            ShowConfigurationError("翻译配置无法读取，请打开设置重新保存。");
            return;
        }
        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(key))
        {
            ShowConfigurationError("请先配置 API Key，再翻译选中文字。");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        long version = ++_requestVersion;
        ResultTextBox.Clear();
        ConfigureButton.Visibility = Visibility.Collapsed;
        EmptyResultPanel.Visibility = Visibility.Visible;
        EmptyResultLabel.Text = "正在翻译…";
        LanguageLabel.Text = settings.TargetLanguage;
        SetBusy(true);
        _requestSource = Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var endpoint)
            ? "请求接口：" + endpoint.Host + endpoint.AbsolutePath + "\n请求模型：" + provider.SelectedModel
            : "接口地址无效";
        SetStatus(provider.Name + " · 正在翻译", false);
        try
        {
            await _service.TranslateAsync(provider, key, selection.Text, settings.TargetLanguage, delta =>
            {
                void Append()
                {
                    if (version != _requestVersion || cancellation.IsCancellationRequested) return;
                    ResultTextBox.AppendText(delta);
                    EmptyResultPanel.Visibility = Visibility.Collapsed;
                    CopyButton.IsEnabled = ResultTextBox.Text.Length > 0;
                }
                if (Dispatcher.CheckAccess()) Append();
                else if (!Dispatcher.HasShutdownStarted) Dispatcher.Invoke(Append);
            }, cancellation.Token);
            if (version != _requestVersion) return;
            SetStatus(provider.Name + " · " + provider.SelectedModel, false);
            EmptyResultPanel.Visibility = ResultTextBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            if (ResultTextBox.Text.Length == 0) EmptyResultLabel.Text = "没有收到译文";
        }
        catch (OperationCanceledException)
        {
            if (version == _requestVersion) SetStatus("已停止", false);
        }
        catch (Exception ex)
        {
            if (version != _requestVersion) return;
            string message = ex is InvalidOperationException or ArgumentException or TimeoutException
                ? ex.Message : "翻译未完成，请检查服务配置后重试。";
            SetStatus(message, true);
            if (ResultTextBox.Text.Length == 0)
            {
                EmptyResultPanel.Visibility = Visibility.Visible;
                EmptyResultLabel.Text = "暂时无法翻译";
                ConfigureButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (ReferenceEquals(_requestCancellation, cancellation)) _requestCancellation = null;
            if (version == _requestVersion) SetBusy(false);
            cancellation.Dispose();
        }
    }

    public void Cancel()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Cancel));
            return;
        }
        ++_requestVersion;
        var cancellation = _requestCancellation;
        _requestCancellation = null;
        cancellation?.Cancel();
        bool wasBusy = _busy;
        SetBusy(false);
        if (wasBusy)
        {
            SetStatus("已停止", false);
            if (ResultTextBox.Text.Length == 0) EmptyResultLabel.Text = "翻译已取消";
        }
    }

    private void ShowConfigurationError(string message)
    {
        ResultTextBox.Clear();
        CopyButton.IsEnabled = false;
        SetStatus(message, true);
        EmptyResultPanel.Visibility = Visibility.Visible;
        EmptyResultLabel.Text = "配置翻译服务后即可使用";
        ConfigureButton.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (RequestProgress is null) return;
        RequestProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.IsEnabled = !busy;
        CopyButton.IsEnabled = ResultTextBox.Text.Length > 0;
    }

    private void SetStatus(string text, bool error)
    {
        RequestStatusLabel.Text = error ? "翻译未完成" : text;
        RequestStatusLabel.ToolTip = string.IsNullOrEmpty(_requestSource) ? text : text + "\n" + _requestSource;
        RequestStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
        ErrorDetailLabel.Text = error ? text : "";
        ErrorDetailLabel.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private async void Retry_Click(object sender, RoutedEventArgs e) => await TranslateSelectionAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    public void DismissIfOutside(Point screenPoint)
    {
        if (!IsVisible || PinnedBox.IsChecked == true) return;
        if (!DesktopInterop.GetWindowBounds(this).Contains(screenPoint)) Hide();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons retain their clicks; only empty header space drags the popup.
        if (e.OriginalSource is not System.Windows.Controls.Grid) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible || _positioning || _clampPending) return;
        _clampPending = true;
        // SizeChanged precedes the HWND resize for SizeToContent; clamp once the native bounds catch up.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _clampPending = false;
            if (IsVisible) DesktopInterop.ClampWindowToWorkArea(this);
        }));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultTextBox.Text)) return;
        try
        {
            Clipboard.SetText(ResultTextBox.Text);
            ShowCopiedFeedback();
        }
        catch (ExternalException)
        {
            ResetCopyFeedback();
            SetStatus("剪贴板暂时被其他程序使用，请稍后再试。", true);
        }
    }

    private void ShowCopiedFeedback()
    {
        ResetCopyFeedback();
        CopyIcon.Visibility = Visibility.Collapsed;
        CopySuccessIcon.Visibility = Visibility.Visible;
        CopyButton.ToolTip = "已复制";
        AutomationProperties.SetName(CopyButton, "已复制");
        long version = _copyFeedbackVersion;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
        _copyFeedbackTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // A queued tick from an earlier copy must not reset feedback for a new selection or copy.
            if (version != _copyFeedbackVersion || !ReferenceEquals(_copyFeedbackTimer, timer)) return;
            ResetCopyFeedback();
        };
        timer.Start();
    }

    private void ResetCopyFeedback()
    {
        ++_copyFeedbackVersion;
        _copyFeedbackTimer?.Stop();
        _copyFeedbackTimer = null;
        CopyIcon.Visibility = Visibility.Visible;
        CopySuccessIcon.Visibility = Visibility.Collapsed;
        CopyButton.ToolTip = "复制译文";
        AutomationProperties.SetName(CopyButton, "复制译文");
    }

    private void Pinned_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = PinnedBox.IsChecked == true;
        PinnedChanged?.Invoke(Topmost);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Cancel();
        Hide();
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            Cancel();
            ResetCopyFeedback();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R && !_busy)
        {
            Retry_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && ResultTextBox.SelectionLength == 0)
        {
            Copy_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape) return;
        Cancel();
        Hide();
        e.Handled = true;
    }
}
