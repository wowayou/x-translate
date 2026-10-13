using System.Net;
using System.Net.Sockets;
using System.Text;
using XTranslate.Core;

internal static class FoundationTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("AI endpoint resolution is deterministic and preserves custom routes", EndpointRules),
        ("AI endpoint validation rejects credential URLs and other protocols", EndpointSafety),
        ("Base URL requests use the resolved route without mutating settings", BaseUrlRequest),
        ("HTTP failures have actionable safe categories", ErrorCategories),
        ("Owned HTTP transport never follows redirects", RedirectBoundary),
        ("JSON response and final output sizes are bounded", ResponseLimits),
        ("Non-streamed AI truncation and refusal are not successful translations", JsonCompletionBoundary),
        ("Target language catalog includes Spanish and respects service mappings", Languages),
        ("Hotkey parsing rejects reserved and unsafe combinations", Hotkeys),
        ("Hotkey replacement preserves the old registration on conflict", HotkeyReplacement),
        ("Runtime settings snapshots are detached without JSON serialization", Snapshots),
        ("Invalid API key characters fail before HTTP", InvalidKey)
    };

    private static Task EndpointRules()
    {
        foreach (var (input, expected) in new[]
        {
            ("https://api.openai.com", "https://api.openai.com/v1/chat/completions"),
            (" https://proxy.test/ ", "https://proxy.test/v1/chat/completions"),
            ("https://proxy.test/v1/", "https://proxy.test/v1/chat/completions"),
            ("https://proxy.test/prefix/v1", "https://proxy.test/prefix/v1/chat/completions"),
            ("https://api.deepseek.com", "https://api.deepseek.com/chat/completions"),
            ("https://proxy.test/v1/chat/completions", "https://proxy.test/v1/chat/completions"),
            ("https://proxy.test/custom/route", "https://proxy.test/custom/route"),
            ("http://127.0.0.1:1234/v1", "http://127.0.0.1:1234/v1/chat/completions")
        }) Check(AiEndpoint.ResolveChatCompletions(input).AbsoluteUri == expected, "Unexpected resolved endpoint");
        Check(AiEndpoint.SameOrigin("https://EXAMPLE.test/v1", "https://example.test:443/other"), "Equivalent origins changed");
        Check(!AiEndpoint.SameOrigin("https://example.test", "http://example.test") &&
            !AiEndpoint.SameOrigin("https://example.test", "https://example.test:444") &&
            !AiEndpoint.SameOrigin("https://example.test", "https://other.test"), "Credential origin isolation failed");
        return Task.CompletedTask;
    }

    private static Task EndpointSafety()
    {
        foreach (var url in new[] { "", "http://remote.test/v1", "file:///tmp/test", "https://user:secret@proxy.test/v1",
            "https://proxy.test/v1?key=secret", "https://proxy.test/#secret", "https://proxy.test/\r\nheader" })
            ExpectSynchronous(() => AiEndpoint.ResolveChatCompletions(url), TranslationErrorCode.InvalidEndpoint);
        foreach (var url in new[] { "https://api.anthropic.com", "https://proxy.test/v1/messages", "https://proxy.test/v1/responses",
            "https://generativelanguage.googleapis.com", "https://proxy.test/v1beta/models/model:generateContent" })
            ExpectSynchronous(() => AiEndpoint.ResolveChatCompletions(url), TranslationErrorCode.UnsupportedProtocol);
        return Task.CompletedTask;
    }

    private static async Task BaseUrlRequest()
    {
        var settings = AiSettings();
        settings.OpenAiEndpoint = "https://proxy.test";
        using var fixture = new Fixture(request =>
        {
            Check(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "Request still targeted the service homepage");
            return Json("{\"choices\":[{\"message\":{\"content\":\"hola\"}}]}");
        });
        var result = await fixture.Service.TranslateAsync("hello", settings, CancellationToken.None);
        Check(result.Text == "hola" && settings.OpenAiEndpoint == "https://proxy.test" && fixture.Calls == 1, "Settings mutated or request retried");
    }

    private static async Task ErrorCategories()
    {
        foreach (var (status, code) in new[] { (400, TranslationErrorCode.BadRequest), (422, TranslationErrorCode.BadRequest),
            (404, TranslationErrorCode.NotFound), (405, TranslationErrorCode.MethodNotAllowed), (307, TranslationErrorCode.RedirectDenied) })
        {
            using var fixture = new Fixture(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret response and user text") });
            var error = await Expect(fixture.Service.TranslateAsync("private source", AiSettings(), CancellationToken.None), code);
            Check(error.StatusCode == status && !error.ToString().Contains("secret") && !error.ToString().Contains("private source") && fixture.Calls == 1,
                "Error classification leaked content or retried");
        }
    }

    private static async Task RedirectBoundary()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var length = 0;
                while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                if (length > 0) await reader.ReadBlockAsync(new char[length].AsMemory(), deadline.Token);
                var response = $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port}/must-not-replay\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), deadline.Token);
            }, deadline.Token);
            using var service = new TranslationService(); // Exercise the actual owned SocketsHttpHandler.
            var settings = AiSettings();
            settings.OpenAiEndpoint = $"http://127.0.0.1:{port}/v1/chat/completions";
            await Expect(service.TranslateAsync("fixed local test", settings, deadline.Token), TranslationErrorCode.RedirectDenied);
            await serve;
            Check(!listener.Pending(), "A redirect replayed the request body/credential");
        }
        finally { listener.Stop(); }
    }

    private static async Task ResponseLimits()
    {
        using var stream = new UnknownLengthStream(Encoding.UTF8.GetBytes(new string('x', 2_000_000)));
        using var fixture = new Fixture(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        await Expect(fixture.Service.TranslateAsync("hello", AiSettings(), CancellationToken.None), TranslationErrorCode.ResponseTooLarge);
        Check(stream.BytesRead <= 1_048_576 + 8192, "Response limit was checked only after buffering the entire body");
        using var output = new Fixture(_ => Json("{\"choices\":[{\"message\":{\"content\":\"" + new string('x', 50_001) + "\"}}]}"));
        await Expect(output.Service.TranslateAsync("hello", AiSettings(), CancellationToken.None), TranslationErrorCode.ResponseTooLarge);
    }

    private static async Task JsonCompletionBoundary()
    {
        foreach (var body in new[]
        {
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"content_filter\",\"message\":{\"content\":\"partial\"}}]}",
            "{\"choices\":[{\"message\":{\"content\":\"refused\",\"refusal\":\"no\"}}]}"
        })
        {
            using var fixture = new Fixture(_ => Json(body));
            await Expect(fixture.Service.TranslateAsync("hello", AiSettings(), CancellationToken.None), TranslationErrorCode.IncompleteResponse);
        }
    }

    private static async Task Languages()
    {
        Check(TranslationLanguages.All.Count == 33 && TranslationLanguages.All.Select(l => l.Code).Distinct().Count() == 33, "Language IDs/count incorrect");
        Check(TranslationLanguages.ForProvider(ProviderKind.DeepL, "es") == "ES" &&
            TranslationLanguages.ForProvider(ProviderKind.GoogleWeb, "es") == "es" &&
            TranslationLanguages.Find("ar")?.RightToLeft == true, "Language mapping or RTL metadata missing");
        Check(!TranslationLanguages.Supports(ProviderKind.DeepL, "zh-TW"), "Traditional Chinese was silently mapped to simplified Chinese");
        using var fixture = new Fixture(_ => Json("{}"));
        await Expect(fixture.Service.TranslateAsync("hello", new TranslationSettings { TargetLanguage = "not-a-language" }, CancellationToken.None), TranslationErrorCode.UnsupportedLanguage);
        await Expect(fixture.Service.TranslateAsync("hello", new TranslationSettings { Provider = ProviderKind.DeepL, DeepLApiKey = "fake", TargetLanguage = "zh-TW" }, CancellationToken.None), TranslationErrorCode.UnsupportedLanguage);
        Check(fixture.Calls == 0, "Unverified language issued HTTP");
    }

    private static Task Hotkeys()
    {
        Check(HotkeyGesture.TryParse(" alt + ctrl + shift + t ", out var key) && key.Display == "Ctrl+Alt+Shift+T" && key.Key == 0x54 && key.Modifiers == 7, "Hotkey normalization failed");
        Check(HotkeyGesture.TryParse("Ctrl+F11", out var f) && f.Key == 0x7A, "Function key mapping failed");
        Check(HotkeyGesture.TryParse("Alt+0", out _), "Digit shortcut was rejected");
        foreach (var invalid in new[] { "", "Q", "Shift+Q", "Win+Q", "Ctrl+Ctrl+Q", "Ctrl++Q", "Ctrl+F12", "Alt+F13", "Ctrl+Enter", "Ctrl+secret" })
            Check(!HotkeyGesture.TryParse(invalid, out _), "Unsafe shortcut was accepted");
        return Task.CompletedTask;
    }

    private static Task HotkeyReplacement()
    {
        var calls = new List<string>();
        var reject = false;
        using var registration = new HotkeyRegistration(10, 12, (id, modifiers, key) =>
        {
            Check((modifiers & 0x4000) != 0, "MOD_NOREPEAT missing");
            calls.Add("register:" + id);
            return !reject;
        }, id => calls.Add("unregister:" + id));
        HotkeyGesture.TryParse("Ctrl+Q", out var original);
        HotkeyGesture.TryParse("Ctrl+Alt+T", out var replacement);
        Check(registration.TryApply(original) && registration.Id == 10, "Initial hotkey failed");
        Check(registration.TryApply(original) && calls.Count == 1, "Unchanged hotkey was needlessly re-registered");
        reject = true;
        Check(!registration.TryApply(replacement) && registration.Current == original && calls.Count == 2, "Conflict discarded working shortcut");
        reject = false;
        Check(registration.TryApply(replacement) && calls[^2] == "register:12" && calls[^1] == "unregister:10", "Old shortcut was released before replacement succeeded");
        registration.Dispose();
        registration.Dispose();
        Check(calls[^1] == "unregister:12" && calls.Count == 5 && registration.Id is null, "Registration disposal leaked or double released a hotkey");
        return Task.CompletedTask;
    }

    private static Task Snapshots()
    {
        var settings = AiSettings();
        var copy = settings.Copy();
        copy.OpenAiApiKey = "changed";
        copy.TargetLanguage = "de";
        Check(settings.OpenAiApiKey == "fake-test-key" && settings.TargetLanguage == "es", "Snapshot mutated the live configuration");
        return Task.CompletedTask;
    }

    private static async Task InvalidKey()
    {
        using var fixture = new Fixture(_ => Json("{}"));
        var settings = AiSettings();
        settings.OpenAiApiKey = "fake\r\nkey";
        await Expect(fixture.Service.TranslateAsync("hello", settings, CancellationToken.None), TranslationErrorCode.InvalidApiKey);
        Check(fixture.Calls == 0, "Invalid credential touched HTTP");
    }

    private static TranslationSettings AiSettings() => new() { Provider = ProviderKind.OpenAI, OpenAiApiKey = "fake-test-key", OpenAiEndpoint = "https://proxy.test/v1/chat/completions", TargetLanguage = "es" };
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<TranslationException> Expect(Task task, TranslationErrorCode code)
    {
        try { await task; }
        catch (TranslationException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}"); return error; }
        throw new Exception("Expected " + code);
    }
    private static void ExpectSynchronous(Action action, TranslationErrorCode code)
    {
        try { action(); }
        catch (TranslationException error) { Check(error.Code == code, "Wrong endpoint error category"); return; }
        throw new Exception("Expected " + code);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        public TranslationService Service { get; }
        public int Calls { get; private set; }
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _client = new HttpClient(new Handler(request => { Calls++; return respond(request); }));
            Service = new TranslationService(_client);
        }
        public void Dispose() { Service.Dispose(); _client.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = await base.ReadAsync(buffer, token);
            BytesRead += count;
            return count;
        }
    }
}
