using System;
using System.IO;
using TranslatorAnywhere.Services;

internal static class DistributionChecks
{
    internal static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "distribution-check-" + Guid.NewGuid().ToString("N"));
        string program = Path.Combine(root, "app");
        string local = Path.Combine(root, "LocalAppData");
        Directory.CreateDirectory(program);
        try
        {
            Equal(Path.Combine(program, "data"), SettingsStore.ResolveDataDirectory(program, local, null));
            File.WriteAllText(Path.Combine(program, "installed-mode"), "installed");
            Equal(Path.Combine(local, "AnywhereTranslator", "data"), SettingsStore.ResolveDataDirectory(program, local, null));
            Equal(Path.Combine(local, "AnywhereTranslator", "data"), SettingsStore.ResolveDataDirectory(program, local, " "));
            Equal(Path.Combine(root, "override"), SettingsStore.ResolveDataDirectory(program, local, Path.Combine(root, "override")));
            File.Delete(Path.Combine(program, "installed-mode"));
            Equal(Path.Combine(program, "data"), SettingsStore.ResolveDataDirectory(program, local, null));
            Console.WriteLine("PASS: 5 distribution data-path checks.");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup outside output directory.");
            Directory.Delete(root, true);
        }
    }

    private static void Equal(string expected, string actual)
    {
        if (Path.GetFullPath(expected) != actual) throw new InvalidOperationException("Unexpected data path: " + actual);
    }
}
