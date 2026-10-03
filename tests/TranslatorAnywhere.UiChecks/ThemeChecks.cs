using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;
using TranslatorAnywhere.Views;

internal static partial class Program
{
    private static async Task CheckThemesAsync(TranslationWindow translation)
    {
        var compact = new SettingsWindow(new AppSettings { ButtonMode = ButtonVisualMode.Icon, Anchor = ButtonAnchor.SelectionBottomLeft, TargetLanguage = "English" }, _ => "");
        try
        {
            compact.Show();
            await LayoutAsync(compact);
            var modes = (ListBox)compact.FindName("ModeBox");
            var anchors = (ListBox)compact.FindName("AnchorBox");
            Check(modes.Items.Cast<ListBoxItem>().Select(item => item.Content.ToString()).SequenceEqual(new[] { "文字", "符号", "自设" }), "Display modes are three inline choices");
            Check(anchors.Items.Cast<ListBoxItem>().Select(item => item.Content.ToString()).SequenceEqual(new[] { "左上", "左下", "右上", "右下" }) && anchors.SelectedIndex == 1, "Position choices use visual order while preserving saved enum mapping");
            foreach (string name in new[] { "LanguageBox" })
            {
                ((TabControl)compact.FindName("SettingsTabs")).SelectedIndex = name == "LanguageBox" ? 1 : 0;
                await LayoutAsync(compact);
                var combo = (ComboBox)compact.FindName(name);
                var content = (ContentPresenter)combo.Template.FindName("SelectionContent", combo);
                var text = FindVisual<TextBlock>(content)!;
                var required = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
                    VisualTreeHelper.GetDpi(text).PixelsPerDip);
                Check(text.ActualWidth + .1 >= required.WidthIncludingTrailingWhitespace, name + " shows its full text without clipping");
            }
            ((TabControl)compact.FindName("SettingsTabs")).SelectedIndex = 0;
            await LayoutAsync(compact);
            Save(compact, "compact-dropdown-text.png");
        }
        finally { compact.Close(); }
        bool systemDark = false;
        using var theme = new ThemeService(app, () => systemDark);
        theme.Apply(AppTheme.System);
        string? previousDirectory = Environment.GetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR");
        Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", Path.Combine(output, "theme-data"));
        var store = new SettingsStore();
        var settings = new AppSettings();
        ProviderRegistry.InitializeLegacy(settings);
        settings.Providers.Add(ProviderRegistry.CreateProvider("openai"));
        settings.Providers.Add(ProviderRegistry.CreateProvider("zhipu"));
        var window = new SettingsWindow(settings, _ => "");
        var fixture = new SelectionFixtureWindow();
        int writes = 0;
        window.SettingsSaved += (updated, keys) =>
        {
            store.SaveConfiguration(updated, keys);
            settings = updated;
            theme.Apply(updated.Theme);
            writes++;
        };
        try
        {
            window.Show();
            fixture.Show();
            var tabs = (TabControl)window.FindName("SettingsTabs");
            var choice = (ListBox)window.FindName("ThemeBox");
            tabs.SelectedIndex = 2;
            await LayoutAsync(window);
            Check(choice.IsVisible && choice.Items.Cast<ListBoxItem>().Select(item => item.Content.ToString()).SequenceEqual(new[] { "跟随系统", "浅色", "深色" }), "Other settings offer exactly three fixed theme choices");
            Check(writes == 0, "Opening the theme settings does not write configuration");
            choice.SelectedIndex = 2;
            await Task.Delay(800);
            await LayoutAsync(window);
            Check(writes == 1 && theme.IsDark && store.Load().Theme == AppTheme.Dark, "Selecting dark mode autosaves and applies without restarting");
            Check(ThemeColor(window.Background) == ThemeResource("BackgroundBrush"), "An already open settings window changes its background");
            Check(ThemeColor(((TextBox)window.FindName("DelayBox")).Background) == ThemeResource("FieldBrush"), "Input fields follow dark mode");
            Check(ThemeColor(((TextBox)translation.FindName("ResultTextBox")).Foreground) == ThemeResource("InkBrush"), "An already open translation adopts readable text");
            Check(ThemeColor(((TextBlock)translation.FindName("RequestStatusLabel")).Foreground) == ThemeResource("MutedBrush"), "Programmatically assigned translation status follows theme changes");
            Check(ThemeColor(fixture.Background) == ThemeResource("BackgroundBrush"), "Selection test window uses the same palette");
            Check(Contrast(ThemeResource("InkBrush"), ThemeResource("FieldBrush")) >= 4.5 && Contrast(ThemeResource("MutedBrush"), ThemeResource("SurfaceBrush")) >= 4.5, "Dark mode normal and secondary text have readable contrast");
            SaveNativeFrame(window, "theme-dark-native.png");
            Save(window, "theme-dark-other.png");
            Save(translation, "theme-dark-translation.png");
            var language = (ComboBox)window.FindName("LanguageBox");
            tabs.SelectedIndex = 1;
            await LayoutAsync(window);
            language.IsDropDownOpen = true;
            await LayoutAsync(window);
            var popup = (Popup)language.Template.FindName("PART_Popup", language);
            Check(popup.Child is Border border && ThemeColor(border.Background) == ThemeResource("SurfaceBrush"), "Dropdown uses dark surface");
            Save(window, "theme-dark-dropdown.png", popup.Child as FrameworkElement);
            theme.Apply(AppTheme.Light);
            await LayoutAsync(window);
            Check(ThemeColor(((Border)popup.Child).Background) == ThemeResource("SurfaceBrush"), "An existing popup follows live palette changes");
            language.IsDropDownOpen = false;
            theme.Apply(AppTheme.Dark);
            tabs.SelectedIndex = 0;
            await LayoutAsync(window);
            Save(window, "theme-dark-appearance.png");
            ((ScrollViewer)window.FindName("AppearanceScrollViewer")).ScrollToBottom();
            await LayoutAsync(window);
            Save(window, "theme-dark-preview.png");
            tabs.SelectedIndex = 1;
            await LayoutAsync(window);
            var providerEditor = (ProviderSettingsControl)window.FindName("ProviderEditor");
            var labels = FindAllVisual<TextBlock>((StackPanel)providerEditor.FindName("ProviderListPanel")).Where(label => settings.Providers.Any(provider => provider.Name == label.Text)).ToArray();
            Check(labels.Length == 3 && labels.All(label => ThemeColor(label.Foreground) == ThemeResource("InkBrush")), "Dynamically built provider rows follow the palette");
            Save(window, "theme-dark-providers.png");
            providerEditor.OpenCatalog();
            await LayoutAsync(window);
            Save(window, "theme-dark-catalog.png");
            providerEditor.OpenProvider(settings.Providers[0].Id);
            await LayoutAsync(window);
            Check(ThemeColor(((PasswordBox)providerEditor.FindName("ProviderApiKeyBox")).Background) == ThemeResource("FieldBrush"), "Provider credentials use the themed input surface");
            Save(window, "theme-dark-provider-detail.png");
            theme.Apply(AppTheme.Light);
            await LayoutAsync(window);
            Check(labels.All(label => ThemeColor(label.Foreground) == ThemeResource("InkBrush")), "Existing provider labels update when returning to light mode");
            Save(window, "theme-light-provider-detail.png");
            tabs.SelectedIndex = 2;
            choice.SelectedIndex = 1;
            Check(window.FlushPendingChanges(), "Explicit light theme is saved");
            Check(!theme.IsDark && store.Load().Theme == AppTheme.Light, "Light theme can override Windows");
            systemDark = true;
            await Task.Run(theme.RefreshSystemTheme);
            await LayoutAsync(window);
            Check(!theme.IsDark, "System changes do not override explicit light mode");
            choice.SelectedIndex = 0;
            Check(window.FlushPendingChanges(), "Follow-system preference is saved");
            Check(theme.IsDark && store.Load().Theme == AppTheme.System, "Follow-system resolves current Windows mode without persisting its resolved color");
            int writesBeforeSystemChange = writes;
            systemDark = false;
            await Task.Run(theme.RefreshSystemTheme);
            await LayoutAsync(window);
            Check(!theme.IsDark && writes == writesBeforeSystemChange, "Background system notifications switch live without writing user settings");
            Check(ThemeColor(window.Background) == Color.FromRgb(242, 245, 247), "Light palette preserves the existing background");
            Save(window, "theme-light-other.png");
            choice.SelectedIndex = 2;
            Check(window.FlushPendingChanges(), "Dark mode persists before reopening");
            theme.Apply(store.Load().Theme);
            var reopened = new SettingsWindow(store.Load(), _ => "");
            reopened.Show();
            await LayoutAsync(reopened);
            Check(((ListBox)reopened.FindName("ThemeBox")).SelectedIndex == 2 && theme.IsDark, "Reopening restores the saved explicit theme");
            reopened.Close();

        }
        finally
        {
            window.Close(); fixture.Close();
            theme.Apply(AppTheme.Light);
            Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", previousDirectory);
        }
    }

    private static Color ThemeColor(Brush brush) => ((SolidColorBrush)brush).Color;
    private static Color ThemeResource(string key) => ThemeColor((Brush)app.FindResource(key));
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double L(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }
    private static void SaveNativeFrame(Window window, string name)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var bounds)) throw new InvalidOperationException("Cannot read test window bounds");
        using var bitmap = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        try { if (!PrintWindow(handle, dc, 2)) throw new InvalidOperationException("Cannot capture native test window"); }
        finally { graphics.ReleaseHdc(dc); }
        bitmap.Save(Path.Combine(output, name), System.Drawing.Imaging.ImageFormat.Png);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
}
