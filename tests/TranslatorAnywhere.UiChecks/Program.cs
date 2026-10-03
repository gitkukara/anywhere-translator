using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Native;
using TranslatorAnywhere.Services;
using TranslatorAnywhere.Views;

internal static partial class Program
{
    private static int assertions;
    private static string output = "";
    private static Application app = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-v1.1.0"));
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", Path.Combine(output, "test-data"));
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/TranslatorAnywhere;component/Views/Theme.xaml") });
        app.Resources["AppIcon"] = AppIconService.LoadWindowIcon(Path.Combine(output, "test-data"));
        app.Startup += async (_, _) =>
        {
            try
            {
                await CheckAsync(args.Contains("--preview"));
                Console.WriteLine("PASS: " + assertions + " UI assertions (mock provider; no external requests or startup writes).");
                if (!args.Contains("--preview")) app.Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        return app.Run();
    }

    private static async Task CheckAsync(bool preview)
    {
        string iconDirectory = Path.Combine(output, "icon-check");
        Directory.CreateDirectory(iconDirectory);
        string iconPath = Path.Combine(iconDirectory, "app.ico");
        File.WriteAllBytes(iconPath, new byte[] { 1, 2, 3, 4 });
        Check(AppIconService.LoadWindowIcon(iconDirectory) is BitmapSource { PixelWidth: 256 }, "Invalid override falls back to embedded icon");
        using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/TranslatorAnywhere;component/Assets/app.ico"))!.Stream)
        using (var file = File.Create(iconPath)) resource.CopyTo(file);
        var customIcon = AppIconService.LoadWindowIcon(iconDirectory);
        Check(customIcon.IsFrozen, "Custom application icon is immutable and loaded into memory");
        using (var file = File.Open(iconPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(file.Length > 0, "Custom application icon file is not kept locked");
        File.Delete(iconPath);
        var settings = new AppSettings();
        ProviderRegistry.InitializeLegacy(settings);
        var firstProvider = settings.Providers[0];
        var secondProvider = ProviderRegistry.CreateProvider("openai");
        secondProvider.SelectedModel = "other-fixture-model";
        settings.Providers.Add(secondProvider);
        using var handler = new MockHandler();
        var service = new TranslationService(handler);
        var window = new TranslationWindow(service, () => settings, id => id == firstProvider.Id ? "test-fixture-key" : "second-fixture-key");
        var work = DesktopInterop.GetWorkArea(DesktopInterop.Cursor);
        var snapshot = new SelectionSnapshot("SOURCE_TEXT_MUST_STAY_HIDDEN", "测试文本", IntPtr.Zero, Rect.Empty,
            new Point(work.Left + 180, work.Top + 150), "UI test", DateTimeOffset.Now);
        window.ShowNearSelection(snapshot);
        await window.TranslateSelectionAsync();
        await LayoutAsync(window);
        double shortHeight = window.ActualHeight;
        Check(shortHeight < 250, "Short translation stays compact");
        Check(window.ActualWidth <= 400, "Popup width stays compact");
        Check(window.FindName("SourceTextBox") is null, "Original text control is absent");
        Check(!VisibleText(window).Contains(snapshot.Text, StringComparison.Ordinal), "Original text is not rendered");
        Check(((Button)window.FindName("CancelButton")).Visibility == Visibility.Collapsed, "Stop is hidden after completion");
        Check(((TextBox)window.FindName("ResultTextBox")).Text == handler.Translation, "Translation is shown");
        Check(handler.LastKey == "test-fixture-key" && handler.LastModel == firstProvider.SelectedModel && handler.LastHost == "api.deepseek.com", "Active provider owns its endpoint, model and key");
        CheckPopupIcons(window);
        Save(window, "translation-short.png");
        await CheckCopyFeedbackAsync(window, snapshot);

        var dismissBounds = DesktopInterop.GetWindowBounds(window);
        var outside = new Point(dismissBounds.Left - 20, dismissBounds.Top - 20);
        var pin = (ToggleButton)window.FindName("PinnedBox");
        pin.IsChecked = false;
        window.DismissIfOutside(new Point(dismissBounds.Left + dismissBounds.Width / 2, dismissBounds.Top + dismissBounds.Height / 2));
        Check(window.IsVisible, "Clicking inside the translation keeps it visible");
        pin.IsChecked = true;
        window.DismissIfOutside(outside);
        Check(window.IsVisible && window.Topmost, "Pinned translation survives an outside click");
        pin.IsChecked = false;
        Check(window.IsVisible, "Unpinning does not immediately hide the translation");
        window.DismissIfOutside(outside);
        Check(!window.IsVisible, "An outside click hides an unpinned translation");
        window.ShowNearSelection(snapshot);

        settings.ActiveProviderId = secondProvider.Id;
        await window.TranslateSelectionAsync();
        Check(handler.LastKey == "second-fixture-key" && handler.LastModel == "other-fixture-model" && handler.LastHost == "api.openai.com", "Switching provider switches credentials and request destination together");
        secondProvider.Enabled = false;
        int callsBeforeDisabled = handler.Calls;
        await window.TranslateSelectionAsync();
        Check(handler.Calls == callsBeforeDisabled, "Disabled current provider never falls back or sends a request");
        settings.ActiveProviderId = firstProvider.Id;

        handler.Translation = string.Join("\n\n", Enumerable.Repeat("设计应让文字成为主角。紧凑的布局、清晰的层次和适当的留白，让翻译自然地融入阅读。", 18));
        snapshot = snapshot with { Pointer = new Point(work.Right - 24, work.Bottom - 24) };
        window.ShowNearSelection(snapshot);
        await window.TranslateSelectionAsync();
        await LayoutAsync(window);
        Check(window.ActualHeight > shortHeight && window.ActualHeight <= 520, "Long translation grows to a bounded height");
        var bounds = DesktopInterop.GetWindowBounds(window);
        Save(window, "translation-long.png");
        Check(bounds.Right <= work.Right + 2 && bounds.Bottom <= work.Bottom + 2 && bounds.Top >= work.Top - 2, $"Growing popup stays inside work area (bounds={bounds}, work={work})");

        handler.Block = true;
        var pending = window.TranslateSelectionAsync();
        await Task.Delay(60);
        Check(((Button)window.FindName("CancelButton")).Visibility == Visibility.Visible, "Stop appears during request");
        window.DismissIfOutside(new Point(-100000, -100000));
        await pending;
        Check(handler.CancellationObserved, "Hiding popup cancels active request");
        handler.Block = false;
        handler.Translation = "让语言不再成为阅读的阻碍。";
        window.ShowNearSelection(snapshot with { Pointer = new Point(work.Left + 180, work.Top + 150) });
        await window.TranslateSelectionAsync();
        await LayoutAsync(window);
        Check(window.ActualHeight < 250, "Popup shrinks again for short translation");

        var emptyKey = new TranslationWindow(service, () => settings, _ => "");
        emptyKey.ShowNearSelection(snapshot with { Pointer = new Point(work.Left + 200, work.Top + 170) });
        await emptyKey.TranslateSelectionAsync();
        await LayoutAsync(emptyKey);
        Check(((Button)emptyKey.FindName("ConfigureButton")).IsVisible, "Missing API key has actionable configuration button");
        Check(((TextBlock)emptyKey.FindName("ErrorDetailLabel")).IsVisible, "Error explanation is visible");
        Save(emptyKey, "translation-error.png");
        emptyKey.Hide();

        var settingsWindow = new SettingsWindow(settings, _ => "fixture-ui-key");
        settingsWindow.Show();
        await LayoutAsync(settingsWindow);
        Save(settingsWindow, "settings-default.png");
        Check(((ScrollViewer)settingsWindow.FindName("AppearanceScrollViewer")).VerticalOffset < 1,
            "Opening settings keeps appearance options visible without jumping to the selected preview text");
        Check(settingsWindow.ActualWidth <= 540 && settingsWindow.ActualHeight <= 580, "Settings window uses the narrow layout");
        Check(settingsWindow.Icon is not null, "Window and taskbar icon are present");
        Check(settingsWindow.FindName("SaveButton") is not Button && !FindAllVisual<Button>(settingsWindow).Any(button => button.Content is string label && label is "保存" or "应用预览"), "Settings use automatic saving without a manual save or apply-preview action");
        var tabs = FindVisual<TabControl>(settingsWindow)!;
        Check(tabs.Items.Cast<TabItem>().Select(tab => tab.Header?.ToString()).SequenceEqual(new[] { "外观", "翻译服务", "其他设置" }), "Compact navigation has the requested three sections");
        for (int index = 0; index < tabs.Items.Count; index++)
        {
            tabs.SelectedIndex = index;
            ((TabItem)tabs.Items[index]).Focus();
            if (index == 0) ((ScrollViewer)settingsWindow.FindName("AppearanceScrollViewer")).ScrollToTop();
            await LayoutAsync(settingsWindow);
            Save(settingsWindow, "settings-" + index + ".png");
            Check(((Button)settingsWindow.FindName("TestSelectionButton")).IsVisible == (index == 0), "Test-selection action belongs only to appearance");
        }
        tabs.SelectedIndex = 1;
        var languageChoice = (ComboBox)settingsWindow.FindName("LanguageBox");
        try
        {
            languageChoice.IsDropDownOpen = true;
            await LayoutAsync(settingsWindow);
            var languagePopup = languageChoice.Template.FindName("PART_Popup", languageChoice) as Popup;
            Save(settingsWindow, "settings-language-dropdown.png", languagePopup?.Child as FrameworkElement);
        }
        finally { languageChoice.IsDropDownOpen = false; }
        await CheckNarrowSettingsLayoutAsync(settingsWindow, settings);
        tabs.SelectedIndex = 0;
        await LayoutAsync(settingsWindow);
        ((TextBox)settingsWindow.FindName("ColorBox")).Focus();
        await LayoutAsync(settingsWindow);
        Save(settingsWindow, "settings-color-focused.png");
        Check(settingsWindow.FindName("LaunchAtStartupCheck") is CheckBox { IsChecked: false }, "Startup is opt-in");
        settingsWindow.SetStartupState(true);
        Check(settingsWindow.FindName("LaunchAtStartupCheck") is CheckBox { IsChecked: true }, "Effective startup state can be resynchronized");
        settingsWindow.SetStartupState(false);
        tabs.SelectedIndex = 1;
        await LayoutAsync(settingsWindow);
        await CheckProviderEditorAsync(settingsWindow, settings);
        tabs.SelectedIndex = 0;
        await CheckAutomaticSavingAsync();
        await CheckRestoredDamagedConfigurationAsync();
        await CheckThemesAsync(window);
        if (!preview) { settingsWindow.Close(); window.Hide(); service.Dispose(); }
        else
        {
            // Interactive inspection also stays on fixtures; no button can send a real API request.
            var providerEditor = (ProviderSettingsControl)settingsWindow.FindName("ProviderEditor");
            providerEditor.ModelLoader = (provider, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { provider.SelectedModel, "fixture-extra-model" });
            providerEditor.ConnectionTester = (_, _, _, _) => Task.FromResult("连接成功（本地模拟）");
            settingsWindow.Closed += (_, _) => { window.Hide(); service.Dispose(); app.Shutdown(); };
        }
    }

    private static async Task CheckAutomaticSavingAsync()
    {
        string directory = Path.GetFullPath(Path.Combine(output, "autosave-" + Guid.NewGuid().ToString("N")));
        string? previousDirectory = Environment.GetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR");
        Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", directory);
        SettingsWindow? window = null;
        bool failWrite = false;
        try
        {
            var store = new SettingsStore();
            var initial = new AppSettings { ButtonColor = "#FF3390EC" };
            ProviderRegistry.InitializeLegacy(initial);
            Guid originalId = initial.Providers[0].Id;
            const string originalKey = "fixture-autosave-original-key";
            const string replacementKey = "fixture-autosave-replacement-key";
            store.SaveConfiguration(initial, new Dictionary<Guid, string> { [originalId] = originalKey });
            window = new SettingsWindow(store.Load(), store.ReadProviderApiKey);
            int saved = 0;
            int attempts = 0;
            IReadOnlyDictionary<Guid, string> lastKeys = new Dictionary<Guid, string>();
            window.SettingsSaved += (settings, keys) =>
            {
                attempts++;
                if (failWrite) throw new IOException("fixture write failure; key must remain pending");
                store.SaveConfiguration(settings, keys);
                lastKeys = new Dictionary<Guid, string>(keys);
                saved++;
            };
            var editor = (ProviderSettingsControl)window.FindName("ProviderEditor");
            editor.ModelLoader = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "fixture-model-a", "fixture-model-b" });
            editor.ConnectionTester = (_, _, _, _) => Task.FromResult("连接成功（本地模拟）");
            int notifications = 0;
            editor.ConfigurationChanged += () => notifications++;
            window.Show();
            await LayoutAsync(window);
            window.SetEnabledState(false);
            window.SetEnabledState(true);
            window.SetStartupState(true);
            window.SetStartupState(false);
            await Task.Delay(750);
            Check(saved == 0 && attempts == 0 && notifications == 0, "Opening, loading and synchronizing effective settings performs no save or configuration-change notification");
            Check(((TextBox)window.FindName("ColorBox")).Text == "#3390EC" && saved == 0,
                "A legacy opaque ARGB color displays as six-digit RGB without saving on open");

            var tabs = (TabControl)window.FindName("SettingsTabs");
            tabs.SelectedIndex = 1;
            await LayoutAsync(window);
            await Task.Delay(750);
            Check(saved == 0 && attempts == 0 && notifications == 0,
                "First display of language choices does not save unchanged form values");
            tabs.SelectedIndex = 0;
            await LayoutAsync(window);
            var label = (TextBox)window.FindName("ButtonTextBox");
            label.Text = "自动";
            await Task.Delay(100);
            label.Text = "自动保";
            await Task.Delay(100);
            label.Text = "自动保存";
            await Task.Delay(250);
            Check(saved == 0, "Typing waits for the debounce interval instead of saving each character");
            await Task.Delay(500);
            Check(saved == 1 && store.Load().ButtonText == "自动保存", "A burst of text edits produces one durable automatic save");

            int before = saved;
            ((ListBox)window.FindName("ModeBox")).SelectedIndex = (int)ButtonVisualMode.Symbol;
            ((ListBox)window.FindName("SizeChoice")).SelectedIndex = 2;
            ((TextBox)window.FindName("ColorBox")).Text = "#8A3FFC";
            ((ListBox)window.FindName("AnchorBox")).SelectedValue = ButtonAnchor.SelectionTopLeft.ToString();
            ((TextBox)window.FindName("OffsetXBox")).Text = "-16";
            ((TextBox)window.FindName("OffsetYBox")).Text = "18";
            await DrainAsync();
            Check(window.FlushPendingChanges(), "Valid appearance edits can be flushed synchronously");
            var persisted = store.Load();
            Check(saved == before + 1 && persisted.ButtonMode == ButtonVisualMode.Symbol && persisted.ButtonSize == 40
                && persisted.ButtonColor == "#8A3FFC"
                && persisted.Anchor == ButtonAnchor.SelectionTopLeft && persisted.OffsetX == -16 && persisted.OffsetY == 18,
                "One save commits appearance and placement together");

            before = saved;
            var transparency = (ListBox)window.FindName("TransparencyChoice");
            Check(transparency.SelectedIndex == 3 && persisted.ButtonTransparency == 0, "Floating buttons start with zero transparency");
            var floatingButton = new SelectionButtonWindow();
            var work = DesktopInterop.GetWorkArea(DesktopInterop.Cursor);
            var selection = new SelectionSnapshot("fixture transparency selection", "UI fixture", IntPtr.Zero,
                new Rect(work.Left + 80, work.Top + 80, 120, 20), new Point(work.Left + 200, work.Top + 100), "UI fixture", DateTimeOffset.Now);
            try
            {
                foreach (double percentage in new[] { 0d, 25d, 50d, 75d })
                {
                    transparency.SelectedIndex = 3 - (int)(percentage / 25);
                    floatingButton.ShowFor(selection, new AppSettings { ButtonMode = ButtonVisualMode.Symbol, ButtonTransparency = percentage });
                    await LayoutAsync(floatingButton);
                    var tile = (Border)floatingButton.FindName("Tile");
                    if (percentage == 0)
                        Check(floatingButton.FindName("Symbol") is HugeIcon { Kind: HugeIconKind.Translate, Visibility: Visibility.Visible },
                            "Desktop symbol mode uses the shared Translate icon");
                    double expected = 1 - percentage / 100;
                    Check(Near(EffectiveOpacity(tile), expected),
                        "Desktop floating button uses the configured " + percentage + " percent transparency");
                    tile.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
                    bool didNotBecomeOpaque = EffectiveOpacity(tile) <= expected + 0.000001;
                    tile.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                    Check(didNotBecomeOpaque && Near(EffectiveOpacity(tile), expected),
                        "Hovering preserves the configured opacity at " + percentage + " percent transparency");
                }
            }
            finally { floatingButton.Close(); }
            transparency.SelectedIndex = 1;
            Check(window.FlushPendingChanges() && saved == before + 1 && store.Load().ButtonTransparency == 50,
                "Transparency changes use the same durable automatic-save path");
            ((ScrollViewer)window.FindName("AppearanceScrollViewer")).ScrollToEnd();
            await LayoutAsync(window);
            Save(window, "settings-transparency-50.png");
            var reopened = new SettingsWindow(store.Load(), store.ReadProviderApiKey);
            int reopenedWrites = 0;
            reopened.SettingsSaved += (_, _) => reopenedWrites++;
            try
            {
                reopened.Show();
                await LayoutAsync(reopened);
                await Task.Delay(750);
                Check(((ListBox)reopened.FindName("TransparencyChoice")).SelectedIndex == 1
                    && reopenedWrites == 0,
                    "Reopening restores saved transparency without an initialization write");
            }
            finally { reopened.Close(); }
            window.Activate();

            tabs.SelectedIndex = 1;
            await LayoutAsync(window);
            var language = (ComboBox)window.FindName("LanguageBox");
            Check(!language.IsEditable && language.Items.Cast<ComboBoxItem>().Select(item => item.Content?.ToString())
                .SequenceEqual(new[] { "简体中文", "繁体中文", "English", "日本語", "한국어" }), "Target language remains a fixed choice among the original five languages");
            language.SelectedValue = "English";
            await DrainAsync();
            Check(window.FlushPendingChanges() && store.Load().TargetLanguage == "English", "Choosing English automatically saves the selected target language");

            tabs.SelectedIndex = 2;
            await LayoutAsync(window);
            ((CheckBox)window.FindName("EnabledBox")).IsChecked = false;
            ((CheckBox)window.FindName("HotkeyFallbackBox")).IsChecked = false;
            ((TextBox)window.FindName("DelayBox")).Text = "260";
            ((TextBox)window.FindName("AutoHideBox")).Text = "12";
            ((TextBox)window.FindName("ExcludedAppsBox")).Text = "notepad.exe; code.exe";
            Check(window.FlushPendingChanges(), "Other settings participate in automatic saving");
            persisted = store.Load();
            Check(!persisted.Enabled && !persisted.ClipboardFallbackOnHotkey && persisted.SelectionDelayMs == 260
                && persisted.AutoHideSeconds == 12 && persisted.ExcludedApplications.SequenceEqual(new[] { "notepad", "code" }),
                "Other settings remain consistent in the durable configuration");

            before = saved;
            ((TextBox)window.FindName("DelayBox")).Text = "-";
            tabs.SelectedIndex = 0;
            await LayoutAsync(window);
            ((ListBox)window.FindName("ModeBox")).SelectedIndex = (int)ButtonVisualMode.Text;
            label.Focus();
            await DrainAsync();
            var focused = System.Windows.Input.Keyboard.FocusedElement;
            Check(!window.FlushPendingChanges(), "Incomplete numeric input is not persisted");
            await Task.Delay(750);
            Check(saved == before && store.Load().SelectionDelayMs == 260, "Invalid input never replaces the last saved configuration");
            Check(tabs.SelectedIndex == 0 && ReferenceEquals(focused, System.Windows.Input.Keyboard.FocusedElement), "Background validation preserves the user's tab and keyboard focus");
            window.Close();
            Check(window.IsVisible, "An invalid pending edit prevents closing without discarding the draft");
            ((TextBox)window.FindName("DelayBox")).Text = "280";
            Check(window.FlushPendingChanges() && store.Load().SelectionDelayMs == 280, "Correcting incomplete input saves the retained changes");

            tabs.SelectedIndex = 1;
            await LayoutAsync(window);
            editor.OpenProvider(originalId);
            await LayoutAsync(window);
            before = notifications;
            editor.ShowProviderList();
            editor.OpenProvider(originalId);
            editor.AcceptSaved();
            Check(notifications == before, "Editor loading, list rendering and acknowledging saved keys are not user configuration changes");

            var keyBox = (PasswordBox)editor.FindName("ProviderApiKeyBox");
            failWrite = true;
            keyBox.Password = replacementKey;
            Check(!window.FlushPendingChanges(), "A failed write reports an unsuccessful flush");
            Check(store.ReadProviderApiKey(originalId) == originalKey
                && editor.PendingApiKeys.TryGetValue(originalId, out string? retainedKey) && retainedKey == replacementKey,
                "Save failure keeps the new credential pending and preserves the saved credential");
            window.Close();
            Check(window.IsVisible && editor.PendingApiKeys.ContainsKey(originalId), "A failed close preserves the open editor and its pending credential");
            failWrite = false;
            ((TextBox)window.FindName("ColorBox")).Text = "#FF3390EC";
            Check(!window.FlushPendingChanges() && editor.PendingApiKeys.ContainsKey(originalId), "New ARGB color input is rejected while preserving the pending credential after a write failure");
            ((TextBox)window.FindName("ColorBox")).Text = "#3390EC";
            Check(window.FlushPendingChanges(), "A corrected edit can retry the pending failed save");
            Check(store.ReadProviderApiKey(originalId) == replacementKey && editor.PendingApiKeys.Count == 0, "Only a successful save acknowledges the replacement credential");
            keyBox.Password = "";
            Check(window.FlushPendingChanges() && lastKeys.TryGetValue(originalId, out string? removedKey) && removedKey == ""
                && store.ReadProviderApiKey(originalId) == "", "Clearing a saved API key is emitted and durably removes the credential");

            before = notifications;
            Guid addedId = editor.AddPreset("gemini");
            Check(notifications > before, "Adding a provider emits a configuration change");
            ((PasswordBox)editor.FindName("ProviderApiKeyBox")).Password = "fixture-added-provider-key";
            ((TextBox)editor.FindName("ModelListBox")).Text = "fixture-model-a\nfixture-model-b";
            await LayoutAsync(window);
            var selectedModel = (ComboBox)editor.FindName("SelectedModelCombo");
            before = notifications;
            selectedModel.SelectedItem = "fixture-model-b";
            await DrainAsync();
            Check(notifications > before && editor.DraftProviders.Single(profile => profile.Id == addedId).SelectedModel == "fixture-model-b",
                "Choosing a model commits the new selection and emits a change before focus leaves");
            Check(window.FlushPendingChanges() && store.Load().Providers.Single(profile => profile.Id == addedId).SelectedModel == "fixture-model-b",
                "The selected model is saved through the settings automatic-save path");
            before = notifications;
            var modelInput = FindVisual<TextBox>(selectedModel) ?? throw new InvalidOperationException("Editable model textbox is missing.");
            modelInput.Text = "fixture-manual-model";
            await DrainAsync();
            Check(notifications > before && window.FlushPendingChanges()
                && store.Load().Providers.Single(profile => profile.Id == addedId).SelectedModel == "fixture-manual-model",
                "A manually typed model saves before focus leaves its editable ComboBox");
            ((TextBox)editor.FindName("ModelListBox")).Text = "fixture-model-a\nfixture-model-b\nfixture-model-c";
            await DrainAsync();
            Check(selectedModel.Text == "fixture-manual-model" && window.FlushPendingChanges()
                && store.Load().Providers.Single(profile => profile.Id == addedId).SelectedModel == "fixture-manual-model",
                "Editing model choices preserves the manually entered selected model during ItemsSource replacement");

            before = notifications;
            ((CheckBox)editor.FindName("ProviderEnabledCheck")).IsChecked = false;
            Check(notifications > before && window.FlushPendingChanges()
                && !store.Load().Providers.Single(profile => profile.Id == addedId).Enabled, "Stopping a provider emits and persists its enabled state");
            before = notifications;
            ((Button)editor.FindName("RemoveProviderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(notifications > before && window.FlushPendingChanges()
                && store.Load().Providers.All(profile => profile.Id != addedId), "Deleting a provider emits and persists the removal");

            tabs.SelectedIndex = 0;
            await LayoutAsync(window);
            before = saved;
            label.Text = "关闭即保存";
            window.Close();
            Check(saved == before + 1 && store.Load().ButtonText == "关闭即保存", "Closing flushes a valid edit before its debounce timer expires");
            window = null;
        }
        finally
        {
            failWrite = false;
            if (window is not null)
            {
                // Restore valid fixture inputs so cancellation of an expected failed close
                // cannot leave a test window alive after a later assertion fails.
                ((TextBox)window.FindName("DelayBox")).Text = "280";
                ((TextBox)window.FindName("ColorBox")).Text = "#3390EC";
                ((ComboBox)window.FindName("LanguageBox")).SelectedValue = "English";
                window.Close();
            }
            Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", previousDirectory);
            string allowedRoot = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (directory.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(directory).StartsWith("autosave-", StringComparison.Ordinal) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CheckRestoredDamagedConfigurationAsync()
    {
        var original = new AppSettings { ButtonText = "翻" };
        ProviderRegistry.InitializeLegacy(original);
        original.Providers[0].BaseUrl = "http://unreachable-fixture.invalid/v1";
        var window = new SettingsWindow(original, _ => "fixture-damaged-config-key");
        int attempts = 0;
        window.SettingsSaved += (_, _) => attempts++;
        try
        {
            window.Show();
            await LayoutAsync(window);
            var label = (TextBox)window.FindName("ButtonTextBox");
            string initialText = label.Text;
            label.Text = "临时文字";
            Check(!window.FlushPendingChanges() && attempts == 0,
                "Changing a form with an existing invalid endpoint does not write an invalid configuration");
            label.Text = initialText;
            Check(window.FlushPendingChanges() && attempts == 0,
                "Restoring original form values clears pending edits before validating a pre-existing invalid endpoint");
            window.Close();
            Check(!window.IsVisible && attempts == 0,
                "A restored unchanged draft with a pre-existing invalid endpoint closes without any save");
        }
        finally
        {
            if (window.IsVisible)
            {
                ((TextBox)window.FindName("ButtonTextBox")).Text = original.ButtonText;
                window.Close();
            }
        }
    }

    private static async Task CheckNarrowSettingsLayoutAsync(SettingsWindow window, AppSettings settings)
    {
        var tabs = (TabControl)window.FindName("SettingsTabs");
        var appearance = (ScrollViewer)window.FindName("AppearanceScrollViewer");
        var testButton = (Button)window.FindName("TestSelectionButton");
        var editor = (ProviderSettingsControl)window.FindName("ProviderEditor");
        double originalWidth = window.Width;
        double originalHeight = window.Height;
        try
        {
            tabs.SelectedIndex = 0;
            appearance.ScrollToTop();
            await LayoutAsync(window);
            Check(window.FindName("AppearancePreviewCard") is null && testButton.Content?.ToString() == "点这里预览测试",
                "Appearance replaces the embedded preview with the renamed test action");
            var actionParent = (StackPanel)testButton.Parent;
            Rect actionBefore = testButton.TransformToAncestor(actionParent).TransformBounds(new Rect(testButton.RenderSize));
            var card = (Border)window.FindName("AppearanceOptionsCard");
            double cardBottom = card.TranslatePoint(new Point(0, card.ActualHeight), actionParent).Y;
            Check(Math.Abs(actionBefore.Top - cardBottom - 4) < 1
                && Math.Abs(actionBefore.Right - actionParent.ActualWidth) < 1,
                "The test action follows the appearance card with a four-pixel gap");
            appearance.ScrollToEnd();
            await LayoutAsync(window);
            Rect actionAfter = testButton.TransformToAncestor(actionParent).TransformBounds(new Rect(testButton.RenderSize));
            Check(actionBefore == actionAfter, "The test action retains its spacing within the scrolling content");
            int requests = 0;
            window.TestSelectionRequested += () => requests++;
            testButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(requests == 1, "The test action opens the existing selection test");
            Save(window, "settings-preview.png");

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await LayoutAsync(window);
            appearance.ScrollToTop();
            await LayoutAsync(window);
            Save(window, "settings-minimum-options.png");
            appearance.ScrollToEnd();
            await LayoutAsync(window);
            Check(InViewport(testButton, ScrollViewport(appearance)),
                "The test action remains reachable by scrolling at minimum window size");
            Save(window, "settings-minimum-preview.png");

            tabs.SelectedIndex = 1;
            editor.OpenProvider(settings.Providers[0].Id);
            await LayoutAsync(window);
            var details = (FrameworkElement)editor.FindName("DetailView");
            var detailsScroll = FindVisual<ScrollViewer>(details) ?? throw new InvalidOperationException("Provider detail scrolling is missing.");
            var model = (ComboBox)editor.FindName("SelectedModelCombo");
            var endpoint = (TextBox)editor.FindName("ProviderBaseUrlBox");
            string originalEndpoint = endpoint.Text;
            string originalModel = model.Text;
            const string longEndpoint = "https://fixture-provider.example.com/gateway/department/project/compatible-mode/v1";
            const string longModel = "fixture-provider/translation-large-context-model-2026-long-name";
            try
            {
                endpoint.Text = longEndpoint;
                model.Text = longModel;
                model.BringIntoView();
                await LayoutAsync(window);
                var content = (FrameworkElement)window.Content;
                Check(InViewport(model, ScrollViewport(detailsScroll))
                    && InViewport(endpoint, ScrollViewport(detailsScroll))
                    && !endpoint.IsReadOnly && model.IsEditable && endpoint.Text == longEndpoint && model.Text == longModel
                    && InViewport((Button)editor.FindName("TestConnectionButton"), content)
                    && InViewport((Button)editor.FindName("RemoveProviderButton"), content),
                    "Long provider endpoints and models remain editable and footer actions reachable at minimum window size");
                Save(window, "settings-minimum-provider.png");
            }
            finally
            {
                endpoint.Text = originalEndpoint;
                model.Text = originalModel;
            }

            tabs.SelectedIndex = 2;
            await LayoutAsync(window);
            var other = (ScrollViewer)window.FindName("OtherScrollViewer");
            other.ScrollToEnd();
            await LayoutAsync(window);
            Check(InViewport((CheckBox)window.FindName("AutomaticFallbackBox"), ScrollViewport(other))
                && InViewport((TextBox)window.FindName("FallbackAppsBox"), ScrollViewport(other)),
                "Lower compatibility settings remain reachable by scrolling at minimum window size");
            Save(window, "settings-minimum-other.png");
        }
        finally
        {
            editor.ShowProviderList();
            window.Width = originalWidth;
            window.Height = originalHeight;
            tabs.SelectedIndex = 0;
            appearance.ScrollToTop();
            await LayoutAsync(window);
        }
    }

    private static ScrollContentPresenter ScrollViewport(ScrollViewer scroll) => FindVisual<ScrollContentPresenter>(scroll)
        ?? throw new InvalidOperationException("The scrolling viewport is missing.");

    private static bool InViewport(FrameworkElement element, FrameworkElement viewport)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        Rect bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }

    private static void CheckPopupIcons(TranslationWindow window)
    {
        var actions = FindAllVisual<ButtonBase>(window).Select(button => new { Button = button, Icon = ActionIcon(button) })
            .Where(action => action.Icon is not null).ToArray();
        var expected = new[] { HugeIconKind.Pin, HugeIconKind.Settings, HugeIconKind.Stop, HugeIconKind.Refresh, HugeIconKind.Copy };
        Check(actions.Select(action => action.Icon!.Kind).OrderBy(kind => kind).SequenceEqual(expected.OrderBy(kind => kind)),
            "All popup actions use shared vector icons instead of font glyphs or text labels");
        Check(actions.All(action => action.Button.ToolTip is string tooltip && !string.IsNullOrWhiteSpace(tooltip)
            && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(action.Button))),
            "Every popup icon action has a tooltip and an accessible name");

        static HugeIcon? ActionIcon(ContentControl button)
        {
            if (button.Content is DependencyObject content)
            {
                var icon = FindAllVisual<HugeIcon>(content).FirstOrDefault(item => item.Visibility == Visibility.Visible);
                if (icon is not null) return icon;
            }
            return FindAllVisual<HugeIcon>(button).FirstOrDefault(item => item.Visibility == Visibility.Visible);
        }
    }

    private static async Task CheckCopyFeedbackAsync(TranslationWindow window, SelectionSnapshot selection)
    {
        var button = (Button)window.FindName("CopyButton");
        var copy = (HugeIcon)window.FindName("CopyIcon");
        var success = (HugeIcon)window.FindName("CopySuccessIcon");
        Check(copy.Kind == HugeIconKind.Copy && copy.Visibility == Visibility.Visible && success.Kind == HugeIconKind.Check
            && success.Visibility == Visibility.Collapsed && AutomationProperties.GetName(button) == "复制译文",
            "Copy initially shows its normal icon and accessible action");
        // Exercise the existing UI state transition without touching the system clipboard.
        var feedback = typeof(TranslationWindow).GetMethod("ShowCopiedFeedback", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The copy-feedback state transition is missing.");
        feedback.Invoke(window, null);
        Check(copy.Visibility == Visibility.Collapsed && success.Visibility == Visibility.Visible
            && button.ToolTip?.ToString() == "已复制" && AutomationProperties.GetName(button) == "已复制",
            "Copy confirmation swaps to a Check icon and updates its action description");
        await LayoutAsync(window);
        Save(window, "translation-copied.png");
        await Task.Delay(1750);
        await DrainAsync();
        Check(copy.Visibility == Visibility.Visible && success.Visibility == Visibility.Collapsed
            && button.ToolTip?.ToString() == "复制译文" && AutomationProperties.GetName(button) == "复制译文",
            "Transient copy confirmation returns to the original action after its timer");
        feedback.Invoke(window, null);
        window.Hide();
        Check(copy.Visibility == Visibility.Visible && success.Visibility == Visibility.Collapsed,
            "Hiding the popup clears pending copy confirmation");
        window.ShowNearSelection(selection);
    }

    private static async Task CheckProviderEditorAsync(SettingsWindow window, AppSettings original)
    {
        var editor = FindVisual<ProviderSettingsControl>(window) ?? throw new InvalidOperationException("Provider manager is missing.");
        Check(editor.IsVisible, "Provider manager is present");
        int originalCount = original.Providers.Count;
        Guid originalId = original.Providers[0].Id;
        editor.OpenCatalog();
        await LayoutAsync(window);
        ((Button)editor.FindName("CatalogBackButton")).Focus();
        Save(window, "providers-catalog.png");
        Check(((ItemsControl)editor.FindName("CatalogItems")).Items.Count == 7 && editor.FindName("CatalogSearchBox") is null, "Catalogue shows seven presets without search");
        ((Button)editor.FindName("CatalogBackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((FrameworkElement)editor.FindName("ListView")).IsVisible && editor.EditingProviderId is null,
            "The catalogue return action opens the provider list");
        editor.OpenCatalog();
        Guid addedId = editor.AddPreset("gemini");
        ((PasswordBox)editor.FindName("ProviderApiKeyBox")).Password = "fixture-gemini-key";
        ((ComboBox)editor.FindName("SelectedModelCombo")).Text = "gemini-fixture";
        ((TextBox)editor.FindName("ModelListBox")).Text = "gemini-fixture";
        editor.ActivateProvider(addedId);
        var saved = new AppSettings();
        editor.ApplyTo(saved);
        Check(saved.ActiveProviderId == addedId && saved.Providers.Single(provider => provider.Id == addedId).SelectedModel == "gemini-fixture", "Provider draft selects exact model and active instance");
        Check(editor.PendingApiKeys.TryGetValue(addedId, out var key) && key == "fixture-gemini-key" && !editor.PendingApiKeys.ContainsKey(originalId), "Only edited credentials are included in pending save");
        Check(!JsonSerializer.Serialize(saved).Contains("fixture-gemini-key", StringComparison.Ordinal), "Provider configuration contains no plaintext key");
        Check(original.Providers.Count == originalCount && original.ActiveProviderId == originalId, "Editing provider drafts does not mutate live settings");
        await LayoutAsync(window);
        ((Button)editor.FindName("DetailBackButton")).Focus();
        Save(window, "provider-detail.png");
        ((Button)editor.FindName("DetailBackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((FrameworkElement)editor.FindName("ListView")).IsVisible && editor.EditingProviderId is null,
            "The detail return action opens the provider list without losing its draft");
        editor.OpenProvider(addedId);
        await LayoutAsync(window);

        var pendingModels = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        editor.ModelLoader = (_, _, token) => { observedToken = token; return pendingModels.Task; };
        ((Button)editor.FindName("RefreshModelsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(observedToken.CanBeCanceled, "Model refresh is cancelable");
        editor.OpenProvider(originalId);
        Check(observedToken.IsCancellationRequested, "Switching provider cancels its previous model request");
        pendingModels.SetResult(new[] { "stale-model-must-not-arrive" });
        await LayoutAsync(window);
        Check(!editor.DraftProviders.Any(provider => provider.Models.Contains("stale-model-must-not-arrive")), "Late model responses cannot overwrite another provider");

        editor.OpenProvider(addedId);
        editor.ModelLoader = (_, _, _) => Task.FromException<IReadOnlyList<string>>(new HttpRequestException("fixture-secret must not appear"));
        ((Button)editor.FindName("RefreshModelsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await LayoutAsync(window);
        Check(editor.DraftProviders.Single(provider => provider.Id == addedId).Models.Contains("gemini-fixture"), "Failed model refresh preserves the existing model list");
        Check(!((TextBlock)editor.FindName("ProviderStatusLabel")).Text.Contains("fixture-secret", StringComparison.Ordinal), "Provider errors do not expose raw exception messages");

        window.AcceptSaved();
        Check(editor.PendingApiKeys.Count == 0, "Successful save acknowledges pending credential changes");
        ((PasswordBox)editor.FindName("ProviderApiKeyBox")).Password = "";
        Check(editor.PendingApiKeys.TryGetValue(addedId, out var cleared) && cleared == "", "Clearing a just-saved key persists as a new change");
        ((Button)editor.FindName("RemoveProviderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        editor.ApplyTo(saved);
        Check(saved.Providers.All(provider => provider.Id != addedId) && !editor.PendingApiKeys.ContainsKey(addedId), "Removing a provider never submits an orphaned pending credential");
        editor.ActivateProvider(originalId);
        editor.ShowProviderList();
        await LayoutAsync(window);
        Save(window, "providers-list.png");
    }

    private static async Task LayoutAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        await Task.Delay(80);
    }

    private static async Task DrainAsync() => await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < .000001;

    private static double EffectiveOpacity(DependencyObject element)
    {
        double opacity = 1;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui) opacity *= ui.Opacity;
        return opacity;
    }

    private static void Save(Window window, string file, FrameworkElement? popup = null)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        if (popup is not null && popup.ActualWidth > 0 && popup.ActualHeight > 0)
        {
            var overlay = new DrawingVisual();
            using (DrawingContext drawing = overlay.RenderOpen())
            {
                Point origin = window.PointFromScreen(popup.PointToScreen(new Point()));
                drawing.DrawRectangle(new VisualBrush(popup), null, new Rect(origin, popup.RenderSize));
            }
            bitmap.Render(overlay);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, file));
        encoder.Save(stream);
    }

    private static T? FindVisual<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(node, index)) is { } result) return result;
        return null;
    }

    private static IEnumerable<T> FindAllVisual<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) yield return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (T child in FindAllVisual<T>(VisualTreeHelper.GetChild(node, index))) yield return child;
    }

    private static string VisibleText(DependencyObject node)
    {
        string own = node is TextBlock block ? block.Text : node is TextBox box ? box.Text : "";
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) own += VisibleText(VisualTreeHelper.GetChild(node, index));
        return own;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("UI check failed: " + message);
        assertions++;
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        public string Translation { get; set; } = "让语言不再成为阅读的阻碍。";
        public bool Block { get; set; }
        public bool CancellationObserved { get; private set; }
        public int Calls { get; private set; }
        public string? LastKey { get; private set; }
        public string? LastModel { get; private set; }
        public string? LastHost { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            LastKey = request.Headers.Authorization?.Parameter;
            LastHost = request.RequestUri?.Host;
            using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token))) LastModel = body.RootElement.GetProperty("model").GetString();
            if (Block)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { CancellationObserved = true; throw; }
            }
            string json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Translation }, finish_reason = "stop" } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
