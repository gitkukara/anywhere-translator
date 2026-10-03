using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TranslatorAnywhere.Services;

internal static class StartMenuRegistration
{
    public static void EnsureCurrentExecutable()
    {
        // Development and fixture runs must not replace the installed shortcut.
        string? executable = Environment.ProcessPath;
        if (!string.Equals(Path.GetFileName(executable), "Anywhere Translator.exe", StringComparison.OrdinalIgnoreCase)) return;
        object? shell = null;
        object? shortcut = null;
        try
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Start menu folder unavailable.");
            Directory.CreateDirectory(folder);
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(Path.Combine(folder, "Anywhere Translator.lnk"));
            dynamic link = shortcut;
            string directory = Path.GetDirectoryName(executable)!;
            string icon = executable + ",0";
            if (!string.Equals((string)link.TargetPath, executable, StringComparison.OrdinalIgnoreCase)
                || (string)link.Arguments != ""
                || !string.Equals((string)link.WorkingDirectory, directory, StringComparison.OrdinalIgnoreCase)
                || !string.Equals((string)link.IconLocation, icon, StringComparison.OrdinalIgnoreCase)
                || (string)link.Description != "Anywhere Translator")
            {
                link.TargetPath = executable;
                link.Arguments = "";
                link.WorkingDirectory = directory;
                link.IconLocation = icon;
                link.Description = "Anywhere Translator";
                link.Save();
            }
        }
        catch (Exception error)
        {
            // A shortcut failure must not prevent translation or tray startup.
            DiagnosticLog.Write("Start menu shortcut registration failed", error);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
