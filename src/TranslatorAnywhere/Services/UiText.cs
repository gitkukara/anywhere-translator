using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using TranslatorAnywhere.Models;

namespace TranslatorAnywhere.Services;

// Chinese keys are the original UI copy. User content and translation requests are never localized.
public static class UiText
{
    private static readonly Dictionary<string, string> English = LoadEnglish();
    private static readonly HashSet<string> Sources = new(English.Keys, StringComparer.Ordinal);
    private static readonly (string Source, string English, Regex Pattern)[] Formats = English
        .Where(pair => Regex.IsMatch(pair.Key, @"\{\d+\}"))
        .Select(pair => (pair.Key, pair.Value, new Regex("^" + Regex.Replace(Regex.Escape(pair.Key), @"\\\{(\d+)}", match => "(?<arg" + match.Groups[1].Value + ">.*?)") + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant)))
        .ToArray();
    private static bool _initialized;
    public static InterfaceLanguage Mode { get; private set; } = InterfaceLanguage.System;
    public static bool IsEnglish { get; private set; }
    public static event Action? Changed;

    public static InterfaceLanguage Resolve(InterfaceLanguage mode, CultureInfo systemCulture) => mode switch
    {
        InterfaceLanguage.English => InterfaceLanguage.English,
        InterfaceLanguage.Chinese => InterfaceLanguage.Chinese,
        _ => systemCulture.TwoLetterISOLanguageName == "zh" ? InterfaceLanguage.Chinese : InterfaceLanguage.English,
    };

    public static void EnsureInitialized()
    {
        if (!_initialized) Apply(InterfaceLanguage.System);
    }

    public static void Apply(InterfaceLanguage mode)
    {
        Application.Current.Dispatcher.VerifyAccess();
        mode = Enum.IsDefined(mode) ? mode : InterfaceLanguage.System;
        bool english = Resolve(mode, CultureInfo.CurrentUICulture) == InterfaceLanguage.English;
        bool changed = !_initialized || english != IsEnglish;
        Mode = mode;
        IsEnglish = english;
        _initialized = true;
        if (!changed) return;
        foreach (string source in Sources.ToArray()) Application.Current.Resources[Key(source)] = T(source);
        Changed?.Invoke();
    }

    public static string T(string source)
    {
        if (!IsEnglish || string.IsNullOrEmpty(source)) return source;
        if (English.TryGetValue(source, out string? english)) return english;
        if (source.Contains('\n')) return string.Join("\n", source.Split('\n').Select(T));
        foreach (var format in Formats)
        {
            Match match = format.Pattern.Match(source);
            if (!match.Success) continue;
            var args = Enumerable.Range(0, match.Groups.Count - 1).Select(index => (object)match.Groups["arg" + index].Value).ToArray();
            if (format.Source is "未保存：{0}" or "图标导入失败：{0}") args[0] = T((string)args[0]);
            return string.Format(CultureInfo.InvariantCulture, format.English, args);
        }
        return source;
    }

    public static void Set(FrameworkElement element, DependencyProperty property, string source)
    {
        EnsureInitialized();
        string key = Key(source);
        if (Sources.Add(source)) Application.Current.Resources[key] = T(source);
        element.SetResourceReference(property, key);
    }

    private static string Key(string source) => "Ui." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..16];

    private static Dictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("TranslatorAnywhere.Localization.en.json")
            ?? throw new InvalidOperationException("English UI resources are missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
