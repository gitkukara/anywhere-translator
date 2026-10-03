using System;
using System.Collections.Generic;
using System.Linq;
using TranslatorAnywhere.Models;

namespace TranslatorAnywhere.Services;

public static class ProviderRegistry
{
    // A fixed ID makes an interrupted legacy migration retry against the same credential file.
    public static readonly Guid LegacyProviderId = new("af837c79-b675-4930-a42f-8f9b97d7ff43");

    public static IReadOnlyList<ProviderPreset> AllPresets { get; } = Array.AsReadOnly(new[]
    {
        new ProviderPreset("deepseek", "DeepSeek", "国内服务", "https://api.deepseek.com", ProviderProtocol.OpenAICompatible, true, "#4D6BFE", "D"),
        new ProviderPreset("openai", "OpenAI", "国际服务", "https://api.openai.com/v1", ProviderProtocol.OpenAICompatible, true, "#10A37F", "O"),
        new ProviderPreset("anthropic", "Anthropic", "国际服务", "https://api.anthropic.com/v1", ProviderProtocol.AnthropicMessages, true, "#D97757", "A"),
        new ProviderPreset("gemini", "Google Gemini", "国际服务", "https://generativelanguage.googleapis.com/v1beta/openai", ProviderProtocol.OpenAICompatible, true, "#4285F4", "G"),
        new ProviderPreset("custom", "自定义服务", "自定义", "", ProviderProtocol.OpenAICompatible, true, "#70839D", "+")
    });

    public static ProviderConfiguration CreateProvider(string presetId)
    {
        var preset = AllPresets.FirstOrDefault(item => string.Equals(item.Id, presetId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("未知的服务预设。", nameof(presetId));
        return new ProviderConfiguration
        {
            PresetId = preset.Id, Name = preset.Name, BaseUrl = preset.BaseUrl,
            Protocol = preset.Protocol, RequiresApiKey = preset.RequiresApiKey
        };
    }

    public static void InitializeLegacy(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Providers is { Count: > 0 }) return;
        string legacyName = Clean(settings.ProviderName, 80);
        var preset = AllPresets.FirstOrDefault(item => string.Equals(item.Name, legacyName, StringComparison.OrdinalIgnoreCase));
        var profile = CreateProvider(preset?.Id ?? "custom");
        profile.Id = LegacyProviderId;
        // The legacy backend always used the OpenAI wire format, even when a custom provider was named Anthropic.
        profile.Protocol = ProviderProtocol.OpenAICompatible;
        profile.RequiresApiKey = true;
        profile.Name = legacyName.Length == 0 ? profile.Name : legacyName;
        profile.BaseUrl = Clean(settings.BaseUrl, 2048);
        profile.SelectedModel = Clean(settings.Model, 200);
        profile.DisableThinking = settings.DisableThinking;
        if (profile.SelectedModel.Length > 0) profile.Models.Add(profile.SelectedModel);
        settings.Providers = new List<ProviderConfiguration> { profile };
        settings.ActiveProviderId = profile.Id;
        Normalize(settings);
    }

    public static void Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var ids = new HashSet<Guid>();
        settings.Providers = (settings.Providers ?? new List<ProviderConfiguration>()).Where(profile => profile is not null).Take(100).ToList();
        foreach (var profile in settings.Providers)
        {
            if (profile.Id == Guid.Empty || !ids.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid();
                ids.Add(profile.Id);
            }
            profile.PresetId = Clean(profile.PresetId, 80);
            profile.Name = Clean(profile.Name, 80);
            profile.BaseUrl = Clean(profile.BaseUrl, 2048);
            profile.SelectedModel = Clean(profile.SelectedModel, 200);
            profile.Models = (profile.Models ?? new List<string>())
                .Where(model => !string.IsNullOrWhiteSpace(model) && model.Length <= 200 && !model.Contains('\r') && !model.Contains('\n'))
                .Select(model => model.Trim()).Distinct(StringComparer.Ordinal).Take(1000).ToList();
        }
        // Preserve an absent or disabled active provider: translation must never switch providers implicitly.
    }

    public static ProviderConfiguration ResolveActive(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var profile = settings.ActiveProviderId is Guid id
            ? settings.Providers?.FirstOrDefault(item => item is not null && item.Id == id) : null;
        if (profile is null) throw new InvalidOperationException("请在服务设置中选择用于翻译的服务。");
        if (!profile.Enabled) throw new InvalidOperationException("当前翻译服务已停用，请启用它或选择其他服务。");
        if (!Enum.IsDefined(profile.Protocol)) throw new InvalidOperationException("当前服务的 API 协议无效。");
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("请填写有效的 API 地址。远程服务使用 HTTPS，本机服务可使用 HTTP，且不包含用户名、查询参数或片段。");
        if (!profile.RequiresApiKey && !uri.IsLoopback)
            throw new InvalidOperationException("只有本机服务允许不使用 API Key。");
        if (string.IsNullOrWhiteSpace(profile.SelectedModel) || profile.SelectedModel.Contains('\r') || profile.SelectedModel.Contains('\n'))
            throw new InvalidOperationException("请选择模型或手动填写模型名称。");
        return profile;
    }

    public static void ApplyActiveToLegacyFields(AppSettings settings)
    {
        var profile = ResolveActive(settings);
        settings.ProviderName = profile.Name;
        settings.BaseUrl = profile.BaseUrl;
        settings.Model = profile.SelectedModel;
        settings.DisableThinking = profile.DisableThinking;
    }

    private static string Clean(string? value, int maximumLength)
    {
        value = (value ?? "").Trim();
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
