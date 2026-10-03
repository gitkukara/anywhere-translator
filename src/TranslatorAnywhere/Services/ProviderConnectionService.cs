using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TranslatorAnywhere.Models;

namespace TranslatorAnywhere.Services;

/// <summary>Runs only when the user explicitly tests a connection or requests models.</summary>
public sealed class ProviderConnectionService : IDisposable
{
    private const int MaximumResponseCharacters = 1024 * 1024;
    private const int MaximumModels = 1000;
    private const int MaximumPages = 5;
    private readonly HttpClient client;
    private readonly TranslationService translator;
    public static TimeSpan RequestBudget { get; } = TimeSpan.FromSeconds(30);

    public ProviderConnectionService() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { }

    public ProviderConnectionService(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        translator = new TranslationService(client);
    }

    public static Uri BuildModelsEndpoint(ProviderConfiguration provider)
    {
        Uri chat = TranslationService.BuildEndpoint(provider);
        string path = chat.AbsolutePath;
        string resource = provider.Protocol == ProviderProtocol.AnthropicMessages ? "/messages" : "/chat/completions";
        path = path[..^resource.Length] + "/models";
        return new UriBuilder(chat) { Path = path }.Uri;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfiguration provider, string key, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Uri endpoint = BuildModelsEndpoint(provider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(RequestBudget);
        try
        {
            var models = new Dictionary<string, long>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            for (int page = 0; page < MaximumPages; page++)
            {
                budget.Token.ThrowIfCancellationRequested();
                Uri pageEndpoint = provider.Protocol == ProviderProtocol.AnthropicMessages
                    ? AddPagination(endpoint, cursor) : endpoint;
                using var request = new HttpRequestMessage(HttpMethod.Get, pageEndpoint);
                TranslationService.AddAuthentication(request, provider, key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    DiagnosticLog.Write("Model listing returned HTTP " + (int)response.StatusCode + ".");
                    throw new InvalidOperationException(TranslationService.StatusMessage(response.StatusCode));
                }
                using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
                string data = await ReadBoundedAsync(stream, budget.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(data);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)
                    || !root.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("服务商未返回兼容的模型列表，请手动填写模型名称。");
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var identifier)
                        || identifier.ValueKind != JsonValueKind.String) continue;
                    string id = (identifier.GetString() ?? "").Trim();
                    if (id.Length == 0 || id.Length > 200 || id.Any(char.IsControl)) continue;
                    long created = ReadModelCreated(item);
                    if (!models.TryGetValue(id, out long previous) || created > previous) models[id] = created;
                    if (models.Count > MaximumModels)
                        throw new InvalidOperationException("服务商模型数量超过获取上限，请手动填写需要的模型名称。");
                }
                bool hasMore = provider.Protocol == ProviderProtocol.AnthropicMessages
                    && root.TryGetProperty("has_more", out var hasMoreElement) && hasMoreElement.ValueKind == JsonValueKind.True;
                if (!hasMore)
                {
                    if (models.Count == 0)
                        throw new InvalidOperationException("服务商没有返回可用模型，请检查权限或手动填写模型名称。");
                    return models.OrderByDescending(entry => entry.Value)
                        .ThenByDescending(entry => entry.Key, Comparer<string>.Create(CompareModelNames))
                        .Select(entry => entry.Key).ToArray();
                }
                if (!root.TryGetProperty("last_id", out var lastId) || lastId.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("服务商模型分页格式异常，请手动填写模型名称。");
                cursor = lastId.GetString();
                if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 200 || cursor.Any(char.IsControl) || !cursors.Add(cursor))
                    throw new InvalidOperationException("服务商模型分页格式异常，请手动填写模型名称。");
            }
            throw new InvalidOperationException("模型列表超过获取页数上限，请手动填写需要的模型名称。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
        {
            DiagnosticLog.Write("Model listing exceeded its time budget.");
            throw new TimeoutException("获取模型超过 30 秒，请检查网络或稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            DiagnosticLog.Write("Model listing transport failed.", ex);
            throw new InvalidOperationException("无法获取模型，请检查网络、代理及 API 地址，或手动填写模型名称。", ex);
        }
        catch (IOException ex)
        {
            DiagnosticLog.Write("Model listing response stream failed.", ex);
            throw new InvalidOperationException("获取模型时连接中断，请重试或手动填写模型名称。", ex);
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Write("Model listing returned invalid JSON.", ex);
            throw new InvalidOperationException("服务商模型列表无法解析，请手动填写模型名称。", ex);
        }
    }

    public async Task<string> TestConnectionAsync(ProviderConfiguration provider, string key, string targetLanguage, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(provider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(RequestBudget);
        try
        {
            // A tiny synthetic input validates credentials, protocol and the explicitly chosen model.
            // Disabled drafts can be tested without changing their enabled state.
            await translator.ProbeAsync(provider, key, targetLanguage, budget.Token).ConfigureAwait(false);
            return "连接成功，所选模型已返回译文。";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
        {
            DiagnosticLog.Write("Connection test exceeded its time budget.");
            throw new TimeoutException("测试连接超过 30 秒，请检查网络或稍后重试。");
        }
    }

    private static long ReadModelCreated(JsonElement item)
    {
        if (item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
            && created.TryGetInt64(out long timestamp) && timestamp > 0 && timestamp <= 253402300799L) return timestamp;
        if (item.TryGetProperty("created_at", out var date) && date.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed) && parsed.ToUnixTimeSeconds() > 0) return parsed.ToUnixTimeSeconds();
        return 0;
    }

    // No release date: use descending natural name order (model-10 before model-9).
    private static int CompareModelNames(string? left, string? right)
    {
        string[] a = Regex.Split(left ?? "", "([0-9]+)"), b = Regex.Split(right ?? "", "([0-9]+)");
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            int comparison;
            if (i % 2 == 1)
            {
                string x = a[i].TrimStart('0'), y = b[i].TrimStart('0');
                comparison = x.Length.CompareTo(y.Length);
                if (comparison == 0) comparison = string.CompareOrdinal(x, y);
            }
            else comparison = StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]);
            if (comparison != 0) return comparison;
        }
        int length = a.Length.CompareTo(b.Length);
        return length != 0 ? length : StringComparer.Ordinal.Compare(left, right);
    }

    private static Uri AddPagination(Uri endpoint, string? cursor)
    {
        // Replace managed pagination fields while preserving other explicit query parameters.
        string[] fields = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var query = new List<string>();
        foreach (string field in fields)
        {
            string name = Uri.UnescapeDataString(field.Split('=', 2)[0]);
            if (!string.Equals(name, "limit", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "after_id", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "before_id", StringComparison.OrdinalIgnoreCase)) query.Add(field);
        }
        query.Add("limit=100");
        if (cursor is not null) query.Add("after_id=" + Uri.EscapeDataString(cursor));
        return new UriBuilder(endpoint) { Query = string.Join('&', query) }.Uri;
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var data = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (data.Length + read > MaximumResponseCharacters)
                throw new InvalidOperationException("模型列表响应过大，请手动填写需要的模型名称。");
            data.Append(buffer, 0, read);
        }
        return data.ToString();
    }

    public void Dispose()
    {
        translator.Dispose();
        client.Dispose();
    }
}
