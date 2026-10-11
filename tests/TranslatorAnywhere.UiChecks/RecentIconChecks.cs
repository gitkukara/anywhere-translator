using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;
using TranslatorAnywhere.Views;

internal static partial class Program
{
    private static async Task CheckRecentIconsAsync()
    {
        string? previous = Environment.GetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR");
        string directory = Path.Combine(output, "recent-icons-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", directory);
        SettingsWindow? window = null;
        try
        {
            var store = new SettingsStore();
            var settings = new AppSettings { ButtonMode = ButtonVisualMode.Icon };
            ProviderRegistry.InitializeLegacy(settings);
            window = new SettingsWindow(settings, _ => "") { ImportIcon = store.ImportIcon };
            int writes = 0;
            window.SettingsSaved += (value, _) => { store.Save(value); writes++; };
            window.Show();
            await LayoutAsync(window);
            Check(((ItemsControl)window.FindName("RecentIcons")).Visibility == Visibility.Collapsed,
                "Never imported icons do not leave an empty recent-icon strip");
            Check(writes == 0, "Opening the icon history performs no initialization save");
            Save(window, "recent-icons-empty.png");
            var imports = new List<string>();
            string[] colors = { "#3390ec", "#a84ac9", "#ff4500", "#22a877" };
            for (int index = 0; index < 4; index++)
            {
                string source = Path.Combine(directory, "source.svg");
                string shape = index % 2 == 0 ? "<circle cx=\"16\" cy=\"16\" r=\"12\"" : "<rect x=\"4\" y=\"4\" width=\"24\" height=\"24\" rx=\"4\"";
                File.WriteAllText(source, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\">" + shape + " fill=\"" + colors[index] + "\"/></svg>");
                typeof(SettingsWindow).GetMethod("ImportIconFile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { source });
                Check(window.FlushPendingChanges(), "Import persists icon selection and history together");
                imports.Add(store.Load().IconPath);
                File.Delete(source);
                await LayoutAsync(window);
                Check(((ItemsControl)window.FindName("RecentIcons")).Items.Count == Math.Min(index + 1, 3),
                    "History shows exactly the available icons and never more than three");
                if (index == 0) Save(window, "recent-icons-one.png");
            }
            var strip = (ItemsControl)window.FindName("RecentIcons");
            var buttons = FindAllVisual<ToggleButton>(strip).ToArray();
            string[] expected = imports.Skip(1).Reverse().ToArray();
            Check(buttons.Select(button => (string)button.Tag).SequenceEqual(expected), "Thumbnail order follows import time");
            Check(buttons.Count(button => button.IsChecked == true) == 1 && (string)buttons[0].Tag == store.Load().IconPath,
                "The currently selected icon has one selection marker");
            var importButton = (Button)window.FindName("ImportIconButton");
            var stripRight = strip.TranslatePoint(new Point(strip.ActualWidth, 0), window);
            var importLeft = importButton.TranslatePoint(new Point(), window);
            Check(stripRight.X <= importLeft.X && importLeft.X + importButton.ActualWidth < window.ActualWidth,
                "All three thumbnails fit beside the import button without clipping");
            Save(window, "recent-icons-three-light.png");
            using (var theme = new ThemeService(app))
            {
                theme.Apply(AppTheme.Dark);
                await LayoutAsync(window);
                Save(window, "recent-icons-three-dark.png");
                theme.Apply(AppTheme.Light);
            }
            buttons[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(window.FlushPendingChanges() && store.Load().IconPath == imports[1], "Clicking a thumbnail durably switches the current custom icon");
            Check(store.Load().RecentIconPaths.SequenceEqual(expected), "Thumbnail selection preserves import order");
            window.Close();
            window = new SettingsWindow(store.Load(), _ => "");
            int reopenedWrites = 0;
            window.SettingsSaved += (value, _) => { store.Save(value); reopenedWrites++; };
            window.Show();
            await LayoutAsync(window);
            strip = (ItemsControl)window.FindName("RecentIcons");
            buttons = FindAllVisual<ToggleButton>(strip).ToArray();
            Check(buttons.Length == 3 && buttons.Single(button => button.IsChecked == true).Tag is string selected && selected == imports[1],
                "Reopening retains the three thumbnails and the selected older icon");
            Check(reopenedWrites == 0, "Reopening history does not cause another save");
            File.Delete(imports[3]);
            buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(window.FlushPendingChanges() && ((ItemsControl)window.FindName("RecentIcons")).Items.Count == 2
                && store.Load().IconPath == imports[1], "Missing recent copy is removed without losing the current valid icon");
            window.Close();
            window = new SettingsWindow(new AppSettings
            {
                ButtonMode = ButtonVisualMode.Icon,
                RecentIconPaths = new List<string> { Path.Combine(directory, "missing.png"), imports[0] }
            }, _ => "");
            window.Show();
            await LayoutAsync(window);
            Check(((ItemsControl)window.FindName("RecentIcons")).Items.Count == 1, "Missing history files are skipped while valid copies remain selectable");
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", previous);
        }
    }
}
