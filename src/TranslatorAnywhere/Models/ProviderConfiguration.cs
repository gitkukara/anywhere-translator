using System;
using System.Collections.Generic;

namespace TranslatorAnywhere.Models;

public enum ProviderProtocol { OpenAICompatible, AnthropicMessages }

public sealed class ProviderConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PresetId { get; set; } = "custom";
    public string Name { get; set; } = "自定义服务";
    public string BaseUrl { get; set; } = "";
    public ProviderProtocol Protocol { get; set; } = ProviderProtocol.OpenAICompatible;
    public bool RequiresApiKey { get; set; } = true;
    public List<string> Models { get; set; } = new();
    public string SelectedModel { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool DisableThinking { get; set; } = true;
}
