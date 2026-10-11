using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                string source = Path.Combine(directory, index == 2 ? "source.png" : "source.svg");
                int width = new[] { 16, 2048, 128, 1024 }[index];
                int height = new[] { 16, 128, 2048, 1024 }[index];
                if (index == 2)
                {
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                        drawing.DrawRectangle(Brushes.OrangeRed, null, new Rect(width * .125, height * .125, width * .75, height * .75));
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(source);
                    encoder.Save(file);
                }
                else
                    File.WriteAllText(source, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\"><rect width=\"{width}\" height=\"{height}\" fill=\"{colors[index]}\"/></svg>");
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
            var thumbnails = buttons.Select(button => FindVisual<Image>(button)!).ToArray();
            var viewports = buttons.Select(button => FindVisual<Grid>(button)!).ToArray();
            Check(thumbnails.Any(image => image.Source is DrawingImage && Near(image.Source.Width, 2048))
                && thumbnails.Any(image => image.Source is BitmapSource { PixelHeight: 2048 }),
                "Thumbnail fixtures include wide SVG, tall PNG and large square original images");
            Check(buttons.All(button => Near(button.ActualWidth, 32) && Near(button.ActualHeight, 32))
                && viewports.All(viewport => Near(viewport.ActualWidth, 24) && Near(viewport.ActualHeight, 24)),
                "All thumbnail viewports and click targets stay fixed despite different source sizes");
            var tallInk = ThumbnailInkSize(viewports[1]);
            var wideInk = ThumbnailInkSize(viewports[2]);
            Check(tallInk.Width <= 4 && tallInk.Height >= 15 && wideInk.Height <= 4 && wideInk.Width >= 22,
                "Thumbnail scaling preserves aspect ratio within the clipped viewport");
            var sizeChoice = (ListBox)window.FindName("SizeChoice");
            sizeChoice.SelectedIndex = 3;
            await LayoutAsync(window);
            Check(viewports.All(viewport => Near(viewport.ActualWidth, 24) && Near(viewport.ActualHeight, 24)),
                "Changing the desktop button size does not enlarge recent thumbnails");
            sizeChoice.SelectedIndex = 1;
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

    private static Size ThumbnailInkSize(Visual viewport)
    {
        var rendered = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(viewport);
        var pixels = new byte[24 * 24 * 4];
        rendered.CopyPixels(pixels, 24 * 4, 0);
        int left = 24, top = 24, right = -1, bottom = -1;
        for (int y = 0; y < 24; y++)
            for (int x = 0; x < 24; x++)
                if (pixels[(y * 24 + x) * 4 + 3] > 16)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        return right >= left ? new Size(right - left + 1, bottom - top + 1) : new Size();
    }
}
