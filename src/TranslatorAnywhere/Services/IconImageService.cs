using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using SharpVectors.Converters;
using SharpVectors.Dom;
using SharpVectors.Renderers.Wpf;

namespace TranslatorAnywhere.Services;

public static class IconImageService
{
    public static byte[] ReadFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".svg" or ".jpg" or ".jpeg" or ".ico" or ".bmp" or ".gif"))
            throw new ArgumentException("图标支持透明 PNG、SVG，以及 JPG、ICO、BMP 和 GIF 格式。");
        using var stream = File.OpenRead(path);
        if (stream.Length == 0 || stream.Length > 5 * 1024 * 1024)
            throw new ArgumentException("图标文件必须大于 0 字节且不超过 5 MB。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static ImageSource Load(string path) => Decode(ReadFile(path), Path.GetExtension(path));

    public static ImageSource Decode(byte[] bytes, string extension)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)) return ReadSvg(stream);
            // Inspect every frame before decoding to bound allocation, and retain the original alpha channel.
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (decoder.Frames.Count == 0 || decoder.Frames.Count > 64)
                throw new InvalidDataException("Unsupported frame count.");
            long totalPixels = 0;
            foreach (var frame in decoder.Frames)
            {
                totalPixels += (long)frame.PixelWidth * frame.PixelHeight;
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 2048 || frame.PixelHeight > 2048
                    || totalPixels > 16 * 1024 * 1024)
                    throw new InvalidDataException("Icon dimensions exceed the limit.");
                int stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
                frame.CopyPixels(new byte[checked(stride * frame.PixelHeight)], stride, 0);
            }
            stream.Position = 0;
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ArgumentException("无法读取图标。请使用有效图片，尺寸不超过 2048 × 2048；SVG 应为不依赖外部文件的静态图标。", ex);
        }
    }

    private static ImageSource ReadSvg(Stream stream)
    {
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 5 * 1024 * 1024
        });
        var document = XDocument.Load(xml);
        if (document.Root?.Name != XName.Get("svg", "http://www.w3.org/2000/svg")
            || document.Descendants().Take(10001).Count() > 10000
            || document.DescendantNodes().OfType<XProcessingInstruction>().Any())
            throw new InvalidDataException("Invalid SVG document.");
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName is "script" or "foreignObject")
                throw new InvalidDataException("Only static SVG icons are supported.");
            foreach (var attribute in element.Attributes())
                if ((attribute.Name.LocalName == "href" && !attribute.Value.TrimStart().StartsWith('#'))
                    || attribute.Name == XNamespace.Xml + "base")
                    throw new InvalidDataException("External SVG resources are not supported.");
        }
        using var reader = new FileSvgReader(new WpfDrawingSettings
        {
            IncludeRuntime = false,
            EnsureViewboxSize = true,
            ExternalResourcesAccessMode = ExternalResourcesAccessModes.ThrowError
        });
        using var validatedXml = document.CreateReader();
        var drawing = reader.Read(validatedXml) ?? throw new InvalidDataException("Empty SVG drawing.");
        var bounds = drawing.Bounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 2048 || bounds.Height > 2048)
            throw new InvalidDataException("SVG dimensions exceed the limit.");
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
