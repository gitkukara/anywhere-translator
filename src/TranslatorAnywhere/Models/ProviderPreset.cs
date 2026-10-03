namespace TranslatorAnywhere.Models;

public sealed record ProviderPreset(string Id, string Name, string Category, string BaseUrl,
    ProviderProtocol Protocol, bool RequiresApiKey, string AccentColor, string Symbol);
