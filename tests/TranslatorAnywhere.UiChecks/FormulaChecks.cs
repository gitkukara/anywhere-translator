using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Views;

internal static partial class Program
{
    private static async Task CheckFormulaRenderingAsync(TranslationWindow window, MockHandler handler, SelectionSnapshot snapshot)
    {
        handler.Translation = """
            ## 论文译文

            **目标函数**为 \(\mathcal{L}_{i}=\frac{a_i}{b_i}\)，见文献 [12]。

            \[
            \sum_{i=1}^{n} x_i^2 = \int_0^1 f(x)\,dx \tag{3}
            \]

            \begin{align}
            a &= b+c \\
            d &= \begin{pmatrix}1 & 2 \\ 3 & 4\end{pmatrix}
            \end{align}

            | 方法 | 误差 |
            | --- | --- |
            | A | $x_i^2$ |

            `\frac{a}{b}` 应保留为代码示例。
            """;
        window.ShowNearSelection(snapshot);
        await window.TranslateSelectionAsync();
        await LayoutAsync(window);
        var result = (TranslationResultView)window.FindName("ResultTextBox");
        Check(result.IsRendererReady && result.RenderedRevision == result.Revision, "Local formula renderer initializes in a transparent WPF popup");
        var browser = FindVisual<WebView2CompositionControl>(result)!;
        Check(Math.Abs(browser.ActualHeight - result.ActualHeight) < 1, "The browser fills the computed rich-content viewport");
        var core = browser.CoreWebView2;
        using (var rendered = JsonDocument.Parse(await core.ExecuteScriptAsync("({math:document.querySelectorAll('.katex').length,errors:document.querySelectorAll('.math-error').length,table:!!document.querySelector('.table-scroll table'),heading:document.querySelector('h2')?.textContent,fonts:document.fonts.status})")))
        {
            var data = rendered.RootElement;
            Check(data.GetProperty("math").GetInt32() == 4 && data.GetProperty("errors").GetInt32() == 0,
                "Inline formulas, integrals, numbered equations and aligned matrices render together");
            Check(data.GetProperty("table").GetBoolean() && data.GetProperty("heading").GetString() == "论文译文", "Markdown structure is displayed with formulas");
        }
        Check(result.Text == handler.Translation, "Copy source retains the original Markdown and LaTeX exactly");
        Check(await core.ExecuteScriptAsync("document.fonts.check('16px KaTeX_Main')") == "true", "Bundled math fonts load without a CDN");
        await Task.Delay(150);
        using (var capture = File.Create(Path.Combine(output, "translation-paper-content.png")))
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
        Save(window, "translation-paper.png");

        result.Clear();
        result.AppendText("正文 \\(\\frac{a_i");
        result.Flush();
        await LayoutAsync(window);
        Check(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('.math-pending')?.textContent")) == "\\(\\frac{a_i",
            "An incomplete streamed formula preserves TeX instead of consuming escapes");
        result.AppendText("}{b_i}\\)。");
        result.Flush();
        await LayoutAsync(window);
        Check(await core.ExecuteScriptAsync("document.querySelectorAll('.katex').length") == "1", "A completed streamed formula becomes typeset math");

        result.Clear();
        long streamStart = result.Revision;
        bool displayedDuringStream = false;
        for (int index = 0; index < 24; index++)
        {
            result.AppendText("流式译文 ");
            await Task.Delay(20);
            displayedDuringStream |= browser.IsVisible && result.RenderedRevision > streamStart;
        }
        Check(displayedDuringStream, "Continuous deltas display progressively before the request finishes");
        result.Flush();
        await LayoutAsync(window);

        result.Clear();
        result.AppendText("\\[" + string.Join("+", System.Linq.Enumerable.Repeat("\\frac{x_i^2}{y_i+1}", 25)) + "\\]");
        result.Flush();
        await LayoutAsync(window);
        Check(await core.ExecuteScriptAsync("document.querySelector('.math-display').scrollWidth > document.querySelector('.math-display').clientWidth") == "true",
            "Long paper equations can scroll horizontally without widening the popup");
        var bounds = TranslatorAnywhere.Native.DesktopInterop.GetWindowBounds(window);
        var work = TranslatorAnywhere.Native.DesktopInterop.GetWorkArea(snapshot.Pointer);
        Check(bounds.Right <= work.Right + 2 && bounds.Bottom <= work.Bottom + 2, "Rich content stays inside the monitor work area");

        var fallback = typeof(TranslationResultView).GetMethod("UseFallback", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var fallbackView = new TranslationResultView();
        fallbackView.AppendText("\\(x^2\\)");
        fallback.Invoke(fallbackView, new object[] { new InvalidOperationException("fixture renderer unavailable") });
        fallbackView.AppendText("，后续译文");
        Check(fallbackView.Text == "\\(x^2\\)，后续译文" && !fallbackView.IsRendererReady,
            "Renderer failure preserves accumulated text and accepts later streamed text");
        fallbackView.Dispose();
    }
}
