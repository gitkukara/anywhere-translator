using System;
using System.Windows;
using TranslatorAnywhere.Models;

namespace TranslatorAnywhere.Services;

public static class ButtonPlacement
{
    public static Point Calculate(SelectionSnapshot selection, AppSettings settings, double scale, Rect workArea)
    {
        var bounds = selection.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            bounds = new Rect(selection.Pointer, new Size(1, 1));
        var size = Math.Clamp(settings.ButtonSize, 24, 72) * scale;
        var margin = 8 * scale;
        var x = bounds.Right;
        var y = bounds.Bottom;
        switch (settings.Anchor)
        {
            case ButtonAnchor.SelectionTopRight: y = bounds.Top - size; break;
            case ButtonAnchor.SelectionTopLeft: x = bounds.Left - size; y = bounds.Top - size; break;
            case ButtonAnchor.SelectionBottomLeft: x = bounds.Left - size; break;
            case ButtonAnchor.Cursor: x = selection.Pointer.X; y = selection.Pointer.Y; break;
        }
        x += settings.OffsetX * scale - margin;
        y += settings.OffsetY * scale - margin;
        return new Point(Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - size - margin * 2)),
            Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size - margin * 2)));
    }
}
