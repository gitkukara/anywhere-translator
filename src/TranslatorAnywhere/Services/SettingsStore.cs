using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TranslatorAnywhere.Models;

namespace TranslatorAnywhere.Services;

public sealed class SettingsStore
{
    private const int MaximumKeyBytes = 16 * 1024;
    private const int MaximumIconBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDirectory { get; }
    private readonly object saveLock = new();
    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private string KeyPath => Path.Combine(DataDirectory, "api-key.dpapi");

    public SettingsStore()
    {
        DataDirectory = ResolveDataDirectory();
        Directory.CreateDirectory(DataDirectory);
    }

    internal static string ResolveDataDirectory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : overridePath);
    }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return CreateDefaultSettings();
        try
        {
            if (new FileInfo(SettingsPath).Length > 1024 * 1024)
                throw new InvalidDataException("Settings file is too large.");
            string json = File.ReadAllText(SettingsPath);
            using var document = JsonDocument.Parse(json);
            bool isLegacy = document.RootElement.ValueKind == JsonValueKind.Object
                && !document.RootElement.TryGetProperty(nameof(AppSettings.Providers), out _);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? throw new JsonException("Settings are empty.");
            bool migrateColorAlpha = document.RootElement.ValueKind == JsonValueKind.Object
                && !document.RootElement.TryGetProperty(nameof(AppSettings.ButtonTransparency), out _);
            Normalize(settings, migrateColorAlpha);
            if (isLegacy)
            {
                ProviderRegistry.InitializeLegacy(settings);
                MigrateLegacy(settings);
            }
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Preserve the damaged file so the user can recover it after settings are reset.
            File.Copy(SettingsPath, SettingsPath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), false);
            DiagnosticLog.Write("Invalid settings file; defaults were loaded.", ex);
            return CreateDefaultSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (saveLock)
        {
            Normalize(settings);
            AtomicWrite(SettingsPath, SerializeSettings(settings));
        }
    }

    private static AppSettings CreateDefaultSettings()
    {
        var settings = new AppSettings();
        var profile = ProviderRegistry.CreateProvider("deepseek");
        settings.Providers.Add(profile);
        settings.ActiveProviderId = profile.Id;
        return settings;
    }

    private void MigrateLegacy(AppSettings settings)
    {
        lock (saveLock)
        {
            // Keep both original files recoverable. Persisting the new schema also prevents a removed key
            // from being copied from the original file again on the next normal launch.
            string backup = SettingsPath + ".legacy-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N");
            File.Copy(SettingsPath, backup, overwrite: false);
            string destination = ProviderKeyPath(ProviderRegistry.LegacyProviderId);
            if (File.Exists(KeyPath) && !File.Exists(destination))
            {
                if (new FileInfo(KeyPath).Length > MaximumKeyBytes + 4096)
                    throw new InvalidDataException("保存的 API Key 文件过大。");
                AtomicWrite(destination, File.ReadAllBytes(KeyPath));
            }
            AtomicWrite(SettingsPath, SerializeSettings(settings));
        }
    }

    private static byte[] SerializeSettings(AppSettings settings)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, JsonOptions));
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("服务配置过大，请减少模型列表后重试。");
        return bytes;
    }

    private static AppSettings Normalize(AppSettings settings, bool migrateColorAlpha = false)
    {
        var defaults = new AppSettings();
        if (!Enum.IsDefined(settings.ButtonMode)) settings.ButtonMode = defaults.ButtonMode;
        if (!Enum.IsDefined(settings.Anchor)) settings.Anchor = defaults.Anchor;
        settings.ButtonSize = double.IsFinite(settings.ButtonSize) ? Math.Clamp(settings.ButtonSize, 24, 72) : defaults.ButtonSize;
        settings.ButtonTransparency = double.IsFinite(settings.ButtonTransparency) ? Math.Clamp(settings.ButtonTransparency, 0, 100) : 0;
        settings.OffsetX = double.IsFinite(settings.OffsetX) ? Math.Clamp(settings.OffsetX, -500, 500) : defaults.OffsetX;
        settings.OffsetY = double.IsFinite(settings.OffsetY) ? Math.Clamp(settings.OffsetY, -500, 500) : defaults.OffsetY;
        settings.SelectionDelayMs = Math.Clamp(settings.SelectionDelayMs, 50, 1500);
        settings.AutoHideSeconds = Math.Clamp(settings.AutoHideSeconds, 2, 120);
        settings.ButtonText = CleanString(settings.ButtonText, 32);
        if (settings.ButtonText.Length == 0) settings.ButtonText = defaults.ButtonText;
        settings.IconPath = CleanString(settings.IconPath, 32768);
        settings.ButtonColor = CleanString(settings.ButtonColor, 32);
        try
        {
            if (settings.ButtonColor.Length > 0 && ColorConverter.ConvertFromString(settings.ButtonColor) is Color color)
            {
                if (migrateColorAlpha) settings.ButtonTransparency = (1 - color.A / 255d) * 100;
                settings.ButtonColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            else
                settings.ButtonColor = defaults.ButtonColor;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            settings.ButtonColor = defaults.ButtonColor;
        }
        settings.ProviderName = CleanString(settings.ProviderName, 80);
        // An explicitly damaged custom endpoint remains empty so we never silently send its key to another provider.
        settings.BaseUrl = CleanString(settings.BaseUrl, 2048);
        settings.Model = CleanString(settings.Model, 200);
        string language = CleanString(settings.TargetLanguage, 100);
        settings.TargetLanguage = language is "简体中文" or "繁体中文" or "English" or "日本語" or "한국어" ? language : defaults.TargetLanguage;
        settings.ClipboardFallbackApplications = CleanApplications(settings.ClipboardFallbackApplications);
        settings.ExcludedApplications = CleanApplications(settings.ExcludedApplications);
        ProviderRegistry.Normalize(settings);
        return settings;
    }

    private static string CleanString(string? value, int maximumLength)
    {
        value = (value ?? "").Trim();
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static List<string> CleanApplications(List<string>? applications) => (applications ?? new List<string>())
        .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 260 && !value.Contains('\r') && !value.Contains('\n'))
        .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();

    public string ReadApiKey() { lock (saveLock) return ReadKeyFile(KeyPath); }

    public string ReadProviderApiKey(Guid providerId) { lock (saveLock) return ReadKeyFile(ProviderKeyPath(providerId)); }

    public void SaveApiKey(string key) { lock (saveLock) SaveKeyFile(KeyPath, key); }

    public void SaveProviderApiKey(Guid providerId, string key) { lock (saveLock) SaveKeyFile(ProviderKeyPath(providerId), key); }

    private string ProviderKeyPath(Guid providerId)
    {
        if (providerId == Guid.Empty) throw new ArgumentException("服务标识不能为空。", nameof(providerId));
        return Path.Combine(DataDirectory, "api-key." + providerId.ToString("N") + ".dpapi");
    }

    private static string ReadKeyFile(string path)
    {
        if (!File.Exists(path)) return "";
        long fileLength = new FileInfo(path).Length;
        if (fileLength == 0 || fileLength > MaximumKeyBytes + 4096)
            throw new InvalidDataException("保存的 API Key 无效，请重新填写。");
        byte[] encrypted = File.ReadAllBytes(path);
        if (encrypted.Length == 0 || encrypted.Length > MaximumKeyBytes + 4096)
            throw new InvalidDataException("保存的 API Key 无效，请重新填写。");
        byte[]? clear = null;
        try
        {
            clear = Dpapi.Transform(encrypted, protect: false);
            if (clear.Length > MaximumKeyBytes)
                throw new InvalidDataException("保存的 API Key 无效，请重新填写。");
            return Encoding.UTF8.GetString(clear);
        }
        catch (Win32Exception ex)
        {
            DiagnosticLog.Write("Could not decrypt the stored API credential.", ex);
            throw new InvalidOperationException("无法读取 API Key。密钥绑定到保存时的 Windows 账户，请重新填写。", ex);
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static void SaveKeyFile(string path, string key)
    {
        byte[]? encrypted = EncryptKey(key);
        try
        {
            if (encrypted is null) File.Delete(path);
            else AtomicWrite(path, encrypted);
        }
        finally { if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted); }
    }

    private static byte[]? EncryptKey(string key)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) return null;
        if (key.Contains('\r') || key.Contains('\n'))
            throw new ArgumentException("API Key 不能包含换行。", nameof(key));
        byte[] clear = Encoding.UTF8.GetBytes(key);
        if (clear.Length > MaximumKeyBytes)
        {
            CryptographicOperations.ZeroMemory(clear);
            throw new ArgumentException("API Key 过长。", nameof(key));
        }
        try
        {
            return Dpapi.Transform(clear, protect: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void SaveConfiguration(AppSettings settings, IReadOnlyDictionary<Guid, string> pendingKeys)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pendingKeys);
        lock (saveLock)
        {
            Normalize(settings);
            var providerIds = settings.Providers.Select(profile => profile.Id).ToHashSet();
            if (pendingKeys.Keys.Any(id => id == Guid.Empty || !providerIds.Contains(id)))
                throw new ArgumentException("待保存的密钥不属于当前服务配置。", nameof(pendingKeys));

            var staged = new Dictionary<string, byte[]?>();
            var previous = new Dictionary<string, byte[]?>();
            var changed = new List<string>();
            HashSet<Guid> oldProviderIds = ReadPersistedProviderIds();
            try
            {
                // Encrypt and validate every key before touching any existing configuration.
                foreach (var item in pendingKeys) staged.Add(ProviderKeyPath(item.Key), EncryptKey(item.Value));
                staged.Add(SettingsPath, SerializeSettings(settings));
                foreach (string path in staged.Keys)
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                        throw new InvalidDataException("原配置文件过大，无法安全保存。");
                    previous.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
                }
                foreach (var item in staged.Where(item => item.Key != SettingsPath))
                {
                    if (item.Value is null) File.Delete(item.Key);
                    else AtomicWrite(item.Key, item.Value);
                    changed.Add(item.Key);
                }
                // Commit settings last so a failed save does not leave an old endpoint with a new key.
                AtomicWrite(SettingsPath, staged[SettingsPath]!);
                changed.Add(SettingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException)
            {
                foreach (string path in changed.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (previous[path] is byte[] bytes) AtomicWrite(path, bytes);
                        else File.Delete(path);
                    }
                    catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                    {
                        DiagnosticLog.Write("Could not roll back a configuration file after a failed save.", rollbackError);
                    }
                }
                DiagnosticLog.Write("Could not save provider configuration.", ex);
                throw new InvalidOperationException("无法保存服务配置。请检查数据目录权限和可用空间后重试。", ex);
            }
            finally
            {
                foreach (byte[]? bytes in staged.Values) if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                foreach (byte[]? bytes in previous.Values) if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            }

            // Remove credentials only after the configuration commit; never delete the legacy original.
            foreach (Guid removedId in oldProviderIds.Except(providerIds))
            {
                try { File.Delete(ProviderKeyPath(removedId)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { DiagnosticLog.Write("Could not remove a retired provider credential.", ex); }
            }
        }
    }

    private HashSet<Guid> ReadPersistedProviderIds()
    {
        if (!File.Exists(SettingsPath)) return new HashSet<Guid>();
        try
        {
            if (new FileInfo(SettingsPath).Length > 1024 * 1024) return new HashSet<Guid>();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            return (settings?.Providers ?? new List<ProviderConfiguration>()).Where(profile => profile is not null && profile.Id != Guid.Empty)
                .Select(profile => profile.Id).ToHashSet();
        }
        catch (JsonException) { return new HashSet<Guid>(); }
    }

    public string ImportIcon(string sourcePath)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".ico" or ".bmp" or ".gif"))
            throw new ArgumentException("图标支持 PNG、JPG、ICO、BMP 和 GIF 格式。");
        byte[] bytes;
        using (var stream = File.OpenRead(sourcePath))
        {
            if (stream.Length == 0 || stream.Length > MaximumIconBytes)
                throw new ArgumentException("图标文件必须大于 0 字节且不超过 5 MB。");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        try
        {
            using var imageStream = new MemoryStream(bytes, writable: false);
            // Inspect dimensions before decoding pixels, so a tiny compressed image cannot allocate an oversized bitmap.
            var decoder = BitmapDecoder.Create(imageStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
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
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or ArgumentException or FormatException
            or IOException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            throw new ArgumentException("无法读取图标。请使用有效图片，尺寸不超过 2048 × 2048，帧数不超过 64。", ex);
        }
        string iconsDirectory = Path.Combine(DataDirectory, "icons");
        Directory.CreateDirectory(iconsDirectory);
        string destination = Path.Combine(iconsDirectory, Guid.NewGuid().ToString("N") + extension);
        AtomicWrite(destination, bytes);
        return destination;
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int Length; public IntPtr Data; }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Transform(byte[] data, bool protect)
        {
            var input = new DataBlob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
            var output = new DataBlob();
            IntPtr description = IntPtr.Zero;
            try
            {
                Marshal.Copy(data, 0, input.Data, data.Length);
                const uint NoUserInterface = 1; // Omitting LOCAL_MACHINE binds the key to this Windows account.
                bool ok = protect
                    ? CryptProtectData(ref input, "TranslatorAnywhere API credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoUserInterface, out output)
                    : CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoUserInterface, out output);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length);
                Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                    LocalFree(output.Data);
                }
                if (description != IntPtr.Zero) LocalFree(description);
            }
        }
    }
}
