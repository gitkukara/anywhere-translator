using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TranslatorAnywhere.Services;

public static class DiagnosticLog
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly object Gate = new();

    /// <summary>Use fixed diagnostic messages only. Exception messages, request bodies and credentials are never logged.</summary>
    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            string safeMessage = Regex.Replace(message ?? "", @"https?://\S+", "[endpoint]", RegexOptions.IgnoreCase);
            safeMessage = Regex.Replace(safeMessage, @"\bsk-[A-Za-z0-9_\-]{8,}", "[credential]", RegexOptions.IgnoreCase);
            safeMessage = Regex.Replace(safeMessage, @"(?:Bearer|api[_ -]?key)\s*[:=]?\s*\S+", "[credential]", RegexOptions.IgnoreCase);
            safeMessage = safeMessage.Replace('\r', ' ').Replace('\n', ' ');
            if (safeMessage.Length > 512) safeMessage = safeMessage[..512];
            string record = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + " " + safeMessage;
            if (ex is not null) record += " [" + ex.GetType().Name + "; 0x" + ex.HResult.ToString("X8") + "]";
            lock (Gate)
            {
                string directory = SettingsStore.ResolveDataDirectory();
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                {
                    if (File.Exists(path + ".2")) File.Delete(path + ".2");
                    if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2");
                    File.Move(path, path + ".1");
                }
                File.AppendAllText(path, record + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never prevent selection, settings recovery or translation.
        }
    }
}
