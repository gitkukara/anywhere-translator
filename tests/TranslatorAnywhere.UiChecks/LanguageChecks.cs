using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;
using TranslatorAnywhere.Views;

internal static partial class Program
{
    private static async Task CheckLanguageAsync()
    {
        Check(UiText.Resolve(InterfaceLanguage.System, new CultureInfo("zh-TW")) == InterfaceLanguage.Chinese, "System mode recognizes Chinese locales");
        Check(UiText.Resolve(InterfaceLanguage.System, new CultureInfo("en-US")) == InterfaceLanguage.English, "System mode recognizes English locales");
        Check(UiText.Resolve(InterfaceLanguage.System, new CultureInfo("de-DE")) == InterfaceLanguage.English, "Unsupported system languages use English");
        Check(UiText.Resolve(InterfaceLanguage.Chinese, new CultureInfo("en-US")) == InterfaceLanguage.Chinese, "Explicit language overrides system locale");
        Check(JsonSerializer.Deserialize<AppSettings>("{}")!.UiLanguage == InterfaceLanguage.System, "Existing settings default to system language");
        using var theme = new ThemeService(app);
        theme.Apply(AppTheme.Light);
        var settings = new AppSettings { UiLanguage = InterfaceLanguage.Chinese, TargetLanguage = "繁体中文", ButtonText = "我的翻译" };
        ProviderRegistry.InitializeLegacy(settings);
        settings.Providers[0].Name = "打开设置"; // Deliberately matches a UI key: user names must not be translated.
        var store = new SettingsStore();
        var window = new SettingsWindow(settings, _ => "fixture-key");
        int saves = 0;
        window.SettingsSaved += (saved, _) => { store.Save(saved); settings = saved; ++saves; };
        window.Show();
        var tabs = (TabControl)window.FindName("SettingsTabs");
        var language = (ListBox)window.FindName("UiLanguageBox");
        var target = (ComboBox)window.FindName("LanguageBox");
        var editor = (ProviderSettingsControl)window.FindName("ProviderEditor");
        try
        {
            await LayoutAsync(window);
            Check((string)((TabItem)tabs.Items[0]).Header == "外观", "Chinese settings render initially");
            language.SelectedIndex = 1;
            await LayoutAsync(window);
            Check(saves == 1 && settings.UiLanguage == InterfaceLanguage.English, "Language choice saves immediately exactly once");
            Check((string)((TabItem)tabs.Items[0]).Header == "Appearance", "Open settings change to English without restart");
            Check(settings.TargetLanguage == "繁体中文" && (string)target.SelectedValue == "繁体中文", "Interface language does not change the translation target");
            Check(((ComboBoxItem)target.SelectedItem).Content.ToString() == "Traditional Chinese", "Target label changes while its stored value stays stable");
            Check(((TextBox)window.FindName("ButtonTextBox")).Text == "我的翻译", "Custom button text is unchanged");
            Check(settings.Providers[0].Name == "打开设置", "User-defined provider name is unchanged");
            Check(store.Load().UiLanguage == InterfaceLanguage.English, "Language preference survives reloading settings");
            Check(UiText.T("已获取 12 个模型，请选择翻译模型。") == "Loaded 12 models. Choose a translation model.", "Model counts are localized");
            Check(UiText.T("未保存：请输入按钮文字。") == "Not saved: Enter the button text.", "Validation messages and prefixes are localized");
            Check(UiText.T("翻译请求失败（HTTP 418），请检查接口配置。") == "Translation failed (HTTP 418). Check the API settings.", "Dynamic HTTP errors are localized");
            window.Width = 480;
            await LayoutAsync(window);
            Check(language.ActualWidth <= window.ActualWidth - 150, "Language segments fit at minimum window width");
            Save(window, "language-english-appearance.png");
            tabs.SelectedIndex = 1;
            editor.OpenProvider(settings.Providers[0].Id);
            await LayoutAsync(window);
            Check(((TextBlock)editor.FindName("DetailTitle")).Text == "打开设置", "Custom names remain unchanged in provider details");
            Save(window, "language-english-provider.png");
            tabs.SelectedIndex = 2;
            await LayoutAsync(window);
            Save(window, "language-english-other.png");
            window.SetStatus("请输入按钮文字。");
            Check(((TextBlock)window.FindName("StatusLabel")).Text == "Enter the button text.", "New English error text is localized");
            language.SelectedIndex = 2;
            await LayoutAsync(window);
            Check((string)((TabItem)tabs.Items[0]).Header == "外观", "Switching back restores Chinese in open controls");
            Check(((ComboBoxItem)target.SelectedItem).Content.ToString() == "繁体中文", "Target label switches back without losing selection");
            tabs.SelectedIndex = 0;
            theme.Apply(AppTheme.Dark);
            await LayoutAsync(window);
            Save(window, "language-chinese-dark.png");
            UiText.Apply(InterfaceLanguage.English);
            using var service = new TranslationService(new MockHandler());
            var popup = new TranslationWindow(service, () => settings, _ => "");
            try
            {
                await popup.TranslateSelectionAsync();
                Check(((TextBlock)popup.FindName("ErrorDetailLabel")).Text == "Select some text to translate first.", "Translation popup errors use English");
                UiText.Apply(InterfaceLanguage.Chinese);
                Check(((TextBlock)popup.FindName("ErrorDetailLabel")).Text == "请先选中需要翻译的文字。", "Visible popup errors update when the language changes");
            }
            finally { popup.DisposeRenderer(); }
        }
        finally { window.Close(); UiText.Apply(InterfaceLanguage.Chinese); }
    }
}
