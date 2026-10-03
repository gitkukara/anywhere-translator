using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

internal static class ProviderPersistenceChecks
{
    internal static int Run(string parentDirectory)
    {
        int assertions = 0;
        string previousDirectory = Environment.GetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR") ?? "";
        string directory = Path.Combine(parentDirectory, "provider-fixtures");
        Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", directory);
        try
        {
            var store = new SettingsStore();
            var fresh = store.Load();
            Check(fresh.Providers.Count == 1 && fresh.Providers[0].PresetId == "deepseek", "New configuration seeds DeepSeek");
            Check(fresh.ActiveProviderId == fresh.Providers[0].Id && fresh.Providers[0].Models.Count == 0
                && fresh.Providers[0].SelectedModel.Length == 0, "New presets require a fetched or manual model");
            Check(ProviderRegistry.AllPresets.Count == 7 && ProviderRegistry.AllPresets.Select(item => item.Id).Distinct().Count() == 7,
                "All required provider presets have unique IDs");
            var glm = ProviderRegistry.CreateProvider("zhipu");
            Check(glm.Protocol == ProviderProtocol.OpenAICompatible && glm.RequiresApiKey && glm.Models.Count == 0, "GLM preset requests credentials and a user-selected model");
            Check(TranslationService.BuildEndpoint(glm).AbsoluteUri == "https://open.bigmodel.cn/api/paas/v4/chat/completions", "GLM requests preserve the official v4 directory");
            Check(!ProviderRegistry.AllPresets.Any(preset => preset.Id == "anthropic"), "Anthropic is absent from the add catalogue");
            Check(TranslationService.BuildEndpoint(ProviderRegistry.CreateProvider("mimo")).AbsoluteUri == "https://api.xiaomimimo.com/v1/chat/completions", "MiMo preset uses its official endpoint");
            Check(TranslationService.BuildEndpoint(ProviderRegistry.CreateProvider("qwen")).AbsoluteUri == "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "Qwen preset preserves the compatibility path");
            Check(ProviderRegistry.CreateProvider("custom").Models.Count == 0, "Custom service starts without a hardcoded model list");

            const string legacyJson = "{\"ProviderName\":\"Custom legacy\",\"BaseUrl\":\"https://legacy.example.test/compatible-mode/v1\",\"Model\":\"preserved-model\",\"DisableThinking\":false}";
            string settingsPath = Path.Combine(directory, "settings.json");
            File.WriteAllText(settingsPath, legacyJson);
            const string legacyKey = "sk-migration-fixture-not-real";
            store.SaveApiKey(legacyKey);
            byte[] originalCredential = File.ReadAllBytes(Path.Combine(directory, "api-key.dpapi"));
            var migrated = store.Load();
            var legacy = ProviderRegistry.ResolveActive(migrated);
            Check(legacy.Id == ProviderRegistry.LegacyProviderId && legacy.Name == "Custom legacy", "Legacy provider gains stable identity");
            Check(legacy.BaseUrl == "https://legacy.example.test/compatible-mode/v1" && legacy.SelectedModel == "preserved-model"
                && legacy.Models.SequenceEqual(new[] { "preserved-model" }) && !legacy.DisableThinking, "Legacy endpoint path and model are preserved");
            Check(legacy.Protocol == ProviderProtocol.OpenAICompatible && legacy.RequiresApiKey, "Legacy transport and credential requirement are preserved");
            Check(store.ReadProviderApiKey(legacy.Id) == legacyKey, "Legacy key migrates to its provider");
            Check(File.ReadAllBytes(KeyFile(legacy.Id)).SequenceEqual(originalCredential), "Migrated credential copy preserves ciphertext exactly");
            Check(File.ReadAllBytes(Path.Combine(directory, "api-key.dpapi")).SequenceEqual(originalCredential), "Original encrypted legacy key remains intact");
            string[] backups = Directory.GetFiles(directory, "settings.json.legacy-*");
            Check(backups.Length == 1 && File.ReadAllText(backups[0]) == legacyJson, "Original legacy settings remain recoverable");
            Check(File.ReadAllText(settingsPath).Contains("\"Providers\"", StringComparison.Ordinal)
                && !File.ReadAllText(settingsPath).Contains(legacyKey, StringComparison.Ordinal), "Migration commits provider schema without plaintext credentials");
            store.SaveProviderApiKey(legacy.Id, "");
            Check(store.Load().ActiveProviderId == legacy.Id && store.ReadProviderApiKey(legacy.Id).Length == 0,
                "Cleared migrated credential cannot resurrect from the legacy original");

            var first = ProviderRegistry.CreateProvider("deepseek");
            first.SelectedModel = "manual-model";
            var second = ProviderRegistry.CreateProvider("custom");
            second.BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1";
            second.SelectedModel = "second-model";
            var configuration = new AppSettings { Providers = new() { first, second }, ActiveProviderId = first.Id };
            var keys = new Dictionary<Guid, string> { [first.Id] = "sk-first-provider-fixture", [second.Id] = "sk-second-provider-fixture" };
            store.SaveConfiguration(configuration, keys);
            Check(store.ReadProviderApiKey(first.Id) == keys[first.Id] && store.ReadProviderApiKey(second.Id) == keys[second.Id],
                "Independent provider credentials roundtrip");
            Check(!File.ReadAllText(settingsPath).Contains(keys[first.Id], StringComparison.Ordinal), "Composite save excludes credentials from settings");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(KeyFile(first.Id))).Contains(keys[first.Id], StringComparison.Ordinal),
                "Provider credential is encrypted on disk");
            configuration.ActiveProviderId = second.Id;
            Check(ProviderRegistry.ResolveActive(configuration).Id == second.Id, "Provider switch selects the explicit profile");
            ProviderRegistry.ApplyActiveToLegacyFields(configuration);
            Check(configuration.BaseUrl.EndsWith("/compatible-mode/v1", StringComparison.Ordinal) && configuration.Model == second.SelectedModel,
                "Compatibility adapter preserves arbitrary endpoint paths and selected model");
            second.Enabled = false;
            Throws<InvalidOperationException>(() => ProviderRegistry.ResolveActive(configuration), "Disabled active provider never fails over");
            store.SaveConfiguration(configuration, new Dictionary<Guid, string>());
            Check(!store.Load().Providers.Single(item => item.Id == second.Id).Enabled, "Disabled configuration can be saved");
            configuration.ActiveProviderId = Guid.NewGuid();
            Throws<InvalidOperationException>(() => ProviderRegistry.ResolveActive(configuration), "Missing active provider never fails over");
            configuration.ActiveProviderId = first.Id;
            first.RequiresApiKey = false;
            Throws<InvalidOperationException>(() => ProviderRegistry.ResolveActive(configuration), "Remote keyless provider is rejected");
            first.BaseUrl = "http://127.0.0.1:11434/v1";
            Check(ProviderRegistry.ResolveActive(configuration).Id == first.Id, "Keyless loopback provider is allowed");
            first.BaseUrl = "https://first.example.test/v1";
            first.RequiresApiKey = true;
            store.SaveConfiguration(configuration, new Dictionary<Guid, string>());

            byte[] previousSettings = File.ReadAllBytes(settingsPath);
            byte[] firstCipher = File.ReadAllBytes(KeyFile(first.Id));
            byte[] secondCipher = File.ReadAllBytes(KeyFile(second.Id));
            first.BaseUrl = "https://new.example.test/v1";
            using (var block = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Throws<InvalidOperationException>(() => store.SaveConfiguration(configuration, new Dictionary<Guid, string>
                    { [first.Id] = "sk-new-fixture", [second.Id] = "" }), "Failed settings commit reports composite failure");
            }
            Check(File.ReadAllBytes(settingsPath).SequenceEqual(previousSettings), "Failed commit preserves old endpoint configuration");
            Check(File.ReadAllBytes(KeyFile(first.Id)).SequenceEqual(firstCipher) && File.ReadAllBytes(KeyFile(second.Id)).SequenceEqual(secondCipher),
                "Failed commit restores both replaced and deleted credential ciphertext");
            Check(store.ReadProviderApiKey(first.Id) == keys[first.Id] && store.ReadProviderApiKey(second.Id) == keys[second.Id],
                "Failed commit leaves each original key readable");
            Throws<ArgumentException>(() => store.SaveConfiguration(configuration, new Dictionary<Guid, string> { [Guid.NewGuid()] = "fixture" }),
                "Composite save rejects foreign provider IDs");
            Throws<ArgumentException>(() => store.ReadProviderApiKey(Guid.Empty), "Empty credential identity is rejected");
            first.Models = new List<string> { " model-a ", "model-a", null!, "", "bad\nmodel" };
            store.SaveConfiguration(configuration, new Dictionary<Guid, string>());
            Check(store.Load().Providers.Single(item => item.Id == first.Id).Models.SequenceEqual(new[] { "model-a" }), "Model lists normalize nulls and duplicates");
            first.Models = Enumerable.Range(0, 1000).Select(index => "catalog-model-" + index).ToList();
            store.SaveConfiguration(configuration, new Dictionary<Guid, string>());
            Check(store.Load().Providers.Single(item => item.Id == first.Id).Models.Count == 1000, "Full discovered model catalog persists without truncation");

            configuration.Providers.Remove(second);
            store.SaveConfiguration(configuration, new Dictionary<Guid, string>());
            Check(!File.Exists(KeyFile(second.Id)) && File.Exists(Path.Combine(directory, "api-key.dpapi")),
                "Removed provider credentials are deleted after commit while original legacy credential remains");
            configuration.Providers.Clear();
            configuration.ActiveProviderId = null;
            store.SaveConfiguration(configuration, new Dictionary<Guid, string>());
            var empty = store.Load();
            Check(empty.Providers.Count == 0 && empty.ActiveProviderId is null, "Explicit empty configuration stays empty across restart");
            Throws<InvalidOperationException>(() => ProviderRegistry.ResolveActive(empty), "Empty configuration fails translation clearly");
            return assertions;

            string KeyFile(Guid id) => Path.Combine(directory, "api-key." + id.ToString("N") + ".dpapi");
            void Check(bool condition, string message)
            {
                assertions++;
                if (!condition) throw new Exception("Provider persistence assertion failed: " + message);
            }
            void Throws<T>(Action action, string message) where T : Exception
            {
                assertions++;
                try { action(); }
                catch (T) { return; }
                throw new Exception("Provider persistence assertion failed: " + message);
            }
        }
        finally { Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", previousDirectory); }
    }
}
