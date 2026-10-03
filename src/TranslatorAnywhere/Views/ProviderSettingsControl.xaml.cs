using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

namespace TranslatorAnywhere.Views;

public partial class ProviderSettingsControl : UserControl, IDisposable
{
    private readonly ProviderConnectionService _connection = new();
    private readonly List<ProviderConfiguration> _providers = new();
    private readonly Dictionary<Guid, string> _originalKeys = new();
    private readonly Dictionary<Guid, string> _pendingKeys = new();
    private readonly HashSet<Guid> _unreadableKeys = new();
    private readonly HashSet<Guid> _tested = new();
    private readonly Dictionary<Guid, string> _acceptedEndpoints = new();
    private readonly DispatcherTimer _statusTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
    private Func<Guid, string> _readKey = _ => "";
    private Guid? _activeId;
    private ProviderConfiguration? _editing;
    private CancellationTokenSource? _operation;
    private int _operationVersion;
    private bool _loadingEditor;
    private bool _renderingList;
    private bool _disposed;

    public Func<string> TargetLanguage { get; set; } = () => "简体中文";
    public event Action? ConfigurationChanged;
    public Func<ProviderConfiguration, string, CancellationToken, Task<IReadOnlyList<string>>>? ModelLoader { get; set; }
    public Func<ProviderConfiguration, string, string, CancellationToken, Task<string>>? ConnectionTester { get; set; }
    public IReadOnlyDictionary<Guid, string> PendingApiKeys => new Dictionary<Guid, string>(_pendingKeys);
    public IReadOnlyList<ProviderConfiguration> DraftProviders => _providers.Select(Clone).ToArray();
    public Guid? ActiveProviderId => _activeId;
    public Guid? EditingProviderId => _editing?.Id;

    public void AcceptSaved()
    {
        foreach (var pair in _pendingKeys)
        {
            _originalKeys[pair.Key] = pair.Value;
            _unreadableKeys.Remove(pair.Key);
        }
        _pendingKeys.Clear();
        foreach (var id in _originalKeys.Keys.Where(id => !_providers.Any(p => p.Id == id)).ToArray()) _originalKeys.Remove(id);
        RememberAcceptedEndpoints();
        if (ListView.Visibility == Visibility.Visible) RenderList();
        else UpdateCredentialHint();
    }

