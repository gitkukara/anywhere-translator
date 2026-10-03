using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TranslatorAnywhere.Views;

public enum HugeIconKind
{
    Translate, Pin, Settings, Close, Refresh, Stop, Copy, Check,
    ChevronDown, ChevronLeft, ChevronRight, Add
}

/// <summary>Hugeicons Stroke Rounded, rendered on its original 24-unit grid.</summary>
public sealed class HugeIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(HugeIconKind), typeof(HugeIcon),
        new FrameworkPropertyMetadata(HugeIconKind.Translate, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(HugeIcon), new FrameworkPropertyMetadata(Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    static HugeIcon()
    {
        WidthProperty.OverrideMetadata(typeof(HugeIcon), new FrameworkPropertyMetadata(18d));
        HeightProperty.OverrideMetadata(typeof(HugeIcon), new FrameworkPropertyMetadata(18d));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(HugeIcon), new UIPropertyMetadata(false));
    }

    public HugeIconKind Kind
    {
        get => (HugeIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double scale = Math.Min(RenderSize.Width, RenderSize.Height) / 24;
        if (scale <= 0 || !HugeIconData.Geometries.TryGetValue(Kind, out Geometry? geometry)) return;

        var pen = new Pen(Foreground, 1.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        drawingContext.PushTransform(new TranslateTransform(
            (RenderSize.Width - 24 * scale) / 2, (RenderSize.Height - 24 * scale) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
