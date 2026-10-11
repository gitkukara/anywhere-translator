using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TranslatorAnywhere.Services;

namespace TranslatorAnywhere.Views;

// Keep source text independently of the browser, for copying and graceful fallback.
public sealed class TranslationResultView : UserControl, IDisposable
{
    private const string RendererOrigin = "https://translation-renderer.invalid";
    private static Task<CoreWebView2Environment>? _environment;
    private readonly StringBuilder _text = new();
    private readonly TextBox _fallback;
    private readonly WebView2CompositionControl _browser;
    private readonly DispatcherTimer _renderTimer;
    private bool _started;
    private bool _ready;
    private bool _failed;
    private bool _disposed;
    private long _revision;
    private long _sentRevision = -1;
    private int _sentTextLength;

    public string Text => _text.ToString();
    public bool HasSelection => !_ready && _fallback.SelectionLength > 0;
    public bool HasBrowserFocus => _ready && _browser.IsKeyboardFocusWithin;
    public bool IsRendererReady => _ready;
    public long RenderedRevision { get; private set; } = -1;
    public long Revision => _revision;
    public event Action<string>? ActionRequested;

    public TranslationResultView()
    {
        MinHeight = 52;
        _fallback = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent,
        };
        _fallback.SetResourceReference(ForegroundProperty, "InkBrush");
        _browser = new WebView2CompositionControl { Visibility = Visibility.Hidden, MinWidth = 1, MinHeight = 52, AllowExternalDrop = false,
            DefaultBackgroundColor = System.Drawing.Color.Transparent };
        var grid = new Grid();
        grid.Children.Add(_fallback);
        grid.Children.Add(_browser);
        Content = grid;
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _renderTimer.Tick += (_, _) => Flush();
        Loaded += async (_, _) => await InitializeRendererAsync();
    }

    public void Clear()
    {
        _text.Clear();
        _fallback.Clear();
        _fallback.Visibility = Visibility.Visible;
        _browser.Visibility = Visibility.Hidden;
        Height = double.NaN;
        QueueRender();
        Flush();
    }

    public void AppendText(string delta)
    {
        _text.Append(delta);
        // Do not maintain a second large WPF document once rich rendering is active.
        if (!_ready) _fallback.AppendText(delta);
        QueueRender();
    }

    public void Flush()
    {
        _renderTimer.Stop();
        if (!_ready || _disposed) return;
        try
        {
            _sentRevision = _revision;
            _sentTextLength = _text.Length;
            _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "render", text = Text, revision = _revision,
                dark = Application.Current?.TryFindResource("IsDarkTheme") is true,
                palette = new
                {
                    ink = Color("InkBrush", "#263442"), surface = Color("SurfaceBrush", "#FFFFFF"),
                    muted = Color("MutedBrush", "#667988"), divider = Color("DividerBrush", "#E8EDF1"),
                    accent = Color("AccentBrush", "#3390EC"), selection = Color("SelectionBrush", "#B5D8FA"),
                },
            }));
        }
        catch (Exception ex) { UseFallback(ex); }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_renderTimer is not null && (e.Property == ForegroundProperty || e.Property == BackgroundProperty))
            QueueRender();
    }

    private void QueueRender()
    {
        ++_revision;
        // A repeating timer coalesces streamed deltas without starving on a continuous stream.
        if (_ready && !_renderTimer.IsEnabled) _renderTimer.Start();
    }

    private string Color(string resource, string fallback) => TryFindResource(resource) is SolidColorBrush brush
        ? $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}" : fallback;

    private async Task InitializeRendererAsync()
    {
        if (_started || _disposed) return;
        _started = true;
        try
        {
            string assets = Path.Combine(AppContext.BaseDirectory, "Assets", "TranslationRenderer");
            if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException("Translation renderer assets are missing.");
            _environment ??= CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(SettingsStore.ResolveDataDirectory(), "webview2"));
            await _browser.EnsureCoreWebView2Async(await _environment);
            if (_disposed) return;
            var core = _browser.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("translation-renderer.invalid", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != RendererOrigin + "/index.html";
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
                    || uri.Scheme != "https" || uri.Host != "translation-renderer.invalid")
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) UseFallback(new InvalidOperationException("Translation renderer did not load."));
            };
            core.ProcessFailed += (_, _) => UseFallback(new InvalidOperationException("Translation renderer process failed."));
            core.Navigate(RendererOrigin + "/index.html");
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (!_ready && !_disposed && !_failed) UseFallback(new TimeoutException("Translation renderer startup timed out."));
        }
        catch (Exception ex) { UseFallback(ex); }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || _failed || e.Source != RendererOrigin + "/index.html") return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    _ready = true;
                    Flush();
                    break;
                case "height":
                    // Deltas arriving since the last send must not discard a valid streamed render.
                    if (root.GetProperty("revision").GetInt64() != _sentRevision) return;
                    double height = root.GetProperty("height").GetDouble();
                    if (!double.IsFinite(height)) return;
                    RenderedRevision = _sentRevision;
                    Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, MaxHeight));
                    _browser.Visibility = _sentTextLength > 0 ? Visibility.Visible : Visibility.Hidden;
                    _fallback.Visibility = _sentTextLength > 0 ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case "action":
                    ActionRequested?.Invoke(root.GetProperty("action").GetString() ?? "");
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { DiagnosticLog.Write("Translation renderer returned an invalid message", ex); }
    }

    private void UseFallback(Exception error)
    {
        if (_disposed || _failed) return;
        _failed = true;
        _ready = false;
        _renderTimer.Stop();
        Height = double.NaN;
        // Composition capture cannot resize to zero, even while falling back.
        _browser.Visibility = Visibility.Hidden;
        _fallback.Text = Text;
        _fallback.Visibility = Visibility.Visible;
        _fallback.ToolTip = "公式排版不可用，已显示源文本。请确认已安装 Microsoft Edge WebView2 Runtime。";
        DiagnosticLog.Write("Translation renderer unavailable; using plain text", error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderTimer.Stop();
        _browser.Dispose();
    }
}
