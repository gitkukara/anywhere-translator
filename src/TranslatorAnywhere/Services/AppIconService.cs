using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;

namespace TranslatorAnywhere.Services;

public static class AppIconService
{
    private const int MaximumIconBytes = 5 * 1024 * 1024;
    private const int MaximumIconFrames = 32;
    public const string OverrideFileName = "app.ico";

    /// <summary>Loads a frozen image. An invalid portable override falls back to the embedded application icon.</summary>
    public static ImageSource LoadWindowIcon(string dataDirectory)
    {
        byte[] bytes = LoadBytes(dataDirectory);
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        image.Freeze();
        return image;
    }

    /// <summary>The caller owns and must dispose the returned icon. No user file stays locked.</summary>
    public static DrawingIcon LoadTrayIcon(string dataDirectory)
    {
        byte[] bytes = LoadBytes(dataDirectory);
        using var stream = new MemoryStream(bytes, writable: false);
        using var icon = new DrawingIcon(stream);
        return (DrawingIcon)icon.Clone();
    }

    private static byte[] LoadBytes(string dataDirectory)
    {
        string overridePath = Path.Combine(dataDirectory, OverrideFileName);
        if (File.Exists(overridePath))
        {
            try
            {
                using var stream = File.Open(overridePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length == 0 || stream.Length > MaximumIconBytes) throw new InvalidDataException();
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                ValidateIcon(bytes);
                return bytes;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                or FormatException or NotSupportedException or OverflowException or System.Runtime.InteropServices.COMException)
            {
                DiagnosticLog.Write("Invalid or unreadable application icon override; using the embedded icon.", ex);
            }
        }
        // Qualify the component assembly so this also works when a UI test host references the application.
        string assemblyName = typeof(AppIconService).Assembly.GetName().Name!;
        var resource = Application.GetResourceStream(new Uri("/" + assemblyName + ";component/Assets/app.ico", UriKind.Relative))
            ?? throw new InvalidOperationException("缺少内置应用图标，请重新构建或重新解压程序。");
        using var resourceStream = resource.Stream;
        using var copy = new MemoryStream();
        resourceStream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void ValidateIcon(byte[] bytes)
    {
        using var headerStream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(headerStream);
        if (bytes.Length < 6 || reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
            throw new InvalidDataException();
        int frames = reader.ReadUInt16();
        if (frames == 0 || frames > MaximumIconFrames || bytes.Length < 6 + frames * 16)
            throw new InvalidDataException();
        for (int index = 0; index < frames; index++)
        {
            reader.ReadBytes(8);
            uint size = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            if (size == 0 || offset < 6 + frames * 16 || (ulong)offset + size > (ulong)bytes.Length)
                throw new InvalidDataException();
        }
        using var imageStream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(imageStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count != frames) throw new InvalidDataException();
        foreach (var frame in decoder.Frames)
        {
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 256 || frame.PixelHeight > 256)
                throw new InvalidDataException();
            int stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
            frame.CopyPixels(new byte[checked(stride * frame.PixelHeight)], stride, 0);
        }
        // Validate both consumers before accepting the override, so a tray-only format error also falls back safely.
        using var trayStream = new MemoryStream(bytes, writable: false);
        using var tray = new DrawingIcon(trayStream);
        using var bitmap = tray.ToBitmap();
    }
}
