using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TranslatorAnywhere.Models;
using TranslatorAnywhere.Services;

internal static class Program
{
    private static int assertions;

    [STAThread]
    private static void Main()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "service-check-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TRANSLATOR_ANYWHERE_DATA_DIR", directory);
        try
        {
            CheckSettingsAndIcons(directory);
            assertions += ThemePersistenceChecks.Run(directory);
            assertions += ButtonAppearanceChecks.Run(directory);
            CheckPlacement();
            assertions += StartupRegistrationChecks.Run();
            assertions += ProviderPersistenceChecks.Run(directory);
            CheckTranslationAsync().GetAwaiter().GetResult();
            assertions += ProviderProtocolChecks.RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS: " + assertions + " service assertions (no external API requests).");
        }
        finally
        {
            string fullDirectory = Path.GetFullPath(directory);
            string allowedRoot = Path.GetFullPath(AppContext.BaseDirectory);
            if (fullDirectory.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullDirectory).StartsWith("service-check-", StringComparison.Ordinal)
                && Directory.Exists(fullDirectory))
                Directory.Delete(fullDirectory, recursive: true);
        }
    }

    private static void CheckSettingsAndIcons(string directory)
    {
        var store = new SettingsStore();
        Equal(directory, store.DataDirectory, "Data directory override");
        Equal("DeepSeek", store.Load().ProviderName, "Missing settings use defaults");
        var settings = new AppSettings { ButtonText = "翻译", OffsetX = -18, ProviderName = "Custom", BaseUrl = "http://localhost:11434/v1" };
        store.Save(settings);
        Equal("翻译", store.Load().ButtonText, "JSON Unicode roundtrip");
        Equal(-18d, store.Load().OffsetX, "Numeric settings roundtrip");
        const string key = "sk-fixture-key-never-use";
        store.SaveApiKey(key);
        Equal(key, store.ReadApiKey(), "DPAPI roundtrip");
        Check(!File.ReadAllText(Path.Combine(directory, "settings.json")).Contains(key, StringComparison.Ordinal), "Settings contain no key");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "api-key.dpapi"))).Contains(key, StringComparison.Ordinal), "Key file contains no plaintext");
        store.SaveApiKey("");
        Equal("", store.ReadApiKey(), "Empty key clears credential");
        File.WriteAllText(Path.Combine(directory, "settings.json"), "invalid fixture JSON");
        Equal("DeepSeek", store.Load().ProviderName, "Damaged settings recover defaults");
        Equal(1, Directory.GetFiles(directory, "settings.json.invalid-*").Length, "Damaged settings preserved");
        File.WriteAllText(Path.Combine(directory, "settings.json"), """
            {"ProviderName":"Custom","BaseUrl":null,"Model":null,"TargetLanguage":null,
             "ButtonText":null,"ButtonColor":"invalid","IconPath":null,"ButtonMode":88,"Anchor":-1,
             "ButtonSize":1e999,"OffsetX":1e999,"OffsetY":-9999,"SelectionDelayMs":-20,"AutoHideSeconds":9999,
             "ClipboardFallbackApplications":[null,"", " notepad.exe ","NOTEPAD.exe"],"ExcludedApplications":null}
            """);
        var normalized = store.Load();
        Equal("", normalized.BaseUrl, "Null custom endpoint never reroutes its credential");
        Equal("", normalized.Model, "Null model normalized safely");
        Equal(new AppSettings().TargetLanguage, normalized.TargetLanguage, "Null language uses the supported default");
        Equal("翻", normalized.ButtonText, "Null button label uses default");
        Equal(new AppSettings().ButtonColor, normalized.ButtonColor, "Invalid color uses default");
        Equal(ButtonVisualMode.Text, normalized.ButtonMode, "Invalid visual enum uses default");
        Equal(ButtonAnchor.SelectionBottomRight, normalized.Anchor, "Invalid anchor enum uses default");
        Equal(new AppSettings().ButtonSize, normalized.ButtonSize, "Non-finite size uses default");
        Equal(8d, normalized.OffsetX, "Non-finite offset uses default");
        Equal(-500d, normalized.OffsetY, "Finite offsets are clamped");
        Equal(50, normalized.SelectionDelayMs, "Selection delay minimum");
        Equal(120, normalized.AutoHideSeconds, "Hide timeout maximum");
        Equal(1, normalized.ClipboardFallbackApplications.Count, "Application lists remove nulls and duplicates");
        Equal(0, normalized.ExcludedApplications.Count, "Null application list normalized");

        string imagePath = Path.Combine(directory, "fixture.png");
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(imagePath)) encoder.Save(stream);
        string importedPath = store.ImportIcon(imagePath);
        Check(File.Exists(importedPath) && importedPath.StartsWith(Path.Combine(directory, "icons"), StringComparison.Ordinal), "Icon imported into portable data");
        File.WriteAllText(imagePath, "invalid fixture bitmap");
        Throws<ArgumentException>(() => store.ImportIcon(imagePath), "Invalid icon rejected");
        Throws<ArgumentException>(() => store.ImportIcon("fixture.svg"), "Unsupported icon extension rejected");
        var oversizedBitmap = BitmapSource.Create(2049, 1, 96, 96, PixelFormats.Bgra32, null, new byte[2049 * 4], 2049 * 4);
        var oversizedEncoder = new PngBitmapEncoder();
        oversizedEncoder.Frames.Add(BitmapFrame.Create(oversizedBitmap));
        using (var stream = File.Create(imagePath)) oversizedEncoder.Save(stream);
        Throws<ArgumentException>(() => store.ImportIcon(imagePath), "Oversized icon dimensions rejected before pixel copy");
        using (var stream = File.Create(imagePath)) stream.SetLength(5 * 1024 * 1024 + 1);
        Throws<ArgumentException>(() => store.ImportIcon(imagePath), "Oversized icon file rejected");
        File.WriteAllBytes(Path.Combine(directory, "api-key.dpapi"), new byte[] { 1, 2, 3, 4 });
        Throws<InvalidOperationException>(() => store.ReadApiKey(), "Damaged encrypted credential is recoverable");
        DiagnosticLog.Write("Request to https://example.test/v1?token=fixture failed; Bearer fixture-secret", new Exception("fixture-selected-text"));
        string log = File.ReadAllText(Path.Combine(directory, "app.log"));
        Check(!log.Contains("fixture-secret", StringComparison.Ordinal) && !log.Contains("fixture-selected-text", StringComparison.Ordinal)
            && !log.Contains("token=fixture", StringComparison.Ordinal), "Logs redact URLs, credentials and exception messages");
    }

    private static void CheckPlacement()
    {
        var selection = new SelectionSnapshot("fixture", "fixture", IntPtr.Zero,
            new Rect(300, 250, 200, 40), new Point(600, 400), "fixture", DateTimeOffset.UtcNow);
        var workArea = new Rect(0, 0, 1920, 1080);
        var settings = new AppSettings { ButtonSize = 36 };
        settings.Anchor = ButtonAnchor.SelectionBottomRight;
        Equal(new Point(500, 290), ButtonPlacement.Calculate(selection, settings, 1, workArea), "Bottom-right anchor");
        settings.Anchor = ButtonAnchor.SelectionTopRight;
        Equal(new Point(500, 214), ButtonPlacement.Calculate(selection, settings, 1, workArea), "Top-right anchor");
        settings.Anchor = ButtonAnchor.SelectionTopLeft;
        Equal(new Point(264, 214), ButtonPlacement.Calculate(selection, settings, 1, workArea), "Top-left anchor");
        settings.Anchor = ButtonAnchor.SelectionBottomLeft;
        Equal(new Point(264, 290), ButtonPlacement.Calculate(selection, settings, 1, workArea), "Bottom-left anchor");
        settings.Anchor = ButtonAnchor.Cursor;
        Equal(new Point(600, 400), ButtonPlacement.Calculate(selection, settings, 1, workArea), "Cursor anchor");
        settings.Anchor = ButtonAnchor.SelectionTopRight;
        Equal(new Point(500, 196), ButtonPlacement.Calculate(selection, settings, 1.5, workArea), "150 percent DPI physical selection bounds");
        Equal(new Point(500, 178), ButtonPlacement.Calculate(selection, settings, 2, workArea), "200 percent DPI physical selection bounds");
        settings.OffsetX = -30;
        settings.OffsetY = 20;
        Equal(new Point(443, 214), ButtonPlacement.Calculate(selection, settings, 1.5, workArea), "Offsets scale with DPI");
        settings = new AppSettings { ButtonSize = 36 };
        var leftSelection = selection with { Bounds = new Rect(-1000, 300, 100, 30) };
        Equal(new Point(-900, 330), ButtonPlacement.Calculate(leftSelection, settings, 1, new Rect(-1920, 0, 1920, 1080)), "Negative monitor coordinates");
        var edgeSelection = selection with { Bounds = new Rect(1910, 1070, 5, 5) };
        Equal(new Point(1868, 1028), ButtonPlacement.Calculate(edgeSelection, settings, 1, workArea), "Right and bottom screen clamp");
        settings.Anchor = ButtonAnchor.SelectionTopLeft;
        var topLeftSelection = selection with { Bounds = new Rect(-1915, 5, 5, 5) };
        Equal(new Point(-1920, 0), ButtonPlacement.Calculate(topLeftSelection, settings, 1, new Rect(-1920, 0, 1920, 1080)), "Left and top screen clamp");
        settings.Anchor = ButtonAnchor.SelectionBottomRight;
        Equal(new Point(601, 401), ButtonPlacement.Calculate(selection with { Bounds = Rect.Empty }, settings, 1, workArea), "Missing bounds fall back to cursor");
    }

    private static async Task CheckTranslationAsync()
    {
        Equal("https://example.test/v1/chat/completions", TranslationService.BuildEndpoint("https://example.test").AbsoluteUri, "Root endpoint");
        Equal("https://example.test/v1/chat/completions", TranslationService.BuildEndpoint("https://example.test/v1/").AbsoluteUri, "Version suffix not duplicated");
        Equal("https://example.test/v1/chat/completions", TranslationService.BuildEndpoint("https://example.test/v1/chat/completions/").AbsoluteUri, "Complete endpoint not duplicated");
        Equal("http://127.0.0.1:1234/v1/chat/completions", TranslationService.BuildEndpoint("http://127.0.0.1:1234/v1").AbsoluteUri, "Loopback HTTP accepted");
        Throws<ArgumentException>(() => TranslationService.BuildEndpoint("http://example.test"), "Remote HTTP rejected");
        Throws<ArgumentException>(() => TranslationService.BuildEndpoint("https://user:pass@example.test"), "URL credentials rejected");
        Throws<ArgumentException>(() => TranslationService.BuildEndpoint("https://example.test#fragment"), "URL fragments rejected");

        var settings = new AppSettings { BaseUrl = "https://example.test/v1", Model = "fixture-model" };
        const string sse = ": keepalive\r\n  \r\ndata: {\"choices\": [\r\ndata: {\"delta\": {\"content\": \"你好\"}}]}\r\n\r\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hidden fixture\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n";
        string? requestBody = null;
        using (var service = new TranslationService(new FakeHandler(async (request, token) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(token);
            Equal("Bearer", request.Headers.Authorization?.Scheme, "Bearer authorization");
            Equal("https://example.test/v1/chat/completions", request.RequestUri?.AbsoluteUri, "Request endpoint");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TinyChunkStream(Encoding.UTF8.GetBytes(sse))) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        })))
        {
            var result = new StringBuilder();
            await service.TranslateAsync(settings, "fixture-secret", "fixture input", delta => result.Append(delta), CancellationToken.None);
            Equal("你好，世界", result.ToString(), "Chunked UTF-8, multi-line SSE and reasoning isolation");
            using var body = JsonDocument.Parse(requestBody!);
            Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString(), "DeepSeek thinking switch");
        }

        settings.ProviderName = "Custom";
        using (var service = new TranslationService(new FakeHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(!body.RootElement.TryGetProperty("thinking", out _), "Custom provider receives no DeepSeek-only field");
            return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"完整译文\"},\"finish_reason\":\"stop\"}]}");
        })))
        {
            var result = new StringBuilder();
            await service.TranslateAsync(settings, "fixture-secret", "fixture input", delta => result.Append(delta), CancellationToken.None);
            Equal("完整译文", result.ToString(), "Non-streaming fallback");
        }

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
        {
            using var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("fixture-secret https://example.test private fixture input") })));
            var ex = await ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(settings, "fixture-secret", "fixture input", _ => { }, CancellationToken.None));
            Check(!ex.Message.Contains("fixture-secret", StringComparison.Ordinal) && !ex.Message.Contains("example.test", StringComparison.Ordinal)
                && !ex.Message.Contains("fixture input", StringComparison.Ordinal), "Safe HTTP " + (int)status + " errors");
        }

        using (var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(JsonResponse("{\"choices\":[{\"message\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}")))))
        {
            var ex = await ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(settings, "fixture-secret", "fixture input", _ => { }, CancellationToken.None));
            Check(ex.Message.Contains("截断", StringComparison.Ordinal), "Truncation is surfaced");
        }
        using (var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(JsonResponse("null")))))
        {
            var ex = await ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(settings, "fixture-secret", "fixture input", _ => { }, CancellationToken.None));
            Check(ex.Message.Contains("格式", StringComparison.Ordinal), "Malformed shape returns friendly error");
        }
        const string doneSse = "data: {\"choices\":[{\"delta\":{\"content\":\"完成\"}}]}\r\rdata: [DONE]\r\r";
        using (var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(SseResponse(doneSse)))))
        {
            var result = new StringBuilder();
            await service.TranslateAsync(settings, "fixture-secret", "fixture input", delta => result.Append(delta), CancellationToken.None);
            Equal("完成", result.ToString(), "CR-only lines and DONE without finish_reason");
        }
        const string earlyEofSse = "data: {\"choices\":[{\"delta\":{\"content\":\"不完整\"}}]}\n\n";
        using (var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(SseResponse(earlyEofSse)))))
        {
            var ex = await ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(settings, "fixture-secret", "fixture input", _ => { }, CancellationToken.None));
            Check(ex.Message.Contains("不完整", StringComparison.Ordinal), "Early SSE EOF is never reported as complete");
        }
        using (var service = new TranslationService(new FakeHandler((_, _) => Task.FromResult(SseResponse(doneSse)))))
        {
            using var cancellation = new CancellationTokenSource();
            await ThrowsAsync<OperationCanceledException>(() => service.TranslateAsync(settings, "fixture-secret", "fixture input", _ => cancellation.Cancel(), cancellation.Token));
            assertions++;
        }
        using (var service = new TranslationService(new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return JsonResponse("{}");
        })))
        {
            using var cancellation = new CancellationTokenSource(30);
            await ThrowsAsync<OperationCanceledException>(() => service.TranslateAsync(settings, "fixture-secret", "fixture input", _ => { }, cancellation.Token));
            assertions++;
        }
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage SseResponse(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

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
        catch (T ex) { return ex; }
        throw new Exception("FAIL: Expected " + typeof(T).Name);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => response(request, cancellationToken);
    }

    private sealed class TinyChunkStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }
}
