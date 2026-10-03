using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

namespace TranslatorAnywhere.Views;

public partial class SettingsWindow : Window
{
    private AppSettings _draft;
    private bool _ready;
    private bool _hasPendingChanges;
    private string _lastSavedJson = "";
    private string _lastSavedForm = "";
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private int _statusVersion;
    private Control? _invalidControl;
    private int _invalidTab;
    private Point _previewPointer = new(210, 105);
    private BitmapSource? _previewIcon;

    public event Action<AppSettings, IReadOnlyDictionary<Guid, string>>? SettingsSaved;
    public event Action? TestSelectionRequested;
    public Func<string, string>? ImportIcon { get; set; }

    public SettingsWindow(AppSettings settings, string apiKey) : this(LegacyDraft(settings), _ => apiKey ?? "") { }

    public SettingsWindow(AppSettings settings, Func<Guid, string> readKey)
    {
        // Keep incomplete edits separate from the last successfully persisted configuration.
        _draft = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        InitializeComponent();
        ThemeService.TrackWindow(this);
        ThemeBox.SelectedIndex = (int)_draft.Theme;
        ModeBox.SelectedIndex = (int)_draft.ButtonMode;
        AnchorBox.SelectedValue = _draft.Anchor.ToString();
        if (AnchorBox.SelectedIndex < 0) AnchorBox.ToolTip = "保留原来的鼠标附近位置，选择方位后更改。";
        AnchorBox.SelectionChanged += (_, _) => { if (AnchorBox.SelectedIndex >= 0) AnchorBox.ToolTip = null; };
        ButtonTextBox.Text = _draft.ButtonText;
        ColorBox.Text = DisplayColor(_draft.ButtonColor);
        SelectValue(SizeChoice, _draft.ButtonSize);
        SelectValue(TransparencyChoice, 100 - _draft.ButtonTransparency);
        OffsetXBox.Text = _draft.OffsetX.ToString(CultureInfo.InvariantCulture);
        OffsetYBox.Text = _draft.OffsetY.ToString(CultureInfo.InvariantCulture);
        EnabledBox.IsChecked = _draft.Enabled;
        LaunchAtStartupCheck.IsChecked = _draft.LaunchAtStartup;
        DelayBox.Text = _draft.SelectionDelayMs.ToString(CultureInfo.InvariantCulture);
        AutoHideBox.Text = _draft.AutoHideSeconds.ToString(CultureInfo.InvariantCulture);
        HotkeyFallbackBox.IsChecked = _draft.ClipboardFallbackOnHotkey;
        AutomaticFallbackBox.IsChecked = _draft.AutomaticClipboardFallback;
        FallbackAppsBox.Text = string.Join(Environment.NewLine, _draft.ClipboardFallbackApplications);
        ExcludedAppsBox.Text = string.Join(Environment.NewLine, _draft.ExcludedApplications);
        LanguageBox.SelectedValue = _draft.TargetLanguage;
        if (LanguageBox.SelectedIndex < 0) LanguageBox.SelectedIndex = 0;
        ProviderEditor.TargetLanguage = () => SelectedLanguage;
        ProviderEditor.Load(_draft, readKey);
        ProviderEditor.ConfigurationChanged += QueueSave;
        _saveTimer.Tick += (_, _) => FlushPendingChanges();
        foreach (var textBox in new[] { DelayBox, AutoHideBox, FallbackAppsBox, ExcludedAppsBox })
            textBox.TextChanged += (_, _) => QueueSave();
        LanguageBox.SelectionChanged += (_, _) => QueueSave();
        ThemeBox.SelectionChanged += (_, _) => QueueSave();
        foreach (var checkBox in new[] { EnabledBox, LaunchAtStartupCheck, HotkeyFallbackBox, AutomaticFallbackBox })
        {
            checkBox.Checked += (_, _) => QueueSave();
            checkBox.Unchecked += (_, _) => QueueSave();
        }
        Closing += (_, e) =>
        {
            if (FlushPendingChanges()) return;
            e.Cancel = true;
            if (_invalidControl is null) return;
            SettingsTabs.SelectedIndex = _invalidTab;
            _invalidControl.BringIntoView();
            _invalidControl.Focus();
        };
        Closed += (_, _) => { _saveTimer.Stop(); ProviderEditor.Dispose(); };
        StatusLabel.MouseLeftButtonUp += (_, _) => FlushPendingChanges();
        LoadIcon(_draft.IconPath);
        _ready = true;
        ApplyModeControls();
        ApplyCompatibilityControls();
        UpdatePreview();
        _lastSavedJson = JsonSerializer.Serialize(_draft);
        _lastSavedForm = FormSnapshot();
    }

