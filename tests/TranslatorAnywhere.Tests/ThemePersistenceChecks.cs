using System;
using System.IO;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

internal static class ThemePersistenceChecks
{
    public static int Run(string root)
    {
        string? previous = Environment.GetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR");
        string directory = Path.Combine(root, "themes");
        int count = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); count++; }
        try
        {
            Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", directory);
            var store = new SettingsStore();
            Check(store.Load().Theme == AppTheme.System, "New installations follow the Windows application theme");
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"ButtonText\":\"test\"}");
            Check(store.Load().Theme == AppTheme.System && store.Load().ButtonText == "test", "Older settings retain data and default to system theme");
            foreach (AppTheme mode in Enum.GetValues<AppTheme>())
            {
                var settings = new AppSettings { Theme = mode, ButtonColor = "#125678", ButtonTransparency = 25 };
                store.Save(settings);
                var restored = store.Load();
                Check(restored.Theme == mode, "Theme preference survives restarting: " + mode);
                Check(restored.ButtonColor == "#125678" && restored.ButtonTransparency == 25, "Theme preference does not change floating button appearance");
            }
            store.Save(new AppSettings { Theme = (AppTheme)99 });
            Check(store.Load().Theme == AppTheme.System, "Invalid theme is normalized");
        }
        finally { Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", previous); }
        return count;
    }
}