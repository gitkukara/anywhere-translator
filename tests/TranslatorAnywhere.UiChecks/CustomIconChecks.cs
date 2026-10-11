using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Native;
using TranslatorAnywhere.Views;

internal static partial class Program
{
    private static async Task CheckCustomIconsAsync()
    {
        string directory = Path.Combine(output, "custom-icon-fixtures");
        Directory.CreateDirectory(directory);
        string png = Path.Combine(directory, "transparent.png");
        string svg = Path.Combine(directory, "transparent.svg");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawEllipse(Brushes.OrangeRed, null, new Point(16, 16), 12, 12);
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(png)) encoder.Save(file);
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\"><circle cx=\"16\" cy=\"16\" r=\"12\" fill=\"#ff4500\"/></svg>");
        var work = DesktopInterop.GetWorkArea(DesktopInterop.Cursor);
        var selection = new SelectionSnapshot("custom icon fixture", "UI fixture", IntPtr.Zero,
            new Rect(work.Left + 80, work.Top + 80, 120, 20), new Point(work.Left + 200, work.Top + 100), "UI fixture", DateTimeOffset.Now);
        var button = new SelectionButtonWindow();
        try
        {
            foreach (string path in new[] { png, svg })
            {
                button.ShowFor(selection, new AppSettings { ButtonMode = ButtonVisualMode.Icon, IconPath = path, ButtonColor = "#0000FF", ButtonSize = 48 });
                await LayoutAsync(button);
                var tile = (Border)button.FindName("Tile");
                var icon = (Image)button.FindName("CustomIcon");
                Check(icon.Visibility == Visibility.Visible && icon.ActualWidth == 48 && icon.ActualHeight == 48,
                    "Custom " + Path.GetExtension(path) + " fills configured dimensions without an inset");
                Check(tile.Background is SolidColorBrush { Color.A: 0 } && tile.BorderThickness == new Thickness(0) && tile.Effect is null,
                    "Custom icon has no colored tile, border or shadow");
                var rendered = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(tile);
                var pixels = new byte[48 * 48 * 4];
                rendered.CopyPixels(pixels, 48 * 4, 0);
                int center = (24 * 48 + 24) * 4;
                Check(pixels[3] == 0 && pixels[center + 3] == 255 && pixels[center + 2] == 255,
                    "Custom icon keeps transparent corners and its own color rather than the button color");
                Check(tile.InputHitTest(new Point(1, 1)) is not null, "Transparent part of the icon remains clickable");
                int clicks = 0;
                Action clicked = () => clicks++;
                button.TranslationRequested += clicked;
                tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                button.TranslationRequested -= clicked;
                Check(clicks == 1, "Custom icon click requests translation");
                Save(button, "custom-icon-" + Path.GetExtension(path)[1..] + ".png");
                button.ShowFor(selection, new AppSettings { ButtonMode = ButtonVisualMode.Icon, IconPath = path, ButtonTransparency = 50 });
                Check(Near(tile.Opacity, .5), "Custom icon still respects the opacity setting");
            }
            button.ShowFor(selection, new AppSettings { ButtonMode = ButtonVisualMode.Symbol });
            var restored = (Border)button.FindName("Tile");
            Check(restored.Background is SolidColorBrush { Color.A: 255 } && restored.BorderThickness == new Thickness(1)
                && restored.Effect is not null && ((Image)button.FindName("CustomIcon")).Visibility == Visibility.Collapsed,
                "Switching back to symbols restores the original tile");
            button.ShowFor(selection, new AppSettings { ButtonMode = ButtonVisualMode.Icon, IconPath = Path.Combine(directory, "missing.png") });
            Check(((TextBlock)button.FindName("Glyph")).Visibility == Visibility.Visible && restored.Effect is not null,
                "Unreadable icon falls back to a visible translation button");
        }
        finally { button.Close(); }

        var settings = new AppSettings { ButtonMode = ButtonVisualMode.Icon, IconPath = svg };
        var window = new SettingsWindow(settings, "");
        try
        {
            window.Show();
            await LayoutAsync(window);
            Check(((FrameworkElement)window.FindName("ColorModeRow")).Visibility == Visibility.Collapsed,
                "Icon settings hide the inapplicable button background color");
            Check(((TextBlock)window.FindName("StatusLabel")).Visibility == Visibility.Collapsed,
                "Settings can reopen an SVG icon without an error");
            AppSettings? saved = null;
            window.SettingsSaved += (value, _) => saved = value;
            ((TextBox)window.FindName("ColorBox")).Text = "invalid hidden color";
            ((TextBox)window.FindName("OffsetXBox")).Text = "9";
            Check(window.FlushPendingChanges() && saved is { OffsetX: 9, ButtonColor: "#3390EC" },
                "Hidden background color does not block saving icon settings");
            ((TextBox)window.FindName("ColorBox")).Text = "#3390EC";
            ((ListBox)window.FindName("ModeBox")).SelectedIndex = (int)ButtonVisualMode.Text;
            Check(((FrameworkElement)window.FindName("ColorModeRow")).Visibility == Visibility.Visible,
                "Text settings restore the background color controls");
        }
        finally { window.Close(); }
    }
}
