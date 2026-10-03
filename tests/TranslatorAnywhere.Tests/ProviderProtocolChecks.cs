using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

internal static class ProviderProtocolChecks
{
    private const string Key = "fixture-private-key-never-use";
    private const string Input = "fixture selected input";
    private static int assertions;

    public static async Task<int> RunAsync()
    {
        assertions = 0;
        CheckEndpoints();
        await CheckOpenAiAsync();
        await CheckAnthropicAsync();
        await CheckModelsAsync();
        await CheckConnectionTestsAsync();
        return assertions;
    }

    private static ProviderConfiguration Profile(string baseUrl = "https://example.test/v1", ProviderProtocol protocol = ProviderProtocol.OpenAICompatible) => new()
    {
        BaseUrl = baseUrl,
        Protocol = protocol,
        SelectedModel = "fixture-model",
        Models = new() { "fixture-model" },
        PresetId = "custom",
        Name = "Fixture",
        Enabled = true,
        RequiresApiKey = true
    };

    private static void CheckEndpoints()
    {
        Equal("https://example.test/v1/chat/completions", TranslationService.BuildEndpoint(Profile("https://example.test/")).AbsoluteUri, "Provider origin defaults to v1");
        Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            TranslationService.BuildEndpoint(Profile("https://generativelanguage.googleapis.com/v1beta/openai/")).AbsoluteUri, "Gemini official directory preserved");
        Equal("https://example.test/compatible-mode/v1/chat/completions",
            TranslationService.BuildEndpoint(Profile("https://example.test/compatible-mode/v1")).AbsoluteUri, "Vendor version directory preserved");
        Equal("https://example.test/custom/chat/completions",
            TranslationService.BuildEndpoint(Profile("https://example.test/custom")).AbsoluteUri, "Custom directory preserved");
        Equal("https://example.test/custom/chat/completions",
            TranslationService.BuildEndpoint(Profile("https://example.test/custom/chat/completions/")).AbsoluteUri, "Complete chat endpoint preserved");
        Equal("https://api.anthropic.com/v1/messages",
            TranslationService.BuildEndpoint(Profile("https://api.anthropic.com", ProviderProtocol.AnthropicMessages)).AbsoluteUri, "Anthropic origin endpoint");
        Equal("https://example.test/proxy/v1/messages",
            TranslationService.BuildEndpoint(Profile("https://example.test/proxy/v1", ProviderProtocol.AnthropicMessages)).AbsoluteUri, "Anthropic relay directory preserved");
        Equal("https://example.test/proxy/v1/models",
            ProviderConnectionService.BuildModelsEndpoint(Profile("https://example.test/proxy/v1/messages", ProviderProtocol.AnthropicMessages)).AbsoluteUri, "Models derived from complete native endpoint");
        Equal("https://generativelanguage.googleapis.com/v1beta/openai/models",
            ProviderConnectionService.BuildModelsEndpoint(Profile("https://generativelanguage.googleapis.com/v1beta/openai/")).AbsoluteUri, "Gemini models directory preserved");
        Throws<ArgumentException>(() => TranslationService.BuildEndpoint(Profile("http://example.test/v1")), "Remote HTTP profile rejected");
        Throws<ArgumentException>(() => TranslationService.BuildEndpoint(Profile("https://user:password@example.test/v1")), "URL credentials rejected");
        Throws<ArgumentException>(() => TranslationService.BuildEndpoint(Profile("https://example.test/v1#fragment")), "URL fragment rejected");
        Throws<ArgumentException>(() => TranslationService.BuildEndpoint(Profile("https://example.test/v1?api_key=secret")), "Provider URL query rejected");
        Equal("https://example.test/v1/chat/completions?fixture=value", TranslationService.BuildEndpoint("https://example.test/v1?fixture=value").AbsoluteUri, "Legacy endpoint query behavior preserved");
        var invalid = Profile();
        invalid.Protocol = (ProviderProtocol)99;
        Throws<ArgumentException>(() => TranslationService.BuildEndpoint(invalid), "Unknown protocol rejected");
    }

    private static async Task CheckOpenAiAsync()
    {
        var provider = Profile();
        provider.PresetId = "deepseek";
        provider.Name = "Renamed DeepSeek instance";
        using (var service = new TranslationService(new FakeHandler(async (request, token) =>
        {
            Equal("Bearer", request.Headers.Authorization?.Scheme, "OpenAI-compatible bearer authentication");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Equal("disabled", document.RootElement.GetProperty("thinking").GetProperty("type").GetString(), "DeepSeek thinking uses preset identity");
            Equal(Input, document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString(), "Selected text remains user content");
            return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"译文\"},\"finish_reason\":\"stop\"}]}");
        })))
        {
            var result = new StringBuilder();
            await service.TranslateAsync(provider, Key, Input, "简体中文", delta => result.Append(delta), CancellationToken.None);
            Equal("译文", result.ToString(), "Explicit profile translation");
        }
        provider.PresetId = "gemini";
        using (var service = new TranslationService(new FakeHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(!document.RootElement.TryGetProperty("thinking", out _), "Other compatible vendors receive no thinking field");
            return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"译文\"}}]}");
        })))
            await service.TranslateAsync(provider, Key, Input, "简体中文", _ => { }, CancellationToken.None);

        provider.PresetId = "openai";
        provider.SelectedModel = "gpt-5-fixture";
        using (var service = new TranslationService(new FakeHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Equal(8192, document.RootElement.GetProperty("max_completion_tokens").GetInt32(), "OpenAI completion token bound");
            Check(!document.RootElement.TryGetProperty("max_tokens", out _), "OpenAI omits obsolete token parameter");
            Equal("developer", document.RootElement.GetProperty("messages")[0].GetProperty("role").GetString(), "Modern OpenAI developer instruction role");
            Check(!document.RootElement.TryGetProperty("thinking", out _), "OpenAI receives no DeepSeek thinking field");
            return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"译文\"}}]}");
        })))
            await service.TranslateAsync(provider, Key, Input, "中文", _ => { }, CancellationToken.None);

        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse("{\"choices\":[{\"message\":{\"content\":\"译文\"}}]}")));
        using (var service = new TranslationService(handler))
        {
            provider.Enabled = false;
            await ThrowsAsync<ArgumentException>(() => service.TranslateAsync(provider, Key, Input, "中文", _ => { }, CancellationToken.None));
            Equal(0, handler.Calls, "Disabled provider sends no translation request");
            provider.Enabled = true;
            await ThrowsAsync<ArgumentException>(() => service.TranslateAsync(provider, "bad\nkey", Input, "中文", _ => { }, CancellationToken.None));
            await ThrowsAsync<ArgumentException>(() => service.TranslateAsync(provider, "bad-key-密钥", Input, "中文", _ => { }, CancellationToken.None));
            await ThrowsAsync<ArgumentException>(() => service.TranslateAsync(provider, "", Input, "中文", _ => { }, CancellationToken.None));
            Equal(0, handler.Calls, "Invalid credentials rejected before transport");
            provider.RequiresApiKey = false;
            await ThrowsAsync<ArgumentException>(() => service.TranslateAsync(provider, Key, Input, "中文", _ => { }, CancellationToken.None));
            Equal(0, handler.Calls, "Remote keyless configuration rejected");
        }

        var local = Profile("http://127.0.0.1:1234/v1");
        local.RequiresApiKey = false;
        using (var service = new TranslationService(new FakeHandler((request, _) =>
        {
            Check(request.Headers.Authorization is null, "Keyless local request has no invented credential");
            return Task.FromResult(JsonResponse("{\"choices\":[{\"message\":{\"content\":\"本地译文\"}}]}"));
        })))
            await service.TranslateAsync(local, "", Input, "中文", _ => { }, CancellationToken.None);
    }

    private static async Task CheckAnthropicAsync()
    {
        var provider = Profile("https://api.anthropic.com/v1", ProviderProtocol.AnthropicMessages);
        const string events = ": keepalive\r\n \r\n"
            + "event: message_start\r\ndata: {\"type\":\"message_start\",\"message\":{\"content\":[]}}\r\n\r\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"hidden\"}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hidden reasoning\"}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"hidden signature\"}}\n\n"
            + "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"你好\"}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\n"
            + "data: \"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"，世界\"}}\n\n"
            + "data: {\"type\":\"ping\"}\n\n"
            + "data: {\"type\":\"future_event\",\"text\":\"ignored\"}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n"
            + "data: {\"type\":\"message_stop\"}\n\n";
        using (var service = new TranslationService(new FakeHandler(async (request, token) =>
        {
            Equal("https://api.anthropic.com/v1/messages", request.RequestUri?.AbsoluteUri, "Native Messages endpoint");
            Check(request.Headers.Authorization is null, "Native Messages sends no bearer header");
            Equal(Key, request.Headers.GetValues("x-api-key").Single(), "Native API key header");
            Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single(), "Native version header");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(document.RootElement.GetProperty("system").GetString()!.Contains("中文", StringComparison.Ordinal), "Native top-level system translation prompt");
            Equal(1, document.RootElement.GetProperty("messages").GetArrayLength(), "Native messages exclude system role");
            Check(!document.RootElement.TryGetProperty("thinking", out _), "Native request omits thinking configuration");
            return SseResponse(events, tinyChunks: true);
        })))
        {
            var result = new StringBuilder();
            await service.TranslateAsync(provider, Key, Input, "中文", delta => result.Append(delta), CancellationToken.None);
            Equal("你好，世界", result.ToString(), "Native UTF-8 SSE emits only text blocks");
        }

        using (var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            "{\"type\":\"message\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"private\",\"text\":\"must not leak\"},"
            + "{\"type\":\"text\",\"text\":\"完整译文\"}],\"stop_reason\":\"end_turn\"}")))))
        {
            var result = new StringBuilder();
            await service.TranslateAsync(provider, Key, Input, "中文", delta => result.Append(delta), CancellationToken.None);
            Equal("完整译文", result.ToString(), "Native non-streaming fallback filters thinking blocks");
        }

        foreach (string failure in new[]
        {
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n",
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}\n\n",
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"error\",\"error\":{\"message\":\"" + Key + " " + Input + " https://private.test\"}}\n\n"
        })
        {
            using var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(SseResponse(failure))));
            var ex = await ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(provider, Key, Input, "中文", _ => { }, CancellationToken.None));
            SafeError(ex, "Native EOF, truncation and SSE errors are safe failures");
        }

        using (var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            "{\"content\":[{\"type\":\"text\",\"text\":\"partial\"}],\"stop_reason\":\"max_tokens\"}")))))
        {
            var ex = await ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(provider, Key, Input, "中文", _ => { }, CancellationToken.None));
            Check(ex.Message.Contains("截断", StringComparison.Ordinal), "Native JSON truncation surfaced");
        }
        using (var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(SseResponse(events)))))
        {
            using var cancellation = new CancellationTokenSource();
            await ThrowsAsync<OperationCanceledException>(() => service.TranslateAsync(provider, Key, Input, "中文", _ => cancellation.Cancel(), cancellation.Token));
        }
    }

    private static async Task CheckModelsAsync()
    {
        var provider = Profile("https://example.test/path/chat/completions");
        var handler = new FakeHandler((request, _) =>
        {
            Equal("https://example.test/path/models", request.RequestUri?.AbsoluteUri, "Explicit model listing URL");
            Equal(Key, request.Headers.Authorization?.Parameter, "Models bearer key");
            return Task.FromResult(JsonResponse("{\"data\":[{\"id\":\"z-model\"},{\"id\":\"a-model\"},{\"id\":\"z-model\"},{\"id\":\"\"},{\"id\":12}]}"));
        });
        using (var service = new ProviderConnectionService(handler))
        {
            Equal(0, handler.Calls, "Connection service constructor sends no requests");
            var models = await service.ListModelsAsync(provider, Key, CancellationToken.None);
            Equal("z-model,a-model", string.Join(',', models), "Model discovery deduplicates valid IDs and sorts");
        }

        const string datedModels = """
            {"data":[{"id":"z-old","created":100},{"id":"a-new","created":300},
              {"id":"z-old","created":200},{"id":"iso","created_at":"2026-01-01T00:00:00Z"},
              {"id":"model-9"},{"id":"model-10"},{"id":"model-2","created":"bad"},
              {"id":"model-1","created":-1}]}
            """;
        using (var service = new ProviderConnectionService(new FakeHandler((_, _) => Task.FromResult(JsonResponse(datedModels)))))
        {
            var models = await service.ListModelsAsync(provider, Key, CancellationToken.None);
            Equal("iso,a-new,z-old,model-10,model-9,model-2,model-1", string.Join(',', models),
                "Discovery sorts dated models newest first and undated models by descending natural version, deduplicating IDs");
        }

        var anthropic = Profile("https://api.anthropic.com/v1", ProviderProtocol.AnthropicMessages);
        int pages = 0;
        using (var service = new ProviderConnectionService(new FakeHandler((request, _) =>
        {
            pages++;
            Check(request.Headers.Authorization is null && request.Headers.Contains("x-api-key"), "Native discovery uses native authentication");
            Check(request.RequestUri!.Query.Contains("limit=100", StringComparison.Ordinal), "Native discovery uses bounded page size");
            if (pages == 1)
                return Task.FromResult(JsonResponse("{\"data\":[{\"id\":\"model/a+b\"}],\"has_more\":true,\"last_id\":\"model/a+b\"}"));
            Check(request.RequestUri.Query.Contains("after_id=model%2Fa%2Bb", StringComparison.Ordinal), "Native pagination cursor encoded");
            return Task.FromResult(JsonResponse("{\"data\":[{\"id\":\"model-z\"}],\"has_more\":false,\"last_id\":\"model-z\"}"));
        })))
        {
            var models = await service.ListModelsAsync(anthropic, Key, CancellationToken.None);
            Equal(2, pages, "Native discovery requests all available pages");
            Equal(2, models.Count, "Native discovery combines pages");
        }

        pages = 0;
        using (var service = new ProviderConnectionService(new FakeHandler((_, _) =>
        {
            pages++;
            return Task.FromResult(JsonResponse("{\"data\":[{\"id\":\"model\"}],\"has_more\":true,\"last_id\":\"same-cursor\"}"));
        })))
        {
            await ThrowsAsync<InvalidOperationException>(() => service.ListModelsAsync(anthropic, Key, CancellationToken.None));
            Equal(2, pages, "Repeated native cursor stops pagination");
        }

        pages = 0;
        using (var service = new ProviderConnectionService(new FakeHandler((_, _) =>
        {
            pages++;
            return Task.FromResult(JsonResponse("{\"data\":[{\"id\":\"model\"}],\"has_more\":true,\"last_id\":\"cursor-" + pages + "\"}"));
        })))
        {
            await ThrowsAsync<InvalidOperationException>(() => service.ListModelsAsync(anthropic, Key, CancellationToken.None));
            Equal(5, pages, "Native discovery has a page budget");
        }

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
        {
            using var service = new ProviderConnectionService(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(Key + " " + Input + " https://private.test") })));
            var ex = await ThrowsAsync<InvalidOperationException>(() => service.ListModelsAsync(provider, Key, CancellationToken.None));
            SafeError(ex, "Safe model discovery HTTP " + (int)status);
        }

        foreach (string body in new[] { "invalid " + Key, "{\"data\":[]}", "{\"data\":{}}", "{\"error\":\"" + Key + "\"}", new string(' ', 1024 * 1024 + 1) })
        {
            using var service = new ProviderConnectionService(new FakeHandler((_, _) => Task.FromResult(JsonResponse(body))));
            var ex = await ThrowsAsync<InvalidOperationException>(() => service.ListModelsAsync(provider, Key, CancellationToken.None));
            SafeError(ex, "Invalid or oversized model responses fail safely");
        }

        using (var service = new ProviderConnectionService(new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return JsonResponse("{}");
        })))
        {
            using var cancellation = new CancellationTokenSource(20);
            await ThrowsAsync<OperationCanceledException>(() => service.ListModelsAsync(provider, Key, cancellation.Token));
        }
    }

    private static async Task CheckConnectionTestsAsync()
    {
        var provider = Profile();
        provider.Enabled = false;
        using (var service = new ProviderConnectionService(new FakeHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Equal("Hello.", document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString(), "Connection test sends only synthetic text");
            Equal(128, document.RootElement.GetProperty("max_tokens").GetInt32(), "Connection test output budget is small");
            return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"你好。\"},\"finish_reason\":\"stop\"}]}");
        })))
        {
            string result = await service.TestConnectionAsync(provider, Key, "中文", CancellationToken.None);
            Check(result.Contains("成功", StringComparison.Ordinal), "Explicit test accepts disabled draft");
            Check(!result.Contains("你好", StringComparison.Ordinal) && !result.Contains(Key, StringComparison.Ordinal), "Test result contains safe summary only");
            Check(!provider.Enabled, "Testing never changes provider enabled state");
        }
        using (var service = new ProviderConnectionService(new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return JsonResponse("{}");
        })))
        {
            using var cancellation = new CancellationTokenSource(20);
            await ThrowsAsync<OperationCanceledException>(() => service.TestConnectionAsync(provider, Key, "中文", cancellation.Token));
        }

        provider.PresetId = "openai";
        provider.SelectedModel = "o3-fixture";
        using (var service = new ProviderConnectionService(new FakeHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Equal(1024, document.RootElement.GetProperty("max_completion_tokens").GetInt32(), "OpenAI probe leaves bounded reasoning headroom");
            Check(!document.RootElement.TryGetProperty("max_tokens", out _), "OpenAI probe omits max_tokens");
            return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"你好。\"},\"finish_reason\":\"stop\"}]}");
        })))
            await service.TestConnectionAsync(provider, Key, "中文", CancellationToken.None);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage SseResponse(string body, bool tinyChunks = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = tinyChunks ? new StreamContent(new TinyChunkStream(Encoding.UTF8.GetBytes(body))) : new StringContent(body, Encoding.UTF8)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static void SafeError(Exception ex, string name) => Check(!ex.Message.Contains(Key, StringComparison.Ordinal)
        && !ex.Message.Contains(Input, StringComparison.Ordinal) && !ex.Message.Contains("private.test", StringComparison.Ordinal), name);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        assertions++;
    }

    private static void Equal<T>(T expected, T actual, string name) => Check(Equals(expected, actual), name);

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new Exception("FAIL: " + name);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { assertions++; return ex; }
        throw new Exception("FAIL: Expected " + typeof(T).Name);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return response(request, cancellationToken);
        }
    }

    private sealed class TinyChunkStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}
