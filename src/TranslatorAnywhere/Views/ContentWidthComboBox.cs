using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TranslatorAnywhere.Views;

/// <summary>Reserve space for the longest choice, including the arrow and padding.</summary>
public sealed class ContentWidthComboBox : ComboBox
{
    protected override Size MeasureOverride(Size constraint)
    {
        double longest = 0;
        foreach (var item in Items)
        {
            string text = (item is ComboBoxItem choice ? choice.Content : item)?.ToString() ?? "";
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            longest = Math.Max(longest, formatted.WidthIncludingTrailingWhitespace);
        }
        // Template uses 24 for the arrow and 8 + 4 text padding; allow 4 for rounding.
        double width = Math.Min(Math.Min(MaxWidth, constraint.Width), Math.Max(MinWidth, Math.Ceiling(longest) + 40));
        var measured = base.MeasureOverride(new Size(width, constraint.Height));
        return new Size(width, measured.Height);
    }
}
