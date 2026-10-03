using System;
using System.Collections.Generic;

namespace TranslatorAnywhere.Models;

public enum AppTheme { System, Light, Dark }

public enum ButtonVisualMode { Text, Symbol, Icon }
public enum ButtonAnchor { SelectionTopRight, SelectionBottomRight, SelectionTopLeft, SelectionBottomLeft, Cursor }

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool Enabled { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = false;
    public ButtonVisualMode ButtonMode { get; set; } = ButtonVisualMode.Text;
    public string ButtonText { get; set; } = "翻";
    public string IconPath { get; set; } = "";
    public string ButtonColor { get; set; } = "#3390EC";
    // Percentage: 0 is opaque; 100 is fully transparent.
    public double ButtonTransparency { get; set; } = 0;
    public double ButtonSize { get; set; } = 32;
    public ButtonAnchor Anchor { get; set; } = ButtonAnchor.SelectionBottomRight;
    public double OffsetX { get; set; } = 8;
    public double OffsetY { get; set; } = 8;
    public int SelectionDelayMs { get; set; } = 180;
    public int AutoHideSeconds { get; set; } = 10;
    public bool ClipboardFallbackOnHotkey { get; set; } = true;
    public bool AutomaticClipboardFallback { get; set; }
    public List<string> ClipboardFallbackApplications { get; set; } = new();
    public List<string> ExcludedApplications { get; set; } = new();
    public List<ProviderConfiguration> Providers { get; set; } = new();
    public Guid? ActiveProviderId { get; set; }
    public string ProviderName { get; set; } = "DeepSeek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-flash";
    public string TargetLanguage { get; set; } = "简体中文";
    public bool DisableThinking { get; set; } = true;
    public bool TranslationPinned { get; set; }
}
