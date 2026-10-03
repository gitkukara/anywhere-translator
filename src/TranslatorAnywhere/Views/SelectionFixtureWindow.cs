using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TranslatorAnywhere.Views;

public sealed class SelectionFixtureWindow : Window
{
    public SelectionFixtureWindow()
    {
        Title = "划词测试文本 - Anywhere Translator";
        Width = 780; Height = 480; MinWidth = 500; MinHeight = 320;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 252));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var panel = new DockPanel { Margin = new Thickness(28) };
        var heading = new TextBlock { Text = "拖选下面的英文，试试划词翻译", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var hint = new TextBlock { Text = "选中文字后点击小按钮；也可以按 Ctrl+Alt+T。此窗口可以单独关闭。", Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 22) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var document = new FlowDocument { PagePadding = new Thickness(18), FontFamily = new FontFamily("Segoe UI"), FontSize = 20, LineHeight = 34 };
        document.Blocks.Add(new Paragraph(new Run("Translation brings ideas closer.")));
        document.Blocks.Add(new Paragraph(new Run("Select any sentence with your mouse. A small translation button will appear next to the selection. Click it to open the translation window.")));
        document.Blocks.Add(new Paragraph(new Run("The quick brown fox jumps over the lazy dog. Keep your original clipboard content while reading and translating across applications.")));
        var editor = new RichTextBox { Document = document, IsReadOnly = true, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(219, 225, 237)), BorderThickness = new Thickness(1), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(editor, "划词测试英文");
        panel.Children.Add(editor);
        Content = panel;
    }
}
