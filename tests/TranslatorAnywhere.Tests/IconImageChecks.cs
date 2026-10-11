using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TranslatorAnywhere.Services;

internal static class IconImageChecks
{
    internal static int Run(string directory)
    {
        int assertions = 0;
        var store = new SettingsStore();
        string png = Path.Combine(directory, "transparent.png");
        var original = new byte[] { 0, 0, 255, 0, 0, 255, 0, 128, 255, 0, 0, 255 };
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(3, 1, 96, 96, PixelFormats.Bgra32, null, original, 12)));
        using (var file = File.Create(png)) encoder.Save(file);
        string imported = store.ImportIcon(png);
        var image = (BitmapSource)IconImageService.Load(imported);
        var pixels = new byte[12];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 12, 0);
        Check(pixels[3] == 0 && pixels[7] == 128 && pixels[11] == 255, "PNG retains transparent, translucent and opaque pixels");
        Check(File.ReadAllBytes(imported).SequenceEqual(File.ReadAllBytes(png)), "Imported PNG is not flattened or re-encoded");
        Check(image.IsFrozen, "Raster icon can be reused without thread affinity");
        using (var file = File.Open(imported, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(file.Length > 0, "Loaded raster does not lock its file");

        string svg = Path.Combine(directory, "transparent.SVG");
        File.WriteAllText(svg, """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 20">
              <defs><linearGradient id="g"><stop stop-color="#00ff00"/><stop offset="1" stop-color="#0000ff"/></linearGradient></defs>
              <rect x="10" y="5" width="20" height="10" fill="url(#g)"/>
            </svg>
            """);
        imported = store.ImportIcon(svg);
        var vector = IconImageService.Load(imported);
        Check(Path.GetExtension(imported) == ".svg" && vector is DrawingImage && vector.IsFrozen, "SVG imports and remains a reusable vector drawing");
        Check(Math.Abs(vector.Width - 40) < .001 && Math.Abs(vector.Height - 20) < .001, "SVG preserves its viewBox and aspect ratio");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawImage(vector, new Rect(0, 0, 40, 20));
        var rendered = new RenderTargetBitmap(40, 20, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var renderedPixels = new byte[40 * 20 * 4];
        rendered.CopyPixels(renderedPixels, 40 * 4, 0);
        Check(renderedPixels[3] == 0 && renderedPixels[(10 * 40 + 20) * 4 + 3] == 255, "SVG background stays transparent while its shape renders");
        Check(renderedPixels[(10 * 40 + 12) * 4 + 1] > renderedPixels[(10 * 40 + 27) * 4 + 1], "SVG local gradient resources render correctly");
        using (var file = File.Open(imported, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(file.Length > 0, "Loaded SVG does not lock its file");

        Reject("not xml", "Malformed SVG rejected before saving");
        Reject("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"2049\" height=\"1\"><rect width=\"2049\" height=\"1\"/></svg>", "Oversized SVG rejected");
        Reject("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"file:///C:/fixture.png\"/></svg>", "SVG cannot read external files");
        Reject("<svg xmlns=\"http://www.w3.org/2000/svg\"><use href=\"https://example.invalid/icon.svg#x\"/></svg>", "SVG cannot request external resources");
        Reject("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///C:/fixture'>]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>", "SVG cannot resolve XML entities");
        return assertions;

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        void Reject(string text, string message)
        {
            File.WriteAllText(svg, text);
            int count = Directory.GetFiles(Path.Combine(store.DataDirectory, "icons")).Length;
            try { store.ImportIcon(svg); }
            catch (ArgumentException)
            {
                Check(Directory.GetFiles(Path.Combine(store.DataDirectory, "icons")).Length == count, message);
                return;
            }
            throw new InvalidOperationException(message);
        }
    }
}
