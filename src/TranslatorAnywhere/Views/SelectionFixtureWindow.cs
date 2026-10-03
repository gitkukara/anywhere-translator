using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TranslatorAnywhere.Services;

namespace TranslatorAnywhere.Views;

public sealed class SelectionFixtureWindow : Window
{
    public SelectionFixtureWindow()
    {
        Title = "划词测试文本 - Anywhere Translator";
        Width = 780; Height = 480; MinWidth = 500; MinHeight = 320;
        ThemeService.TrackWindow(this);
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var panel = new DockPanel { Margin = new Thickness(28) };
        var heading = new TextBlock { Text = "拖选下面的英文，试试划词翻译", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var hint = new TextBlock { Text = "选中文字后点击小按钮；也可以按 Ctrl+Alt+T。此窗口可以单独关闭。", Margin = new Thickness(0, 0, 0, 22) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var document = new FlowDocument { PagePadding = new Thickness(18), FontFamily = new FontFamily("Segoe UI"), FontSize = 20, LineHeight = 34 };
        document.Blocks.Add(new Paragraph(new Run("Translation brings ideas closer.")));
        document.Blocks.Add(new Paragraph(new Run("Select any sentence with your mouse. A small translation button will appear next to the selection. Click it to open the translation window.")));
        document.Blocks.Add(new Paragraph(new Run("The quick brown fox jumps over the lazy dog. Keep your original clipboard content while reading and translating across applications.")));
        var editor = new RichTextBox { Document = document, IsReadOnly = true, BorderThickness = new Thickness(1), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        editor.SetResourceReference(Control.BackgroundProperty, "FieldBrush");
        editor.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        editor.SetResourceReference(Control.BorderBrushProperty, "DividerBrush");
        document.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
        System.Windows.Automation.AutomationProperties.SetName(editor, "划词测试英文");
        panel.Children.Add(editor);
        Content = panel;
    }
}
