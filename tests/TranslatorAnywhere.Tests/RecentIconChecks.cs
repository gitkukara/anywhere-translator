using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

internal static class RecentIconChecks
{
    internal static int Run(string parent)
    {
        int assertions = 0;
        string? previous = Environment.GetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR");
        string directory = Path.Combine(parent, "recent-icon-history");
        Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", directory);
        try
        {
            var store = new SettingsStore();
            Check(store.Load().RecentIconPaths.Count == 0, "New installations have no recent icons");
            var settings = new AppSettings();
            var imports = new List<string>();
            for (int index = 0; index < 4; index++)
            {
                string source = Path.Combine(directory, "source.svg");
                File.WriteAllText(source, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\"><circle cx=\"16\" cy=\"16\" r=\"" + (10 + index) + "\"/></svg>");
                string imported = store.ImportIcon(source);
                imports.Add(imported);
                settings.IconPath = imported;
                settings.RecentIconPaths = RecentIconHistory.Add(settings.RecentIconPaths, imported);
                File.SetLastWriteTimeUtc(imported, new DateTime(2026, 1, 1, 0, index, 0, DateTimeKind.Utc));
                File.Delete(source);
            }
            store.Save(settings);
            var restored = store.Load();
            string[] expected = imports.Skip(1).Reverse().ToArray();
            Check(restored.RecentIconPaths.SequenceEqual(expected), "History persists only the newest three imports, newest first");
            Check(restored.RecentIconPaths.All(File.Exists), "History uses the imported copies after original files are deleted");
            restored.IconPath = imports[1];
            store.Save(restored);
            Check(store.Load().RecentIconPaths.SequenceEqual(expected), "Choosing an older icon does not change import order");
            Check(File.Exists(imports[0]), "Evicting an icon from history does not delete its file");
            Check(RecentIconHistory.Add(expected, imports[2]).SequenceEqual(new[] { imports[2], imports[3], imports[1] }),
                "Adding an existing path keeps history distinct");

            string settingsPath = Path.Combine(directory, "settings.json");
            string legacy = "{\"Providers\":[],\"IconPath\":\"\"}";
            File.WriteAllText(settingsPath, legacy);
            Check(store.Load().RecentIconPaths.SequenceEqual(expected), "Older settings recover the last three imported copies");
            Check(File.ReadAllText(settingsPath) == legacy, "History recovery does not rewrite settings while loading");
            File.WriteAllText(settingsPath, "{\"Providers\":[],\"RecentIconPaths\":[]}");
            Check(store.Load().RecentIconPaths.Count == 0, "Explicit empty history is not rediscovered from old files");
            File.WriteAllText(settingsPath, "{\"Providers\":[],\"IconPath\":\"legacy.png\",\"RecentIconPaths\":null}");
            Check(store.Load().RecentIconPaths.SequenceEqual(new[] { "legacy.png" }), "Null history retains the existing custom icon");
            File.WriteAllText(settingsPath, "{\"Providers\":[],\"RecentIconPaths\":[null,\"\",\" A.png \",\"a.PNG\",\"b.svg\",\"c.png\",\"d.png\"]}");
            Check(store.Load().RecentIconPaths.SequenceEqual(new[] { "A.png", "b.svg", "c.png" }), "Malformed history removes empty paths and duplicates and caps entries");
            return assertions;
        }
        finally { Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", previous); }

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