    public void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(text));
            return;
        }
        ShowError(text);
    }

    public void AcceptSaved() => ProviderEditor.AcceptSaved();

    public void SetStartupState(bool enabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStartupState(enabled));
            return;
        }
        bool ready = _ready;
        _ready = false;
        try
        {
            LaunchAtStartupCheck.IsChecked = enabled;
            _draft.LaunchAtStartup = enabled;
        }
        finally { _ready = ready; }
        if (!_hasPendingChanges) _lastSavedForm = FormSnapshot();
    }

    public void SetEnabledState(bool enabled)
    {
        bool ready = _ready;
        _ready = false;
        try { EnabledBox.IsChecked = enabled; _draft.Enabled = enabled; }
        finally { _ready = ready; }
        if (!_hasPendingChanges) _lastSavedForm = FormSnapshot();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PreviewText.Select(0, 7);
        UpdatePreview();
    }

    private void ColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color }) ColorBox.Text = color;
    }

    private void Form_SelectionChanged(object sender, SelectionChangedEventArgs e) => Form_Changed(sender, e);

    private void Form_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplyModeControls();
        UpdatePreview();
        QueueSave();
    }

    private void QueueSave()
    {
        if (!_ready) return;
        _hasPendingChanges = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        StatusLabel.Visibility = Visibility.Collapsed;
    }

    public bool FlushPendingChanges()
    {
        _saveTimer.Stop();
        if (!_ready || !_hasPendingChanges || SettingsSaved is null) return true;
        _invalidControl = null;
        // Template initialization and edits reverted to their original text are not user changes.
        // Compare before validation so an unchanged legacy problem cannot trap the window open.
        if (FormSnapshot() == _lastSavedForm && ProviderEditor.PendingApiKeys.Count == 0)
        {
            _hasPendingChanges = false;
            StatusLabel.Visibility = Visibility.Collapsed;
            return true;
        }
        if (!TryReadForm(out var settings)) return false;
        var pendingKeys = ProviderEditor.PendingApiKeys;
        if (JsonSerializer.Serialize(settings) == _lastSavedJson && pendingKeys.Count == 0)
        {
            _hasPendingChanges = false;
            _lastSavedForm = FormSnapshot();
            StatusLabel.Visibility = Visibility.Collapsed;
            return true;
        }
        int statusVersion = _statusVersion;
        try
        {
            SettingsSaved.Invoke(settings, pendingKeys);
            // The callback only returns after persistence succeeds; failed credentials remain pending.
            AcceptSaved();
            _draft = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            _lastSavedJson = JsonSerializer.Serialize(_draft);
            _lastSavedForm = FormSnapshot();
            _hasPendingChanges = false;
            if (_statusVersion == statusVersion) StatusLabel.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception)
        {
            ShowError("自动保存失败，请检查文件夹权限。点击重试。");
            return false;
        }
    }

    private ButtonAnchor SelectedAnchor => AnchorBox.SelectedValue is string name && Enum.TryParse<ButtonAnchor>(name, out var anchor) ? anchor : _draft.Anchor;
    private double SelectedSize => ChoiceValue(SizeChoice) ?? _draft.ButtonSize;
    private double SelectedTransparency => ChoiceValue(TransparencyChoice) is double opacity ? 100 - opacity : _draft.ButtonTransparency;
    private static double? ChoiceValue(ListBox choice) => choice.SelectedItem is ListBoxItem item
        ? double.Parse((string)item.Tag, CultureInfo.InvariantCulture) : null;
    private static void SelectValue(ListBox choice, double value)
    {
        choice.SelectedItem = choice.Items.Cast<ListBoxItem>().FirstOrDefault(item =>
            double.Parse((string)item.Tag, CultureInfo.InvariantCulture) == value);
        if (choice.SelectedIndex < 0) choice.ToolTip = $"保留原值：{value:0.##}，选择档位后更改。";
        choice.SelectionChanged += (_, _) => { if (choice.SelectedIndex >= 0) choice.ToolTip = null; };
    }

    private string FormSnapshot() => JsonSerializer.Serialize(new
    {
        Theme = ThemeBox.SelectedIndex, Mode = ModeBox.SelectedIndex, Label = ButtonTextBox.Text, Color = ColorBox.Text,
        Size = SelectedSize, Transparency = SelectedTransparency,
        Anchor = SelectedAnchor, X = OffsetXBox.Text, Y = OffsetYBox.Text,
        Enabled = EnabledBox.IsChecked, Startup = LaunchAtStartupCheck.IsChecked,
        Delay = DelayBox.Text, Hide = AutoHideBox.Text, Hotkey = HotkeyFallbackBox.IsChecked,
        Automatic = AutomaticFallbackBox.IsChecked, FallbackApps = FallbackAppsBox.Text,
        ExcludedApps = ExcludedAppsBox.Text, Language = SelectedLanguage, _draft.IconPath,
        Providers = ProviderEditor.DraftProviders, ProviderEditor.ActiveProviderId
    });

    private void ApplyModeControls()
    {
        ButtonTextBox.IsEnabled = ModeBox.SelectedIndex == 0;
        TextModeRow.Visibility = ModeBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        IconModeRow.Visibility = ModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutomaticFallback_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) ApplyCompatibilityControls();
    }

    private void ApplyCompatibilityControls() => FallbackAppsBox.IsEnabled = AutomaticFallbackBox.IsChecked == true;

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready) return;
        PreviewText.Width = Math.Max(100, PreviewCanvas.ActualWidth - 32);
        Dispatcher.BeginInvoke(new Action(UpdatePreview));
    }

    private void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_ready) return;
        _previewPointer = e.GetPosition(PreviewCanvas);
        if (SelectedAnchor == ButtonAnchor.Cursor) UpdatePreview();
    }

    private void PreviewText_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdatePreview();
    }

    private Rect GetPreviewSelectionBounds()
    {
        Rect selection = Rect.Empty;
        for (int index = PreviewText.SelectionStart; index < PreviewText.SelectionStart + PreviewText.SelectionLength; index++)
        {
            Rect start = PreviewText.GetRectFromCharacterIndex(index);
            Rect end = PreviewText.GetRectFromCharacterIndex(index, true);
            if (start.IsEmpty || start.Height <= 0) continue;
            var origin = PreviewText.TranslatePoint(start.TopLeft, PreviewCanvas);
            var character = new Rect(origin.X, origin.Y, Math.Max(2, end.X - start.X), start.Height);
            selection.Union(character);
        }
        return selection;
    }

    private void UpdatePreview()
    {
        if (!_ready || PreviewCanvas is null) return;
        double size = SelectedSize;
        double transparency = SelectedTransparency;
        PreviewButton.Opacity = 1 - transparency / 100;
        PreviewButton.Width = size;
        PreviewButton.Height = size;
        PreviewButton.CornerRadius = new CornerRadius(Math.Min(10, size * .22));
        if (TryColor(ColorBox.Text, out var color))
        {
            var brush = new SolidColorBrush(color);
            PreviewButton.Background = brush;
            CurrentColorSwatch.Background = brush;
            CurrentColorSwatch.ToolTip = $"当前颜色：#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        if (ModeBox.SelectedIndex == 2 && _previewIcon is not null)
        {
            PreviewButtonContent.Content = new Image { Source = _previewIcon, Width = size - 10, Height = size - 10, Stretch = Stretch.Uniform };
        }
        else if (ModeBox.SelectedIndex == 1)
        {
            PreviewButtonContent.Content = new HugeIcon
            {
                Kind = HugeIconKind.Translate, Width = size - 10, Height = size - 10, Foreground = Brushes.White
            };
        }
        else
        {
            string text = ModeBox.SelectedIndex switch
            {
                0 => string.IsNullOrWhiteSpace(ButtonTextBox.Text) ? "翻" : ButtonTextBox.Text,
                _ => "翻"
            };
            PreviewButtonContent.Content = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = Math.Clamp(size * (text.Length > 2 ? 0.26 : 0.54), 10, 32),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                MaxWidth = size - 6,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        Rect bounds = GetPreviewSelectionBounds();
        if (bounds.IsEmpty)
        {
            PreviewButton.Visibility = Visibility.Collapsed;
            PreviewSelectionOutline.Visibility = Visibility.Collapsed;
            PreviewPositionLabel.Text = "在上方文字中选中一段内容。";
            return;
        }
        PreviewSelectionOutline.Visibility = Visibility.Visible;
        PreviewSelectionOutline.Width = bounds.Width;
        PreviewSelectionOutline.Height = bounds.Height;
        Canvas.SetLeft(PreviewSelectionOutline, bounds.Left);
        Canvas.SetTop(PreviewSelectionOutline, bounds.Top);

        _ = double.TryParse(OffsetXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetX);
        _ = double.TryParse(OffsetYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetY);
        if (!double.IsFinite(offsetX)) offsetX = 0;
        if (!double.IsFinite(offsetY)) offsetY = 0;
        var anchor = SelectedAnchor;
        double x = anchor switch
        {
            ButtonAnchor.SelectionTopLeft or ButtonAnchor.SelectionBottomLeft => bounds.Left - size,
            ButtonAnchor.Cursor => _previewPointer.X,
            _ => bounds.Right
        };
        double y = anchor switch
        {
            ButtonAnchor.SelectionTopLeft or ButtonAnchor.SelectionTopRight => bounds.Top - size,
            ButtonAnchor.Cursor => _previewPointer.Y,
            _ => bounds.Bottom
        };
        x = Math.Clamp(x + offsetX, 4, Math.Max(4, PreviewCanvas.ActualWidth - size - 4));
        y = Math.Clamp(y + offsetY, 4, Math.Max(4, PreviewCanvas.ActualHeight - size - 4));
        Canvas.SetLeft(PreviewButton, x);
        Canvas.SetTop(PreviewButton, y);
        PreviewButton.Visibility = Visibility.Visible;
        string anchorText = (AnchorBox.SelectedItem as ListBoxItem)?.Content?.ToString() ?? "选区右下角";
        PreviewPositionLabel.Text = $"{anchorText} · 横向 {offsetX:+0;-0;0} px · 纵向 {offsetY:+0;-0;0} px";
    }

    private void LoadIcon(string path)
    {
        _previewIcon = null;
        IconNameLabel.Text = string.IsNullOrWhiteSpace(path) ? "尚未导入" : Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            _previewIcon = frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            SetStatus("图标无法读取，请重新导入 PNG、JPG、ICO、BMP 或 GIF 图片。");
        }
    }

    private void ImportIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入翻译按钮图标",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string path = ImportIcon is null ? dialog.FileName : ImportIcon(dialog.FileName);
            LoadIcon(path);
            if (_previewIcon is null) return;
            _draft.IconPath = path;
            ModeBox.SelectedIndex = (int)ButtonVisualMode.Icon;
            UpdatePreview();
            QueueSave();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError("图标导入失败：" + ex.Message);
        }
    }

    private void TestSelection_Click(object sender, RoutedEventArgs e) => TestSelectionRequested?.Invoke();

    private bool TryReadForm(out AppSettings settings)
    {
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_draft)) ?? new AppSettings();
        string label = ButtonTextBox.Text.Trim();
        if (ModeBox.SelectedIndex == 0 && string.IsNullOrWhiteSpace(label)) return Invalid("请输入按钮文字。", ButtonTextBox, 0);
        if (!TryColor(ColorBox.Text, out Color color)) return Invalid("颜色格式应为 #RRGGBB，例如 #3390EC。", ColorBox, 0);
        if (!TryOffset(OffsetXBox.Text, out double offsetX)) return Invalid("横向偏移应为 -500 到 500 之间的数字。", OffsetXBox, 0);
        if (!TryOffset(OffsetYBox.Text, out double offsetY)) return Invalid("纵向偏移应为 -500 到 500 之间的数字。", OffsetYBox, 0);
        if (ModeBox.SelectedIndex == 2 && (_previewIcon is null || string.IsNullOrWhiteSpace(settings.IconPath) || !File.Exists(settings.IconPath))) return Invalid("请先导入有效的按钮图标。", ModeBox, 0);
        if (!int.TryParse(DelayBox.Text, out int delay) || delay < 50 || delay > 1500) return Invalid("选区确认延迟应为 50 到 1500 毫秒。", DelayBox, 2);
        if (!int.TryParse(AutoHideBox.Text, out int hide) || hide < 2 || hide > 120) return Invalid("自动隐藏时间应为 2 到 120 秒。", AutoHideBox, 2);
        if (LanguageBox.SelectedItem is not ComboBoxItem) return Invalid("请选择目标语言。", LanguageBox, 1);
        try { ProviderEditor.ApplyTo(settings); }
        catch (ArgumentException ex) { return Invalid(ex.Message, ProviderEditor, 1); }
        List<string> fallbackApps = ParseApplications(FallbackAppsBox.Text);
        if (AutomaticFallbackBox.IsChecked == true && fallbackApps.Count == 0) return Invalid("启用自动复制取词前，请添加至少一个兼容应用。", FallbackAppsBox, 2);

        settings.Theme = (AppTheme)Math.Clamp(ThemeBox.SelectedIndex, 0, 2);
        settings.Enabled = EnabledBox.IsChecked == true;
        settings.LaunchAtStartup = LaunchAtStartupCheck.IsChecked == true;
        settings.ButtonMode = (ButtonVisualMode)Math.Clamp(ModeBox.SelectedIndex, 0, 2);
        settings.ButtonText = label;
        settings.ButtonColor = RgbHex(color);
        settings.ButtonSize = SelectedSize;
        settings.ButtonTransparency = SelectedTransparency;
        settings.Anchor = SelectedAnchor;
        settings.OffsetX = offsetX;
        settings.OffsetY = offsetY;
        settings.SelectionDelayMs = delay;
        settings.AutoHideSeconds = hide;
        settings.ClipboardFallbackOnHotkey = HotkeyFallbackBox.IsChecked == true;
        settings.AutomaticClipboardFallback = AutomaticFallbackBox.IsChecked == true;
        settings.ClipboardFallbackApplications = fallbackApps;
        settings.ExcludedApplications = ParseApplications(ExcludedAppsBox.Text);
        settings.TargetLanguage = SelectedLanguage;
        return true;
    }

    private string SelectedLanguage => LanguageBox.SelectedValue as string ?? "简体中文";

    private static AppSettings LegacyDraft(AppSettings settings)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        ProviderRegistry.InitializeLegacy(copy);
        return copy;
    }

    private bool Invalid(string message, Control control, int tab)
    {
        _invalidControl = control;
        _invalidTab = tab;
        ShowError("未保存：" + message);
        return false;
    }

    private void ShowError(string text)
    {
        ++_statusVersion;
        StatusLabel.Text = text;
        StatusLabel.ToolTip = text;
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        StatusLabel.Visibility = Visibility.Visible;
    }

    private static bool TryOffset(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value) && value >= -500 && value <= 500;

    private static bool TryColor(string text, out Color color)
    {
        color = default;
        string value = text.Trim();
        if (value.Length != 7 || value[0] != '#') return false;
        if (!byte.TryParse(value.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte red)
            || !byte.TryParse(value.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte green)
            || !byte.TryParse(value.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte blue)) return false;
        color = Color.FromRgb(red, green, blue);
        return true;
    }

    private static string RgbHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string DisplayColor(string value)
    {
        // Earlier versions persisted WPF's #AARRGGBB representation, including opaque FF.
        try
        {
            return ColorConverter.ConvertFromString(value) is Color color ? RgbHex(color) : value;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            return value;
        }
    }

    private static List<string> ParseApplications(string text) => text
        .Split(new[] { '\r', '\n', ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => Path.GetFileNameWithoutExtension(value).ToLowerInvariant())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