    public ProviderSettingsControl()
    {
        InitializeComponent();
        SelectedModelCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(SelectedModel_TextChanged), true);
        _statusTimer.Tick += (_, _) => ClearStatus();
    }

    public void Load(AppSettings settings, Func<Guid, string> readKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(readKey);
        CancelOperation();
        _readKey = readKey;
        _providers.Clear();
        _providers.AddRange((settings.Providers ?? new()).Select(Clone));
        _activeId = settings.ActiveProviderId;
        _originalKeys.Clear();
        _pendingKeys.Clear();
        _unreadableKeys.Clear();
        _tested.Clear();
        RememberAcceptedEndpoints();
        _editing = null;
        ShowProviderList();
    }

    public void ApplyTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CommitEditor();
        foreach (var provider in _providers)
        {
            ValidateDraft(provider);
            if (provider.BaseUrl.Length == 0 && _acceptedEndpoints.TryGetValue(provider.Id, out string? accepted) && accepted.Length > 0)
                throw new ArgumentException("请填写完整的 API 地址。");
            ValidateKey(GetKey(provider.Id));
        }
        settings.Providers = _providers.Select(Clone).ToList();
        settings.ActiveProviderId = _activeId;
        var active = settings.Providers.FirstOrDefault(p => p.Id == _activeId);
        if (active is not null)
        {
            settings.ProviderName = active.Name;
            settings.BaseUrl = active.BaseUrl;
            settings.Model = active.SelectedModel;
            settings.DisableThinking = active.DisableThinking;
        }
    }

    public void OpenCatalog()
    {
        CommitEditor();
        CancelOperation();
        _editing = null;
        ListView.Visibility = DetailView.Visibility = Visibility.Collapsed;
        CatalogView.Visibility = Visibility.Visible;
        RefreshCatalog();
        ClearStatus();
        CatalogBackButton.Focus();
    }

    public Guid AddPreset(string presetId)
    {
        if (_providers.Count >= 100) throw new ArgumentException("最多可以添加 100 个服务。");
        var provider = ProviderRegistry.CreateProvider(presetId);
        if (provider.PresetId == "custom") provider.Name = "自定义接口";
        _providers.Add(provider);
        _originalKeys[provider.Id] = "";
        OpenProvider(provider.Id);
        NotifyConfigurationChanged();
        return provider.Id;
    }

    public void OpenProvider(Guid id)
    {
        CommitEditor();
        CancelOperation();
        _editing = _providers.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("服务不存在。");
        ListView.Visibility = CatalogView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;
        LoadEditor();
        ClearStatus();
    }

    public void ShowProviderList()
    {
        CommitEditor();
        CancelOperation();
        _editing = null;
        CatalogView.Visibility = DetailView.Visibility = Visibility.Collapsed;
        ListView.Visibility = Visibility.Visible;
        RenderList();
        ClearStatus();
    }

    public void ActivateProvider(Guid id)
    {
        CommitEditor();
        var provider = _providers.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("服务不存在。");
        if (!provider.Enabled) throw new ArgumentException("请先启用这个服务。");
        ValidateConnection(provider, requireModel: true);
        bool changed = _activeId != id;
        _activeId = id;
        if (_editing?.Id == id) UpdateActiveButton();
        RenderList();
        ClearStatus();
        if (changed) NotifyConfigurationChanged();
    }

    private void RenderList()
    {
        _renderingList = true;
        try { RenderProviderRows(); }
        finally { _renderingList = false; }
    }

    private void RenderProviderRows()
    {
        ProviderListPanel.Children.Clear();
        EmptyProvidersPanel.Visibility = _providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProviderListBorder.Visibility = _providers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var provider in _providers)
        {
            var row = new Grid { Margin = new Thickness(13, 11, 9, 11) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(39) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.Children.Add(CreateLogo(provider.PresetId));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            text.Children.Add(Themed(new TextBlock { Text = provider.Name.Length > 0 ? provider.Name : "未命名服务", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, TextBlock.ForegroundProperty, "InkBrush"));
            text.Children.Add(Themed(new TextBlock { Text = Host(provider.BaseUrl) + "  ·  " + provider.Models.Count + " 个模型", FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }, TextBlock.ForegroundProperty, "MutedBrush"));
            string model = provider.SelectedModel.Length > 0 ? provider.SelectedModel : "未选择模型";
            string providerKey = GetKey(provider.Id);
            string credential = _unreadableKeys.Contains(provider.Id) ? "密钥需重新填写" : providerKey.Length > 0 ? "已配置密钥" : provider.RequiresApiKey ? "未配置密钥" : "本机服务";
            var detail = new TextBlock { Text = model + "  ·  " + credential + (_tested.Contains(provider.Id) ? "  ·  测试通过" : ""), FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
            var open = new Button { Content = content, Style = (Style)FindResource("ProviderRowButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 54, ToolTip = "编辑“" + provider.Name + "”" };
            open.Click += (_, _) => OpenProvider(provider.Id);
            row.Children.Add(open);
            bool current = _activeId == provider.Id;
            var use = new Button { Content = current ? "当前服务" : "使用", MinWidth = 74, Style = (Style)FindResource("SubtleButton"), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 9, 0), ToolTip = current ? "当前翻译服务" : "设为当前翻译服务", IsEnabled = provider.Enabled || current };
            if (current) use.SetResourceReference(Control.BackgroundProperty, "AccentLightBrush");
            use.SetResourceReference(Control.ForegroundProperty, current ? "AccentBrush" : "MutedBrush");
            use.Click += (_, _) => { try { ActivateProvider(provider.Id); } catch (ArgumentException ex) { SetStatus(ex.Message, true); } };
            Grid.SetColumn(use, 1);
            row.Children.Add(use);
            var enabled = new CheckBox { Style = (Style)FindResource("SwitchBar"), IsChecked = provider.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "启用或停用这个服务", Margin = new Thickness(0, 0, 1, 0) };
            enabled.Checked += (_, _) => SetProviderEnabled(provider, true);
            enabled.Unchecked += (_, _) => SetProviderEnabled(provider, false);
            Grid.SetColumn(enabled, 2);
            row.Children.Add(enabled);
            var chevron = new Button { Style = (Style)FindResource("IconButton"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "编辑服务" };
            var chevronIcon = new HugeIcon { Kind = HugeIconKind.ChevronRight, Width = 16, Height = 16, IsHitTestVisible = false };
            chevronIcon.SetBinding(HugeIcon.ForegroundProperty, new Binding(nameof(Button.Foreground)) { Source = chevron });
            chevron.Content = chevronIcon;
            System.Windows.Automation.AutomationProperties.SetName(chevron, "编辑服务");
            chevron.Click += (_, _) => OpenProvider(provider.Id);
            Grid.SetColumn(chevron, 3);
            row.Children.Add(chevron);
            ProviderListPanel.Children.Add(Themed(new Border { BorderThickness = new Thickness(0, 0, 0, provider == _providers[^1] ? 0 : 1), Child = row }, Border.BorderBrushProperty, "DividerBrush"));
        }
    }

    private void SetProviderEnabled(ProviderConfiguration provider, bool enabled)
    {
        if (_renderingList || provider.Enabled == enabled) return;
        provider.Enabled = enabled;
        _tested.Remove(provider.Id);
        ClearStatus();
        RenderList();
        NotifyConfigurationChanged();
    }

    private UIElement CreateLogo(string presetId)
    {
        if (TryFindResource("ProviderIcon." + presetId) is ImageSource icon)
            return new Image { Source = icon, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var preset = ProviderRegistry.AllPresets.FirstOrDefault(p => p.Id == presetId);
        return new HugeIcon { Kind = HugeIconKind.Settings, Foreground = Accent(preset?.AccentColor), Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    }

    private void RefreshCatalog()
    {
        var entries = ProviderRegistry.AllPresets
            .Select(p => new PresetEntry(p.Id, p.Id == "custom" ? "自定义接口" : p.Name, p.Category, TryFindResource("ProviderIcon." + p.Id) as ImageSource, Accent(p.AccentColor))).ToArray();
        CatalogItems.ItemsSource = entries;
    }

    private void LoadEditor()
    {
        if (_editing is not { } p) return;
        bool wasLoading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            DetailTitle.Text = p.Name;
            DetailLogo.Source = TryFindResource("ProviderIcon." + p.PresetId) as ImageSource;
            DetailLogo.Visibility = DetailLogo.Source is null ? Visibility.Collapsed : Visibility.Visible;
            ProviderNameBox.Text = p.Name;
            ProviderBaseUrlBox.Text = p.BaseUrl;
            ProtocolBox.SelectedIndex = p.Protocol == ProviderProtocol.AnthropicMessages ? 1 : 0;
            ProtocolRow.Visibility = p.PresetId == "custom" ? Visibility.Visible : Visibility.Collapsed;
            ProviderApiKeyBox.Password = GetKey(p.Id);
            ModelListBox.Text = string.Join(Environment.NewLine, p.Models);
            ModelListPanel.Visibility = Visibility.Collapsed;
            ModelListToggleLabel.Text = "管理模型列表";
            ModelListToggleIcon.Kind = HugeIconKind.ChevronRight;
            SelectedModelCombo.ItemsSource = p.Models.ToArray();
            SelectedModelCombo.Text = p.SelectedModel;
            ProviderEnabledCheck.IsChecked = p.Enabled;
            ProviderThinkingCheck.IsChecked = p.DisableThinking;
            ProviderThinkingRow.Visibility = p.PresetId == "deepseek" ? Visibility.Visible : Visibility.Collapsed;
            UpdateCredentialHint();
            UpdateActiveButton();
        }
        finally { _loadingEditor = wasLoading; }
    }

    private void CommitEditor()
    {
        if (_loadingEditor || _editing is not { } p) return;
        string selected = SelectedModelCombo.Text.Trim();
        var models = ParseModels(ModelListBox.Text);
        if (selected.Length > 0 && !models.Contains(selected, StringComparer.Ordinal)) models.Add(selected);
        p.Name = ProviderNameBox.Text.Trim();
        p.BaseUrl = ProviderBaseUrlBox.Text.Trim().TrimEnd('/');
        if (p.PresetId == "custom")
            p.RequiresApiKey = !Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback;
        p.Protocol = ProtocolBox.SelectedIndex == 1 ? ProviderProtocol.AnthropicMessages : ProviderProtocol.OpenAICompatible;
        p.Models = models;
        p.SelectedModel = selected;
        p.Enabled = ProviderEnabledCheck.IsChecked == true;
        p.DisableThinking = ProviderThinkingCheck.IsChecked == true;
        DetailTitle.Text = p.Name;
        UpdateCredentialHint();
        UpdateActiveButton();
    }

    private void UpdateCredentialHint()
    {
        if (_editing is not { } p) return;
        CredentialHint.Text = _unreadableKeys.Contains(p.Id) ? "密钥需重新填写。" : !p.RequiresApiKey ? "本机服务可留空。" : "";
        CredentialHint.Visibility = CredentialHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateActiveButton()
    {
        if (_editing is null) return;
        bool current = _activeId == _editing.Id;
        UseProviderButton.Content = current ? "当前服务" : "设为当前服务";
        UseProviderButton.SetResourceReference(Control.ForegroundProperty, current ? "AccentBrush" : "InkBrush");
        UseProviderButton.IsEnabled = _operation is null && (_editing.Enabled || current);
    }

    private string GetKey(Guid id)
    {
        if (_pendingKeys.TryGetValue(id, out string? changed)) return changed;
        if (_originalKeys.TryGetValue(id, out string? existing)) return existing;
        try { existing = _readKey(id) ?? ""; }
        catch { _unreadableKeys.Add(id); existing = ""; }
        _originalKeys[id] = existing;
        return existing;
    }

    private void Editor_KeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor || _editing is not { } p) return;
        _tested.Remove(p.Id);
        string value = ProviderApiKeyBox.Password.Trim();
        string original = _originalKeys.TryGetValue(p.Id, out string? key) ? key : "";
        if (value == original && !_unreadableKeys.Contains(p.Id)) _pendingKeys.Remove(p.Id);
        else _pendingKeys[p.Id] = value;
        ClearStatus();
        NotifyConfigurationChanged();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => EditorChanged();
    private void EditorSelection_Changed(object sender, SelectionChangedEventArgs e) => EditorChanged();

    private void Editor_Changed(object sender, RoutedEventArgs e) => EditorChanged();
    private void EditorChanged()
    {
        if (_loadingEditor || _editing is null) return;
        _tested.Remove(_editing.Id);
        CommitEditor();
        ClearStatus();
        NotifyConfigurationChanged();
    }
    private void Models_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingEditor || _editing is null) return;
        string selected = SelectedModelCombo.Text;
        bool wasLoading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            _editing.Models = ParseModels(ModelListBox.Text);
            SelectedModelCombo.ItemsSource = _editing.Models.ToArray();
            SelectedModelCombo.Text = selected;
        }
        finally { _loadingEditor = wasLoading; }
        EditorChanged();
    }

    private void SelectedModel_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is not TextBox { Name: "PART_EditableTextBox" }) return;
        if (_loadingEditor || _editing is null || _editing.SelectedModel == SelectedModelCombo.Text.Trim()) return;
        EditorChanged();
    }

    private void SelectedModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || _editing is null) return;
        Guid id = _editing.Id;
        // ComboBox raises SelectionChanged before updating Text; the editable TextBox usually handles this first.
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (_loadingEditor || _disposed || _editing?.Id != id || _editing.SelectedModel == SelectedModelCombo.Text.Trim()) return;
            EditorChanged();
        }));
    }
    private void ModelListToggle_Click(object sender, RoutedEventArgs e)
    {
        bool expand = ModelListPanel.Visibility != Visibility.Visible;
        ModelListPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ModelListToggleLabel.Text = expand ? "收起模型列表" : "管理模型列表";
        ModelListToggleIcon.Kind = expand ? HugeIconKind.ChevronLeft : HugeIconKind.ChevronRight;
    }
    private void SelectedModel_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loadingEditor || _editing is null || _editing.SelectedModel == SelectedModelCombo.Text.Trim()) return;
        EditorChanged();
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e) => OpenCatalog();
    private void BackToList_Click(object sender, RoutedEventArgs e) => ShowProviderList();
    private void DetailBack_Click(object sender, RoutedEventArgs e) => ShowProviderList();
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        try { AddPreset(id); } catch (ArgumentException ex) { SetStatus(ex.Message, true); }
    }
    private void UseProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        try { ActivateProvider(_editing.Id); } catch (ArgumentException ex) { SetStatus(ex.Message, true); }
    }
    private void RemoveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } p) return;
        CancelOperation();
        _providers.Remove(p);
        _pendingKeys.Remove(p.Id);
        _tested.Remove(p.Id);
        if (_activeId == p.Id) _activeId = null;
        _editing = null;
        ShowProviderList();
        NotifyConfigurationChanged();
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RunConnectionAsync(models: true);
    private async void TestConnection_Click(object sender, RoutedEventArgs e) => await RunConnectionAsync(models: false);
    private void CancelConnection_Click(object sender, RoutedEventArgs e) { CancelOperation(); SetStatus("请求已停止。"); }
    public void CancelRequests() => CancelOperation();

    private async Task RunConnectionAsync(bool models)
    {
        if (_editing is null || _operation is not null || _disposed) return;
        CommitEditor();
        var profile = Clone(_editing);
        try { ValidateConnection(profile, requireModel: !models); }
        catch (ArgumentException ex) { SetStatus(ex.Message, true); return; }
        var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        int version = ++_operationVersion;
        SetBusy(true);
        SetStatus(models ? "正在获取模型列表..." : "正在测试连接...", persistent: true);
        try
        {
            string key = GetKey(profile.Id);
            if (models)
            {
                var list = ModelLoader is null
                    ? await _connection.ListModelsAsync(profile, key, cancellation.Token)
                    : await ModelLoader(profile, key, cancellation.Token);
                if (!IsCurrentOperation(version, profile.Id, cancellation)) return;
                var names = list.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToList();
                if (names.Count == 0) throw new InvalidOperationException("服务没有返回可用模型，可手动填写模型名称。");
                _editing!.Models = names;
                _tested.Remove(profile.Id);
                bool wasLoading = _loadingEditor;
                _loadingEditor = true;
                try
                {
                    ModelListBox.Text = string.Join(Environment.NewLine, names);
                    SelectedModelCombo.ItemsSource = names.ToArray();
                    SelectedModelCombo.Text = profile.SelectedModel;
                }
                finally { _loadingEditor = wasLoading; }
                NotifyConfigurationChanged();
                SetStatus($"已获取 {names.Count} 个模型，请选择翻译模型。");
            }
            else
            {
                _ = ConnectionTester is null
                    ? await _connection.TestConnectionAsync(profile, key, TargetLanguage(), cancellation.Token)
                    : await ConnectionTester(profile, key, TargetLanguage(), cancellation.Token);
                if (!IsCurrentOperation(version, profile.Id, cancellation)) return;
                _tested.Add(profile.Id);
                SetStatus("连接成功，所选模型已返回译文。");
            }
        }
        catch (OperationCanceledException) { if (IsCurrentOperation(version, profile.Id, cancellation)) SetStatus("请求已停止。"); }
        catch (Exception ex)
        {
            if (IsCurrentOperation(version, profile.Id, cancellation))
            {
                _tested.Remove(profile.Id);
                // Error responses may echo authorization data; show only local, fixed messages.
                string message = ex is TimeoutException ? "请求超过 30 秒，请检查网络或稍后重试。" : models ? "模型获取失败。请检查密钥、API 地址与网络，也可手动填写模型。" : "连接测试失败。请检查密钥、模型、API 地址与网络。";
                SetStatus(message, true);
            }
        }
        finally
        {
            if (_operationVersion == version)
            {
                _operation = null;
                SetBusy(false);
            }
            cancellation.Dispose();
        }
    }

    private bool IsCurrentOperation(int version, Guid id, CancellationTokenSource source) =>
        !_disposed && version == _operationVersion && !source.IsCancellationRequested && _editing?.Id == id;

    private void CancelOperation()
    {
        _operationVersion++;
        var operation = _operation;
        _operation = null;
        operation?.Cancel();
        if (!_disposed && ProviderNameBox is not null) SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        foreach (Control field in new Control[] { ProviderNameBox, ProviderBaseUrlBox, ProtocolBox, ProviderApiKeyBox, SelectedModelCombo, ModelListBox, ProviderEnabledCheck, ProviderThinkingCheck, RefreshModelsButton, TestConnectionButton, RemoveProviderButton }) field.IsEnabled = !busy;
        UseProviderButton.IsEnabled = !busy && _editing is not null && (_editing.Enabled || _activeId == _editing.Id);
        CancelConnectionButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ValidateConnection(ProviderConfiguration profile, bool requireModel)
    {
        ValidateDraft(profile);
        if (profile.BaseUrl.Length == 0) throw new ArgumentException("请填写 API 地址。");
        try { TranslationService.BuildEndpoint(profile); }
        catch (ArgumentException) { throw new ArgumentException("请填写有效的 API 地址；远程服务使用 HTTPS，本机服务可使用 HTTP。"); }
        if (requireModel && profile.SelectedModel.Length == 0) throw new ArgumentException("请选择或手动填写翻译模型。");
        string key = GetKey(profile.Id);
        ValidateKey(key);
        if (profile.RequiresApiKey && key.Length == 0) throw new ArgumentException("请先填写这个服务的 API Key。");
    }

    private static void ValidateDraft(ProviderConfiguration profile)
    {
        if (profile.Name.Length == 0 || profile.Name.Length > 80) throw new ArgumentException("服务名称应为 1 到 80 个字符。");
        if (profile.BaseUrl.Length > 2048) throw new ArgumentException("API 地址过长。");
        if (profile.BaseUrl.Length > 0)
        {
            try { TranslationService.BuildEndpoint(profile); }
            catch (ArgumentException) { throw new ArgumentException("“" + profile.Name + "” 的 API 地址无效。远程服务使用 HTTPS，本机服务可使用 HTTP。"); }
            if (!profile.RequiresApiKey && Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) && !uri.IsLoopback)
                throw new ArgumentException("“" + profile.Name + "” 是本机服务，请使用 localhost 或 127.0.0.1。");
        }
        if (profile.SelectedModel.Length > 200 || profile.SelectedModel.Any(char.IsControl)) throw new ArgumentException("模型名称应不超过 200 个字符，且不包含换行。");
        if (profile.Models.Count > 1000 || profile.Models.Any(x => x.Length > 200 || x.Any(char.IsControl))) throw new ArgumentException("模型列表最多 1000 项，每项不超过 200 个字符。");
    }

    private static void ValidateKey(string key)
    {
        if (Encoding.UTF8.GetByteCount(key) > 16 * 1024 || key.Contains('\r') || key.Contains('\n')) throw new ArgumentException("API Key 过长或包含换行，请重新填写。");
    }
    private void SetStatus(string text, bool error = false, bool persistent = false)
    {
        _statusTimer.Stop();
        ProviderStatusLabel.Text = text;
        ProviderStatusLabel.ToolTip = text;
        ProviderStatusLabel.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
        ProviderStatusLabel.Visibility = Visibility.Visible;
        if (!error && !persistent) _statusTimer.Start();
    }
    private void ClearStatus()
    {
        _statusTimer.Stop();
        ProviderStatusLabel.Text = "";
        ProviderStatusLabel.ToolTip = null;
        ProviderStatusLabel.Visibility = Visibility.Collapsed;
    }
    private void NotifyConfigurationChanged()
    {
        if (!_loadingEditor && !_renderingList && !_disposed) ConfigurationChanged?.Invoke();
    }
    private void RememberAcceptedEndpoints()
    {
        _acceptedEndpoints.Clear();
        foreach (var provider in _providers) _acceptedEndpoints[provider.Id] = provider.BaseUrl;
    }
    private static T Themed<T>(T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
    private Brush Accent(string? color)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color ?? "#667988")); }
        catch { return Brush("MutedBrush"); }
    }
    private static string Host(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority : "尚未填写 API 地址";
    private static List<string> ParseModels(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
    private static ProviderConfiguration Clone(ProviderConfiguration provider) => JsonSerializer.Deserialize<ProviderConfiguration>(JsonSerializer.Serialize(provider))!;
    private sealed record PresetEntry(string Id, string Name, string Description, ImageSource? Icon, Brush Accent);
    private void Control_Unloaded(object sender, RoutedEventArgs e) { CancelOperation(); ClearStatus(); }
    public void Dispose()
    {
        if (_disposed) return;
        CancelOperation();
        ClearStatus();
        _disposed = true;
        _connection.Dispose();
        _loadingEditor = true;
        ProviderApiKeyBox.Clear();
        _editing = null;
        _originalKeys.Clear();
        _pendingKeys.Clear();
    }
}
