using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TranslatorAnywhere.Services;
using TranslatorAnywhere.Views;

internal static partial class Program
{
    private static async Task CheckPackageAsync()
    {
#pragma warning disable IL3000 // An empty assembly location is the condition being checked.
        Check(typeof(TranslationResultView).Assembly.Location.Length == 0, "Application assembly is inside the single EXE");
#pragma warning restore IL3000
        Check(!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Assets")), "Renderer has no loose asset directory");
        using (var process = Process.GetCurrentProcess())
        {
            string runtime = process.Modules.Cast<ProcessModule>().Single(module => module.ModuleName == "coreclr.dll").FileName;
            Check(runtime.Contains("\\shared\\Microsoft.NETCore.App\\", StringComparison.OrdinalIgnoreCase), "Runtime is loaded from the installed .NET framework");
        }
        string[] licenses = typeof(TranslationResultView).Assembly.GetManifestResourceNames();
        Check(licenses.Any(name => name.EndsWith("SharpVectors-LICENSE.txt", StringComparison.Ordinal)), "SVG license is embedded");
        Check(licenses.Any(name => name.EndsWith("THIRD-PARTY-NOTICES.md", StringComparison.Ordinal)), "Third-party notices are embedded");
        var renderer = new TranslationResultView { MaxHeight = 420 };
        var window = new Window { Content = renderer, Width = 440, Height = 460, Background = Brushes.White };
        const string markdown = "# Paper translation\n\nInline $E=mc^2$.\n\n$$\\begin{bmatrix}1&2\\\\3&4\\end{bmatrix}$$\n\n$$\\begin{aligned}a&=b+c\\\\d&=e+f\\end{aligned}$$";
        renderer.AppendText(markdown);
        window.Show();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while ((!renderer.IsRendererReady || renderer.RenderedRevision != renderer.Revision) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Check(renderer.IsRendererReady && renderer.RenderedRevision == renderer.Revision, "Embedded renderer starts and displays the translation");
            var browser = (WebView2CompositionControl)typeof(TranslationResultView).GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
            while (await browser.CoreWebView2.ExecuteScriptAsync("document.fonts.status === 'loaded'") != "true" && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            string result = await browser.CoreWebView2.ExecuteScriptAsync("({heading:document.querySelector('h1')?.textContent,math:document.querySelectorAll('.katex').length,errors:document.querySelectorAll('.katex-error').length,font:document.fonts.check('16px KaTeX_Main'),loadedFonts:[...document.fonts].filter(f=>f.status==='loaded').length})");
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            Check(root.GetProperty("heading").GetString() == "Paper translation", "Markdown heading renders");
            Check(root.GetProperty("math").GetInt32() == 3 && root.GetProperty("errors").GetInt32() == 0, "Inline, matrix and aligned formulas render without errors");
            Check(root.GetProperty("font").GetBoolean() && root.GetProperty("loadedFonts").GetInt32() > 0, "Bundled mathematical fonts load");
            Check(renderer.Text == markdown, "Original Markdown and LaTeX remain available for copying");
            using (var screenshot = File.Create(Path.Combine(output, "single-file-renderer.png")))
                await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
            string svgPath = Path.Combine(output, "package-icon.svg");
            File.WriteAllText(svgPath, "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><circle cx='12' cy='12' r='10' fill='#3390ec'/></svg>");
            Check(IconImageService.Load(svgPath) is DrawingImage, "Bundled SVG dependencies load");
        }
        finally { renderer.Dispose(); window.Close(); }
    }
}
