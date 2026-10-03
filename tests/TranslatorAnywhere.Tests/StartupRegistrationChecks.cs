using System;
using System.IO;
using System.Text.Json;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

// Pure state checks plus COM roundtrips in an isolated artifacts directory.
// These never inspect or change a real Windows startup folder or registry entry.
internal static class StartupRegistrationChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("Startup check failed: " + description);
            count++;
        }
        void Reject(string path)
        {
            try { StartupRegistration.BuildCommand(path); }
            catch (ArgumentException) { count++; return; }
            throw new Exception("Unsafe startup command was accepted.");
        }

        const string current = @"C:\My Tools\Anywhere Translator\TranslatorAnywhere.exe";
        const string old = @"D:\Old Folder\TranslatorAnywhere.exe";
        string command = StartupRegistration.BuildCommand(current);
        Check(command == "\"" + current + "\" --background", "Space-containing EXE is quoted and background switch is outside quotes");
        Check(command.EndsWith(" --background", StringComparison.Ordinal), "Startup uses the silent background argument");
        Check(!new AppSettings().LaunchAtStartup, "Startup is opt-in by default");
        Check(!JsonSerializer.Deserialize<AppSettings>("{}")!.LaunchAtStartup, "Old settings remain opted out");
        Check(JsonSerializer.Deserialize<AppSettings>("{\"LaunchAtStartup\":true}")!.LaunchAtStartup, "Explicit preference roundtrip");
        Check(!StartupRegistration.EvaluateCommand(null, current).HasRegistration, "Missing entry is disabled");
        Check(StartupRegistration.EvaluateCommand(command, current).IsEnabledForCurrentExecutable, "Canonical current registration recognized");
        Check(StartupRegistration.EvaluateCommand(command.ToUpperInvariant(), current).IsEnabledForCurrentExecutable, "Windows path matching ignores case");
        var moved = StartupRegistration.EvaluateCommand(StartupRegistration.BuildCommand(old), current);
        Check(moved.HasRegistration && moved.PointsToDifferentExecutable && !moved.IsEnabledForCurrentExecutable, "Portable directory move is diagnosed");
        Check(moved.RegisteredExecutable == old, "Old registered path retained for status");
        Check(!StartupRegistration.EvaluateCommand("\"" + current + "\"", current).IsEnabledForCurrentExecutable, "Non-silent entry is not reported as silently enabled");
        Check(!StartupRegistration.EvaluateCommand(new byte[] { 1, 2 }, current).IsEnabledForCurrentExecutable, "Malformed registry type is not enabled");
        Reject(@"TranslatorAnywhere.exe");
        Reject(@"C:\Tools\TranslatorAnywhere.dll");
        Reject("C:\\Bad\"Path\\TranslatorAnywhere.exe");
        Reject("C:\\Bad\nPath\\TranslatorAnywhere.exe");
        Reject("C:\\" + new string('x', 260) + "\\TranslatorAnywhere.exe");

        var shortcut = StartupRegistration.BuildShortcutDescription(current);
        Check(shortcut.TargetPath == current, "Shortcut target keeps the executable path separate from its arguments");
        Check(shortcut.Arguments == "--background", "Shortcut uses only the silent background argument");
        Check(shortcut.WorkingDirectory == Path.GetDirectoryName(current), "Shortcut starts in the portable program's directory");
        var active = StartupRegistration.EvaluateShortcut(shortcut, current);
        Check(active.ReadSucceeded && active.HasRegistration && active.IsEnabledForCurrentExecutable && !active.RequiresMigration,
            "An exact shortcut without a Windows override is recognized as enabled");
        Check(StartupRegistration.EvaluateShortcut(shortcut with { TargetPath = current.ToUpperInvariant(), WorkingDirectory = shortcut.WorkingDirectory.ToUpperInvariant() }, current).IsEnabledForCurrentExecutable,
            "Shortcut path and working-directory matching use Windows case-insensitive semantics");
        var absent = StartupRegistration.EvaluateShortcut(null, current);
        Check(absent.ReadSucceeded && !absent.HasRegistration && !absent.IsEnabledForCurrentExecutable && !absent.RequiresMigration,
            "No shortcut is disabled without requesting migration");
        var relocated = StartupRegistration.EvaluateShortcut(StartupRegistration.BuildShortcutDescription(old), current);
        Check(relocated.HasRegistration && relocated.PointsToDifferentExecutable && !relocated.IsEnabledForCurrentExecutable && relocated.RegisteredExecutable == old,
            "A shortcut left in an old portable directory is diagnosed");
        Check(!StartupRegistration.EvaluateShortcut(shortcut with { Arguments = "" }, current).IsEnabledForCurrentExecutable,
            "A shortcut without background arguments is not enabled");
        Check(!StartupRegistration.EvaluateShortcut(shortcut with { Arguments = "--background --unexpected" }, current).IsEnabledForCurrentExecutable,
            "Additional unexpected shortcut arguments are not silently accepted");
        Check(!StartupRegistration.EvaluateShortcut(shortcut with { WorkingDirectory = Path.GetDirectoryName(old)! }, current).IsEnabledForCurrentExecutable,
            "A shortcut using an old working directory is not enabled for the current portable program");
        Check(!StartupRegistration.EvaluateShortcut(shortcut with { TargetPath = "\"" + current + "\"" }, current).IsEnabledForCurrentExecutable,
            "Quoting belongs to legacy command lines, not shortcut target paths");

        Check(StartupRegistration.EvaluateStartupApproval(null) == StartupApprovalState.Missing,
            "A missing Windows approval entry leaves the shortcut's ordinary state effective");
        foreach (int state in new[] { 2, 6 })
        {
            Check(StartupRegistration.EvaluateStartupApproval(Approval(state)) == StartupApprovalState.Enabled,
                "Windows startup enabled DWORD " + state + " is recognized");
            Check(StartupRegistration.EvaluateShortcut(shortcut, current, Approval(state)).IsEnabledForCurrentExecutable,
                "A Windows enabled override preserves an exact shortcut");
        }
        foreach (int state in new[] { 3, 7, 9 })
        {
            Check(StartupRegistration.EvaluateStartupApproval(Approval(state)) == StartupApprovalState.Disabled,
                "Windows startup disabled DWORD " + state + " is recognized");
            var disabled = StartupRegistration.EvaluateShortcut(shortcut, current, Approval(state));
            Check(disabled.HasRegistration && !disabled.IsEnabledForCurrentExecutable && !disabled.RequiresMigration,
                "A present shortcut disabled by Windows is never reported enabled or auto-migrated");
        }
        foreach (object raw in new object[] { Approval(1), Approval(4), "2", new byte[] { 2, 0, 0 } })
        {
            Check(StartupRegistration.EvaluateStartupApproval(raw) == StartupApprovalState.Unknown,
                "Unrecognized or malformed Windows approval data is conservative");
            var unknown = StartupRegistration.EvaluateShortcut(shortcut, current, raw);
            Check(!unknown.IsEnabledForCurrentExecutable && !unknown.RequiresMigration && unknown.Message.Length > 0,
                "An unknown Windows override requires a clear diagnosis rather than enabling the shortcut");
        }
        Check(StartupRegistration.EvaluateStartupApproval(new byte[] { 2, 0, 0, 0 }) == StartupApprovalState.Enabled,
            "A valid approval DWORD does not require unused timestamp bytes");
        Check(StartupRegistration.EvaluateStartupApproval(new byte[] { 0, 0, 0, 2 }) == StartupApprovalState.Unknown,
            "Windows approval DWORD is interpreted in little-endian order");

        var legacy = StartupRegistration.EvaluateLegacyRegistration(command, current);
        Check(legacy.HasRegistration && !legacy.IsEnabledForCurrentExecutable && legacy.RequiresMigration,
            "A canonical legacy Run entry requests migration rather than being mistaken for a shortcut");
        Check(StartupRegistration.EvaluateLegacyRegistration(command, current, Approval(2), Approval(6)).RequiresMigration,
            "A legacy registration approved in both Windows channels remains eligible for migration");
        Check(!StartupRegistration.EvaluateLegacyRegistration(command, current, Approval(3)).RequiresMigration,
            "Disabling the old Run entry in Windows prevents automatic migration");
        Check(!StartupRegistration.EvaluateLegacyRegistration(command, current, null, Approval(7)).RequiresMigration,
            "Disabling the Startup-folder entry in Windows also prevents automatic migration");
        Check(!StartupRegistration.EvaluateLegacyRegistration(command, current, Approval(4)).RequiresMigration,
            "An unknown Run approval status prevents automatic migration");
        Check(!StartupRegistration.EvaluateLegacyRegistration(command, current, null, "unknown").RequiresMigration,
            "An unknown Startup-folder approval status prevents automatic migration");
        var obsoleteLegacy = StartupRegistration.EvaluateLegacyRegistration(StartupRegistration.BuildCommand(old), current);
        Check(obsoleteLegacy.PointsToDifferentExecutable && !obsoleteLegacy.RequiresMigration,
            "A legacy entry for a previous portable location is not migrated without a user decision");
        Check(!StartupRegistration.EvaluateLegacyRegistration(null, current).RequiresMigration,
            "An absent shortcut and absent legacy Run entry cannot create a migration request");
        Check(!StartupRegistration.EvaluateLegacyRegistration("\"" + current + "\"", current).RequiresMigration,
            "A non-background legacy entry cannot silently migrate into background startup");

        string artifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/startup-v0.3.8"));
        string directory = Path.Combine(artifacts, "shortcut-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string shortcutPath = Path.Combine(directory, "Translator Anywhere.lnk");
            StartupRegistration.WriteShortcut(shortcutPath, shortcut);
            Check(File.Exists(shortcutPath), "COM writes a shortcut only into the isolated artifact fixture");
            var restored = StartupRegistration.ReadShortcut(shortcutPath);
            Check(restored.TargetPath == shortcut.TargetPath && restored.Arguments == shortcut.Arguments && restored.WorkingDirectory == shortcut.WorkingDirectory,
                "Real shortcut persistence preserves space-containing target, arguments and working directory");
            Check(StartupRegistration.EvaluateShortcut(restored, current).IsEnabledForCurrentExecutable,
                "The shortcut read from disk has the same effective state as its intended registration");
            using (var unlocked = File.Open(shortcutPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(unlocked.Length > 0, "Shortcut COM objects release their file after reading and writing");
            StartupRegistration.WriteShortcut(shortcutPath, StartupRegistration.BuildShortcutDescription(old));
            var replaced = StartupRegistration.ReadShortcut(shortcutPath);
            Check(replaced.TargetPath == old && StartupRegistration.EvaluateShortcut(replaced, current).PointsToDifferentExecutable,
                "Updating an existing isolated shortcut replaces the old target without executing it");
            File.Delete(shortcutPath);
            Check(!File.Exists(shortcutPath), "The isolated startup fixture can be removed without a lingering COM file handle");
        }
        finally
        {
            string allowedRoot = artifacts.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullDirectory = Path.GetFullPath(directory);
            if (fullDirectory.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullDirectory).StartsWith("shortcut-check-", StringComparison.Ordinal)
                && Directory.Exists(fullDirectory))
                Directory.Delete(fullDirectory, recursive: true);
        }
        return count;
    }

    private static byte[] Approval(int state)
    {
        var raw = new byte[12];
        BitConverter.GetBytes(state).CopyTo(raw, 0);
        return raw;
    }
}
