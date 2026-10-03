using System;
using System.IO;
using System.Text.Json;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

internal static class ButtonAppearanceChecks
{
    internal static int Run(string parentDirectory)
    {
        int assertions = 0;
        string previousDirectory = Environment.GetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR") ?? "";
        string directory = Path.Combine(parentDirectory, "appearance-fixtures");
        Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", directory);
        try
        {
            var store = new SettingsStore();
            string path = Path.Combine(directory, "settings.json");
            Check(store.Load().ButtonTransparency == 0, "New settings are fully opaque");

            string legacy = "{\"Providers\":[],\"ButtonColor\":\"#80123456\"}";
            File.WriteAllText(path, legacy);
            var settings = store.Load();
            Near((1 - 128 / 255d) * 100, settings.ButtonTransparency, "Legacy alpha migrates to transparency");
            Check(settings.ButtonColor == "#123456", "Migrated color has opaque RGB format");
            Check(File.ReadAllText(path) == legacy, "Transparency migration does not write settings during load");
            store.Save(settings);
            var reloaded = store.Load();
            Near(settings.ButtonTransparency, reloaded.ButtonTransparency, "Saved transparency migration is idempotent");
            Check(reloaded.ButtonColor == "#123456", "Saved migrated RGB remains opaque");

            Write("#80123456", "0");
            settings = store.Load();
            Check(settings.ButtonTransparency == 0 && settings.ButtonColor == "#123456", "Explicit zero overrides old alpha");
            Write("#00123456", "25");
            settings = store.Load();
            Check(settings.ButtonTransparency == 25 && settings.ButtonColor == "#123456", "Explicit transparency never multiplies color alpha");
            Write("#123");
            settings = store.Load();
            Check(settings.ButtonColor == "#112233" && settings.ButtonTransparency == 0, "Short RGB normalizes without adding transparency");
            Write("Red");
            settings = store.Load();
            Check(settings.ButtonColor == "#FF0000" && settings.ButtonTransparency == 0, "Named colors normalize to RGB");
            Write("Transparent");
            settings = store.Load();
            Check(settings.ButtonColor == "#FFFFFF" && settings.ButtonTransparency == 100, "Named transparent legacy color migrates alpha");
            Write("#80123456", "1e999");
            Check(store.Load().ButtonTransparency == 0, "Explicit non-finite JSON transparency uses zero instead of old alpha");

            foreach (double value in new[] { -1d, 101d, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 37.125 })
            {
                var candidate = new AppSettings { ButtonColor = "#80123456", ButtonTransparency = value };
                store.Save(candidate);
                double expected = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
                Near(expected, store.Load().ButtonTransparency, "Transparency clamps and safely serializes all boundary inputs");
                Check(store.Load().ButtonColor == "#123456", "Explicit settings always store opaque RGB");
            }

            foreach (string language in new[] { "简体中文", "繁体中文", "English", "日本語", "한국어" })
            {
                store.Save(new AppSettings { TargetLanguage = language });
                Check(store.Load().TargetLanguage == language, "Supported language persists");
            }
            foreach (string language in new[] { "", "Español", "english" })
            {
                string json = "{\"Providers\":[],\"TargetLanguage\":" + JsonSerializer.Serialize(language) + "}";
                File.WriteAllText(path, json);
                Check(store.Load().TargetLanguage == "简体中文", "Unsupported language uses simplified Chinese");
                Check(File.ReadAllText(path) == json, "Language normalization does not write settings during load");
            }
            return assertions;

            void Write(string color, string? transparency = null)
            {
                string json = "{\"Providers\":[],\"ButtonColor\":" + JsonSerializer.Serialize(color)
                    + (transparency is null ? "" : ",\"ButtonTransparency\":" + transparency) + "}";
                File.WriteAllText(path, json);
            }
            void Check(bool condition, string message)
            {
                assertions++;
                if (!condition) throw new Exception("Button appearance assertion failed: " + message);
            }
            void Near(double expected, double actual, string message) => Check(Math.Abs(expected - actual) < 0.0000001, message);
        }
        finally { Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", previousDirectory); }
    }
}
