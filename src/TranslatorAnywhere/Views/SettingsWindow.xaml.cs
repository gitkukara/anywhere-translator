using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private ImageSource? _loadedIcon;

    public event Action<AppSettings, IReadOnlyDictionary<Guid, string>>? SettingsSaved;
    public event Action? TestSelectionRequested;
    public Func<string, string>? ImportIcon { get; set; }

    public SettingsWindow(AppSettings settings, string apiKey) : this(LegacyDraft(settings), _ => apiKey ?? "") { }

    public SettingsWindow(AppSettings settings, Func<Guid, string> readKey)
    {
        // Keep incomplete edits separate from the last successfully persisted configuration.
        _draft = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        UiText.EnsureInitialized();
        InitializeComponent();
        var assembly = typeof(SettingsWindow).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3) ?? "未知";
        VersionLabel.Text = "v" + version;
        ThemeService.TrackWindow(this);
        Loaded += (_, _) => FitAppearanceHeight();
        SettingsTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, SettingsTabs))
                Dispatcher.BeginInvoke(new Action(FitAppearanceHeight), DispatcherPriority.Loaded);
        };
        ThemeBox.SelectedIndex = (int)_draft.Theme;
        UiLanguageBox.SelectedIndex = (int)_draft.UiLanguage;
        UiText.Changed += RefreshLanguage;
        ModeBox.SelectedIndex = (int)_draft.ButtonMode;
        AnchorBox.SelectedValue = _draft.Anchor.ToString();
        if (AnchorBox.SelectedIndex < 0) UiText.Set(AnchorBox, FrameworkElement.ToolTipProperty, "保留原来的鼠标附近位置，选择方位后更改。");
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
        UiLanguageBox.SelectionChanged += (_, _) =>
        {
            if (!_ready) return;
            QueueSave();
            // Persist first so a failed save cannot silently change the active language.
            FlushPendingChanges();
        };
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
        Closed += (_, _) => { UiText.Changed -= RefreshLanguage; _saveTimer.Stop(); ProviderEditor.Dispose(); };
        StatusLabel.MouseLeftButtonUp += (_, _) => FlushPendingChanges();
        LoadIcon(_draft.IconPath);
        RefreshRecentIcons();
        _ready = true;
        ApplyModeControls();
        ApplyCompatibilityControls();
        UpdateColorSwatch();
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

    private void ColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color }) ColorBox.Text = color;
    }

    private void Form_SelectionChanged(object sender, SelectionChangedEventArgs e) => Form_Changed(sender, e);

    private void Form_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplyModeControls();
        UpdateColorSwatch();
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
            UiText.Apply(settings.UiLanguage);
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
        if (choice.SelectedIndex < 0) UiText.Set(choice, FrameworkElement.ToolTipProperty, $"保留原值：{value:0.##}，选择档位后更改。");
        choice.SelectionChanged += (_, _) => { if (choice.SelectedIndex >= 0) choice.ToolTip = null; };
    }

    private string FormSnapshot() => JsonSerializer.Serialize(new
    {
        Theme = ThemeBox.SelectedIndex, UiLanguage = UiLanguageBox.SelectedIndex, Mode = ModeBox.SelectedIndex, Label = ButtonTextBox.Text, Color = ColorBox.Text,
        Size = SelectedSize, Transparency = SelectedTransparency,
        Anchor = SelectedAnchor, X = OffsetXBox.Text, Y = OffsetYBox.Text,
        Enabled = EnabledBox.IsChecked, Startup = LaunchAtStartupCheck.IsChecked,
        Delay = DelayBox.Text, Hide = AutoHideBox.Text, Hotkey = HotkeyFallbackBox.IsChecked,
        Automatic = AutomaticFallbackBox.IsChecked, FallbackApps = FallbackAppsBox.Text,
        ExcludedApps = ExcludedAppsBox.Text, Language = SelectedLanguage, _draft.IconPath, _draft.RecentIconPaths,
        Providers = ProviderEditor.DraftProviders, ProviderEditor.ActiveProviderId
    });

    private void ApplyModeControls()
    {
        ButtonTextBox.IsEnabled = ModeBox.SelectedIndex == 0;
        TextModeRow.Visibility = ModeBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        IconModeRow.Visibility = ModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ColorModeRow.Visibility = ModeBox.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(FitAppearanceHeight), DispatcherPriority.Loaded);
    }

    private void FitAppearanceHeight()
    {
        if (!IsLoaded || SettingsTabs.SelectedIndex != 0 || WindowState != WindowState.Normal) return;
        UpdateLayout();
        double height = ActualHeight + AppearanceScrollViewer.ExtentHeight - AppearanceScrollViewer.ViewportHeight + 16;
        double limit = Math.Min(MaxHeight, SystemParameters.WorkArea.Height);
        height = Math.Clamp(Math.Ceiling(height), MinHeight, Math.Max(MinHeight, limit));
        if (Math.Abs(Height - height) > 0.5) Height = height;
    }

    private void AutomaticFallback_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) ApplyCompatibilityControls();
    }

    private void ApplyCompatibilityControls() => FallbackAppsBox.IsEnabled = AutomaticFallbackBox.IsChecked == true;

    private void UpdateColorSwatch()
    {
        if (!_ready || !TryColor(ColorBox.Text, out var color)) return;
        CurrentColorSwatch.Background = new SolidColorBrush(color);
        UiText.Set(CurrentColorSwatch, FrameworkElement.ToolTipProperty, $"当前颜色：#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void LoadIcon(string path)
    {
        _loadedIcon = null;
        UiText.Set(IconNameLabel, System.Windows.Controls.TextBlock.TextProperty, string.IsNullOrWhiteSpace(path) ? "尚未导入" : Path.GetFileName(path));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            _loadedIcon = IconImageService.Load(path);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            SetStatus("图标无法读取，请重新导入透明 PNG 或静态 SVG，也支持 JPG、ICO、BMP 和 GIF。");
        }
    }

    private void ImportIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.T("导入翻译按钮图标"),
            Filter = UiText.T("图标文件（推荐透明 PNG 或 SVG）|*.png;*.svg;*.jpg;*.jpeg;*.ico;*.bmp;*.gif|PNG 图片|*.png|SVG 矢量图标|*.svg"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ImportIconFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError("图标导入失败：" + ex.Message);
        }
    }

    private void ImportIconFile(string sourcePath)
    {
        string path = ImportIcon is null ? sourcePath : ImportIcon(sourcePath);
        var image = IconImageService.Load(path);
        _draft.RecentIconPaths = RecentIconHistory.Add(_draft.RecentIconPaths, path);
        SelectIcon(path, image);
    }

    private void RecentIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        try { SelectIcon(path, IconImageService.Load(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _draft.RecentIconPaths.RemoveAll(value => string.Equals(value, path, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_draft.IconPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _draft.IconPath = "";
                _loadedIcon = null;
                ModeBox.SelectedIndex = (int)ButtonVisualMode.Text;
            }
            RefreshRecentIcons();
            QueueSave();
            ShowError("图标无法读取，请重新导入。");
        }
    }

    private void SelectIcon(string path, ImageSource image)
    {
        _loadedIcon = image;
        _draft.IconPath = path;
        ModeBox.SelectedIndex = (int)ButtonVisualMode.Icon;
        RefreshRecentIcons();
        QueueSave();
    }

    private sealed record RecentIconChoice(string Path, ImageSource Image, bool IsSelected, string Label);

    private void RefreshRecentIcons()
    {
        var paths = RecentIconHistory.Normalize(_draft.RecentIconPaths);
        if (paths.Count == 0 && !string.IsNullOrWhiteSpace(_draft.IconPath)) paths.Add(_draft.IconPath);
        var choices = new List<RecentIconChoice>();
        foreach (string path in paths)
        {
            try
            {
                var image = IconImageService.Load(path);
                choices.Add(new RecentIconChoice(path, image, string.Equals(path, _draft.IconPath, StringComparison.OrdinalIgnoreCase),
                    UiText.T("使用最近导入的图标 " + (choices.Count + 1))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        _draft.RecentIconPaths = choices.Select(choice => choice.Path).ToList();
        RecentIcons.ItemsSource = choices;
        RecentIcons.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IconNameLabel.Visibility = choices.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UiText.Set(IconNameLabel, System.Windows.Controls.TextBlock.TextProperty, "尚未导入");
    }

    private void TestSelection_Click(object sender, RoutedEventArgs e)
    {
        if (FlushPendingChanges()) TestSelectionRequested?.Invoke();
    }

    private bool TryReadForm(out AppSettings settings)
    {
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_draft)) ?? new AppSettings();
        string label = ButtonTextBox.Text.Trim();
        if (ModeBox.SelectedIndex == 0 && string.IsNullOrWhiteSpace(label)) return Invalid("请输入按钮文字。", ButtonTextBox, 0);
        if (!TryColor(ColorBox.Text, out Color color))
        {
            if (ModeBox.SelectedIndex != (int)ButtonVisualMode.Icon)
                return Invalid("颜色格式应为 #RRGGBB，例如 #3390EC。", ColorBox, 0);
            if (!TryColor(_draft.ButtonColor, out color)) color = Color.FromRgb(51, 144, 236);
        }
        if (!TryOffset(OffsetXBox.Text, out double offsetX)) return Invalid("横向偏移应为 -500 到 500 之间的数字。", OffsetXBox, 0);
        if (!TryOffset(OffsetYBox.Text, out double offsetY)) return Invalid("纵向偏移应为 -500 到 500 之间的数字。", OffsetYBox, 0);
        if (ModeBox.SelectedIndex == 2 && (_loadedIcon is null || string.IsNullOrWhiteSpace(settings.IconPath) || !File.Exists(settings.IconPath))) return Invalid("请先导入有效的按钮图标。", ModeBox, 0);
        if (!int.TryParse(DelayBox.Text, out int delay) || delay < 50 || delay > 1500) return Invalid("选区确认延迟应为 50 到 1500 毫秒。", DelayBox, 2);
        if (!int.TryParse(AutoHideBox.Text, out int hide) || hide < 2 || hide > 120) return Invalid("自动隐藏时间应为 2 到 120 秒。", AutoHideBox, 2);
        if (LanguageBox.SelectedItem is not ComboBoxItem) return Invalid("请选择目标语言。", LanguageBox, 1);
        try { ProviderEditor.ApplyTo(settings); }
        catch (ArgumentException ex) { return Invalid(ex.Message, ProviderEditor, 1); }
        List<string> fallbackApps = ParseApplications(FallbackAppsBox.Text);
        if (AutomaticFallbackBox.IsChecked == true && fallbackApps.Count == 0) return Invalid("启用自动复制取词前，请添加至少一个兼容应用。", FallbackAppsBox, 2);

        settings.Theme = (AppTheme)Math.Clamp(ThemeBox.SelectedIndex, 0, 2);
        settings.UiLanguage = (InterfaceLanguage)Math.Clamp(UiLanguageBox.SelectedIndex, 0, 2);
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

    private void RefreshLanguage()
    {
        bool ready = _ready;
        _ready = false;
        try { RefreshRecentIcons(); }
        finally { _ready = ready; }
        Dispatcher.BeginInvoke(new Action(FitAppearanceHeight), DispatcherPriority.Loaded);
    }

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
        UiText.Set(StatusLabel, System.Windows.Controls.TextBlock.TextProperty, text);
        UiText.Set(StatusLabel, FrameworkElement.ToolTipProperty, text);
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
