using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Native;
using TranslatorAnywhere.Services;

namespace TranslatorAnywhere.Views;

public partial class SelectionButtonWindow : Window
{
    public event Action? TranslationRequested;
    public SelectionSnapshot? Selection { get; private set; }
    private double _baseOpacity = 1;
    private readonly System.Windows.Media.Effects.Effect _tileShadow;

    public SelectionButtonWindow()
    {
        InitializeComponent();
        _tileShadow = Tile.Effect;
        SourceInitialized += (_, _) => DesktopInterop.MakeNonActivating(this);
        Tile.MouseEnter += (_, _) => Tile.Opacity = _baseOpacity * .9;
        Tile.MouseLeave += (_, _) => Tile.Opacity = _baseOpacity;
    }

    public void ShowFor(SelectionSnapshot selection, AppSettings settings)
    {
        Selection = selection;
        double transparency = double.IsFinite(settings.ButtonTransparency) ? Math.Clamp(settings.ButtonTransparency, 0, 100) : 0;
        _baseOpacity = 1 - transparency / 100;
        Tile.Opacity = _baseOpacity;
        Tile.Width = Tile.Height = Math.Clamp(settings.ButtonSize, 24, 72);
        Symbol.Width = Symbol.Height = Tile.Width - 10;
        Tile.CornerRadius = new CornerRadius(Math.Min(10, settings.ButtonSize * .22));
        Tile.BorderThickness = new Thickness(1);
        Tile.Effect = _tileShadow;
        Glyph.Text = string.IsNullOrWhiteSpace(settings.ButtonText) ? "翻" : settings.ButtonText;
        Glyph.FontSize = Math.Clamp(settings.ButtonSize * (Glyph.Text.Length > 2 ? .26 : .54), 10, 32);
        Glyph.MaxWidth = settings.ButtonSize - 6;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(settings.ButtonColor);
            Tile.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        }
        catch { Tile.Background = new SolidColorBrush(Color.FromRgb(51, 144, 236)); }
        Glyph.Visibility = settings.ButtonMode == ButtonVisualMode.Text ? Visibility.Visible : Visibility.Collapsed;
        Symbol.Visibility = settings.ButtonMode == ButtonVisualMode.Symbol ? Visibility.Visible : Visibility.Collapsed;
        CustomIcon.Visibility = Visibility.Collapsed;
        CustomIcon.Source = null;
        if (settings.ButtonMode == ButtonVisualMode.Icon)
        {
            try
            {
                CustomIcon.Source = IconImageService.Load(settings.IconPath);
                CustomIcon.Visibility = Visibility.Visible;
                // A transparent hit surface keeps the whole icon clickable without adding visible chrome.
                Tile.Background = Brushes.Transparent;
                Tile.BorderThickness = new Thickness(0);
                Tile.CornerRadius = new CornerRadius(0);
                Tile.Effect = null;
            }
            catch { Glyph.Text = "翻"; Glyph.Visibility = Visibility.Visible; }
        }
        var scale = DesktopInterop.GetDpiScale(selection.Pointer);
        var position = ButtonPlacement.Calculate(selection, settings, scale, DesktopInterop.GetWorkArea(selection.Pointer));
        // Position the hidden HWND before displaying so there is no flash on another monitor.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        DesktopInterop.PlaceWindow(this, position);
        Show();
        UpdateLayout();
        DesktopInterop.PlaceWindow(this, position);
    }

    private void OnTranslate(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TranslationRequested?.Invoke();
    }
}
