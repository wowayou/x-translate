using System.Net;
using System.Text;
using System.Text.Json;
using XTranslate.Core;

/// <summary>Anthropic Messages and Gemini generateContent contracts. Mock HTTP only; fake keys.</summary>
internal static class AiProtocolTests
{
    private const string Key = "fake-native-key";

    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Native AI endpoints resolve per protocol and reject mismatched routes", EndpointRules),
        ("Anthropic request uses x-api-key, version header and Messages body", AnthropicRequest),
        ("Anthropic non-streamed stop reasons gate success", AnthropicStopReasons),
        ("Anthropic stream emits only text deltas and requires message_stop", AnthropicStream),
        ("Anthropic stream truncation, refusal and error events fail safely", AnthropicStreamFailures),
        ("Gemini request keeps the key out of the URL and skips thought parts", GeminiRequest),
        ("Gemini safety, prompt block and truncation are not translations", GeminiFailures),
        ("Gemini stream requires STOP and rejects text after completion", GeminiStream),
        ("Native AI HTTP errors are classified once without leaking bodies", NativeErrors),
        ("Native AI streaming requires an event stream without fallback", NativeStreamingContentType),
        ("Service profiles apply to requests and templates carry no credentials", Profiles)
    };

    private static Task EndpointRules()
    {
        foreach (var (input, expected) in new[]
        {
            ("https://api.anthropic.com", "https://api.anthropic.com/v1/messages"),
            ("https://api.anthropic.com/v1", "https://api.anthropic.com/v1/messages"),
            ("https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/messages"),
            ("https://api.deepseek.com/anthropic", "https://api.deepseek.com/anthropic/v1/messages")
        }) Check(AiEndpoint.Resolve(AiProtocol.AnthropicMessages, input, "m", false).AbsoluteUri == expected, "Anthropic route: " + input);
        foreach (var (input, streaming, expected) in new[]
        {
            ("https://generativelanguage.googleapis.com", false, "https://generativelanguage.googleapis.com/v1beta/models/gemini-x:generateContent"),
            ("https://generativelanguage.googleapis.com/v1", false, "https://generativelanguage.googleapis.com/v1/models/gemini-x:generateContent"),
            ("https://relay.test/gemini/", true, "https://relay.test/gemini/v1beta/models/gemini-x:streamGenerateContent?alt=sse")
        }) Check(AiEndpoint.Resolve(AiProtocol.GeminiGenerateContent, input, "models/gemini-x", streaming).AbsoluteUri == expected, "Gemini route: " + input);
        foreach (var (protocol, url) in new[]
        {
            (AiProtocol.AnthropicMessages, "https://relay.test/v1/chat/completions"),
            (AiProtocol.GeminiGenerateContent, "https://relay.test/v1/messages"),
            (AiProtocol.OpenAiChat, "https://api.anthropic.com/v1")
        }) ExpectSynchronous(() => AiEndpoint.Resolve(protocol, url, "m", false), TranslationErrorCode.UnsupportedProtocol);
        ExpectSynchronous(() => AiEndpoint.Resolve(AiProtocol.GeminiGenerateContent,
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-x:generateContent", "gemini-x", false), TranslationErrorCode.InvalidEndpoint);
        foreach (var model in new[] { "", "../admin", "gemini x", "a/b", "m?key=1", "m:generateContent" })
            ExpectSynchronous(() => AiEndpoint.Resolve(AiProtocol.GeminiGenerateContent, "https://relay.test", model, false), TranslationErrorCode.InvalidModel);
        ExpectSynchronous(() => AiEndpoint.Resolve(AiProtocol.AnthropicMessages, "https://relay.test/v1?key=1", "m", false), TranslationErrorCode.InvalidEndpoint);
        return Task.CompletedTask;
    }

    private static async Task AnthropicRequest()
    {
        using var fixture = new Fixture(async request =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://api.anthropic.com/v1/messages", "Wrong Anthropic URL");
            Check(request.Headers.GetValues("x-api-key").Single() == Key && request.Headers.GetValues("anthropic-version").Single() == "2023-06-01" &&
                request.Headers.Authorization is null, "Anthropic auth headers are wrong");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Check(root.GetProperty("model").GetString() == "claude-test" && root.GetProperty("max_tokens").GetInt32() > 0 &&
                root.GetProperty("system").GetString()!.Contains("es") && !root.GetProperty("stream").GetBoolean() &&
                root.GetProperty("messages")[0].GetProperty("content").GetString() == "source text", "Anthropic body is wrong");
            Check(!root.GetProperty("system").GetString()!.Contains("source text"), "Source text leaked into the system prompt");
            return Json("{\"model\":\"claude-served\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"hidden\"},{\"type\":\"text\",\"text\":\"hola\"}]}");
        });
        var result = await fixture.Service.TranslateAsync("source text", Settings(AiProtocol.AnthropicMessages, "https://api.anthropic.com"), CancellationToken.None);
        Check(result.Text == "hola" && result.Model == "claude-served" && result.ProviderName == "Anthropic · api.anthropic.com" && fixture.Calls == 1,
            "Anthropic result, provenance or request count is wrong");
    }

    private static async Task AnthropicStopReasons()
    {
        foreach (var (body, code) in new[]
        {
            ("{\"stop_reason\":\"max_tokens\",\"content\":[{\"type\":\"text\",\"text\":\"partial\"}]}", TranslationErrorCode.IncompleteResponse),
            ("{\"stop_reason\":\"refusal\",\"content\":[{\"type\":\"text\",\"text\":\"no\"}]}", TranslationErrorCode.IncompleteResponse),
            ("{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\"}]}", TranslationErrorCode.IncompleteResponse),
            ("{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"x\"}]}", TranslationErrorCode.EmptyResponse),
            ("{\"stop_reason\":\"end_turn\",\"content\":\"text\"}", TranslationErrorCode.InvalidResponse)
        })
        {
            using var fixture = new Fixture(_ => Task.FromResult(Json(body)));
            await Expect(fixture.Service.TranslateAsync("hello", Settings(AiProtocol.AnthropicMessages, "https://api.anthropic.com"), CancellationToken.None), code);
        }
        using var stopSequence = new Fixture(_ => Task.FromResult(Json("{\"stop_reason\":\"stop_sequence\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}")));
        Check((await stopSequence.Service.TranslateAsync("hello", Settings(AiProtocol.AnthropicMessages, "https://api.anthropic.com"), CancellationToken.None)).Text == "ok",
            "stop_sequence should be a complete response");
    }

    private static async Task AnthropicStream()
    {
        var stream = string.Concat(
            Event("message_start", "{\"type\":\"message_start\",\"message\":{\"model\":\"claude-served\",\"content\":[],\"stop_reason\":null}}"),
            Event("content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}"),
            Event("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"secret plan\"}}"),
            Event("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}"),
            Event("ping", "{\"type\":\"ping\"}"),
            Event("content_block_start", "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}"),
            Event("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Ho\"}}"),
            Event("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"la\"}}"),
            Event("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":1}"),
            Event("future_event", "{\"type\":\"future_event\"}"),
            Event("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}"),
            Event("message_stop", "{\"type\":\"message_stop\"}"));
        var deltas = new List<string>();
        using var fixture = new Fixture(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("stream").GetBoolean() && request.Headers.Accept.ToString().Contains("text/event-stream"), "Stream not requested");
            return EventStream(stream);
        });
        var settings = Settings(AiProtocol.AnthropicMessages, "https://api.anthropic.com", streaming: true);
        var result = await fixture.Service.TranslateAsync("hello", settings, CancellationToken.None, new Progress(update => deltas.Add(update.TextDelta)));
        Check(result.Text == "Hola" && result.Model == "claude-served" && string.Join("|", deltas) == "Ho|la" && fixture.Calls == 1,
            "Anthropic stream mixed thinking text, lost deltas, or retried");
    }

    private static async Task AnthropicStreamFailures()
    {
        var start = Event("message_start", "{\"type\":\"message_start\",\"message\":{\"model\":\"m\"}}") +
                    Event("content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}") +
                    Event("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}");
        foreach (var (tail, code) in new[]
        {
            ("", TranslationErrorCode.IncompleteResponse),
            (Event("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}") + Event("message_stop", "{\"type\":\"message_stop\"}"), TranslationErrorCode.IncompleteResponse),
            (Event("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"refusal\"}}") + Event("message_stop", "{\"type\":\"message_stop\"}"), TranslationErrorCode.IncompleteResponse),
            (Event("message_stop", "{\"type\":\"message_stop\"}"), TranslationErrorCode.IncompleteResponse),
            (Event("error", "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"secret detail fake-native-key\"}}"), TranslationErrorCode.RequestFailed),
            (Event("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":7,\"delta\":{\"type\":\"text_delta\",\"text\":\"x\"}}"), TranslationErrorCode.InvalidResponse)
        })
        {
            using var fixture = new Fixture(_ => Task.FromResult(EventStream(start + tail)));
            var error = await Expect(fixture.Service.TranslateAsync("hello", Settings(AiProtocol.AnthropicMessages, "https://api.anthropic.com", streaming: true), CancellationToken.None), code);
            Check(!error.ToString().Contains("secret") && !error.ToString().Contains(Key) && fixture.Calls == 1, "Stream failure leaked or retried");
        }
    }

    private static async Task GeminiRequest()
    {
        using var fixture = new Fixture(async request =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://generativelanguage.googleapis.com/v1beta/models/gemini-test:generateContent" &&
                !request.RequestUri.AbsoluteUri.Contains(Key), "Gemini URL is wrong or carries the key");
            Check(request.Headers.GetValues("x-goog-api-key").Single() == Key && request.Headers.Authorization is null, "Gemini auth header is wrong");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Check(root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!.Contains("es") &&
                root.GetProperty("contents")[0].GetProperty("role").GetString() == "user" &&
                root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString() == "source text", "Gemini body is wrong");
            return Json("{\"modelVersion\":\"gemini-served\",\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"plan\",\"thought\":true},{\"text\":\"ho\"},{\"thoughtSignature\":\"sig\"},{\"text\":\"la\"}]},\"finishReason\":\"STOP\"}]}");
        });
        var result = await fixture.Service.TranslateAsync("source text", Settings(AiProtocol.GeminiGenerateContent, "https://generativelanguage.googleapis.com", model: "gemini-test"), CancellationToken.None);
        Check(result.Text == "hola" && result.Model == "gemini-served" && result.ProviderName == "Gemini · generativelanguage.googleapis.com" && fixture.Calls == 1,
            "Gemini result, provenance or request count is wrong");
    }

    private static async Task GeminiFailures()
    {
        foreach (var (body, code) in new[]
        {
            ("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"x\"}]},\"finishReason\":\"SAFETY\"}]}", TranslationErrorCode.IncompleteResponse),
            ("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"part\"}]},\"finishReason\":\"MAX_TOKENS\"}]}", TranslationErrorCode.IncompleteResponse),
            ("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}", TranslationErrorCode.IncompleteResponse),
            ("{\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"f\"}}]},\"finishReason\":\"STOP\"}]}", TranslationErrorCode.IncompleteResponse),
            ("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"t\",\"thought\":true}]},\"finishReason\":\"STOP\"}]}", TranslationErrorCode.EmptyResponse),
            ("{\"candidates\":[]}", TranslationErrorCode.InvalidResponse),
            ("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"a\"}]},\"finishReason\":\"STOP\"},{\"content\":{\"parts\":[{\"text\":\"b\"}]},\"finishReason\":\"STOP\"}]}", TranslationErrorCode.InvalidResponse)
        })
        {
            using var fixture = new Fixture(_ => Task.FromResult(Json(body)));
            await Expect(fixture.Service.TranslateAsync("hello", Settings(AiProtocol.GeminiGenerateContent, "https://generativelanguage.googleapis.com"), CancellationToken.None), code);
        }
        using var invalidModel = new Fixture(_ => Task.FromResult(Json("{}")));
        await Expect(invalidModel.Service.TranslateAsync("hello", Settings(AiProtocol.GeminiGenerateContent, "https://relay.test", model: "../x"), CancellationToken.None),
            TranslationErrorCode.InvalidModel);
        Check(invalidModel.Calls == 0, "Invalid model touched HTTP");
    }

    private static async Task GeminiStream()
    {
        static string Chunk(string body) => "data: " + body + "\r\n\r\n";
        var ok = Chunk("{\"modelVersion\":\"g\",\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Ho\"}]}}]}") +
                 Chunk("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"think\",\"thought\":true},{\"text\":\"la\"}]},\"finishReason\":\"STOP\"}]}") +
                 Chunk("{\"usageMetadata\":{\"totalTokenCount\":3}}");
        var deltas = new List<string>();
        using (var fixture = new Fixture(request =>
        {
            Check(request.RequestUri!.Query == "?alt=sse" && request.RequestUri.AbsolutePath.EndsWith(":streamGenerateContent"), "Gemini stream route is wrong");
            return Task.FromResult(EventStream(ok));
        }))
        {
            var result = await fixture.Service.TranslateAsync("hello", Settings(AiProtocol.GeminiGenerateContent, "https://generativelanguage.googleapis.com", streaming: true),
                CancellationToken.None, new Progress(update => deltas.Add(update.TextDelta)));
            Check(result.Text == "Hola" && string.Join("|", deltas) == "Ho|la" && fixture.Calls == 1, "Gemini stream result or deltas are wrong");
        }
        foreach (var (body, code) in new[]
        {
            (Chunk("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}"), TranslationErrorCode.IncompleteResponse),
            (Chunk("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"p\"}]},\"finishReason\":\"MAX_TOKENS\"}]}"), TranslationErrorCode.IncompleteResponse),
            (ok + Chunk("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"late\"}]}}]}"), TranslationErrorCode.InvalidResponse),
            (Chunk("{\"error\":{\"code\":500,\"message\":\"secret fake-native-key\"}}"), TranslationErrorCode.RequestFailed),
            ("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"x\"}]},\"finishReason\":\"STOP\"}]}", TranslationErrorCode.IncompleteResponse)
        })
        {
            using var fixture = new Fixture(_ => Task.FromResult(EventStream(body)));
            var error = await Expect(fixture.Service.TranslateAsync("hello", Settings(AiProtocol.GeminiGenerateContent, "https://generativelanguage.googleapis.com", streaming: true), CancellationToken.None), code);
            Check(!error.ToString().Contains("secret") && fixture.Calls == 1, "Gemini stream failure leaked or retried");
        }
    }

    private static async Task NativeErrors()
    {
        foreach (var protocol in new[] { AiProtocol.AnthropicMessages, AiProtocol.GeminiGenerateContent })
        foreach (var (status, code) in new[] { (401, TranslationErrorCode.Unauthorized), (400, TranslationErrorCode.BadRequest),
            (404, TranslationErrorCode.NotFound), (429, TranslationErrorCode.RateLimited), (529, TranslationErrorCode.ServerUnavailable) })
        {
            using var fixture = new Fixture(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret body " + Key) }));
            var error = await Expect(fixture.Service.TranslateAsync("private source", Settings(protocol, "https://relay.test"), CancellationToken.None), code);
            Check(error.StatusCode == status && !error.ToString().Contains("secret") && !error.ToString().Contains(Key) &&
                !error.ToString().Contains("private source") && fixture.Calls == 1, "Native error leaked content or retried");
        }
        using var missing = new Fixture(_ => Task.FromResult(Json("{}")));
        var noKey = Settings(AiProtocol.AnthropicMessages, "https://relay.test");
        noKey.OpenAiApiKey = "";
        await Expect(missing.Service.TranslateAsync("hello", noKey, CancellationToken.None), TranslationErrorCode.MissingApiKey);
        Check(missing.Calls == 0, "Missing key touched HTTP");
    }

    private static async Task NativeStreamingContentType()
    {
        foreach (var protocol in new[] { AiProtocol.AnthropicMessages, AiProtocol.GeminiGenerateContent })
        {
            using var fixture = new Fixture(_ => Task.FromResult(Json("{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}]}")));
            await Expect(fixture.Service.TranslateAsync("hello", Settings(protocol, "https://relay.test", streaming: true), CancellationToken.None),
                TranslationErrorCode.StreamingNotSupported);
            Check(fixture.Calls == 1, "Unsupported streaming retried or downgraded");
        }
    }

    private static Task Profiles()
    {
        foreach (var template in AiServiceTemplates.All.Where(template => template.Endpoint.Length > 0))
        {
            var profile = AiServiceTemplates.Create(template, template.Name);
            Check(profile.ApiKey.Length == 0 && profile.Id.Length > 0, "Template carried a credential or lacked an id");
            AiEndpoint.Resolve(profile.Protocol, profile.Endpoint, profile.Model, true); // Every default must be a valid route.
        }
        Check(AiServiceTemplates.All.Select(template => template.Id).Distinct().Count() == AiServiceTemplates.All.Count, "Template ids repeat");
        var service = new AiServiceProfile { Name = "Claude", Protocol = AiProtocol.AnthropicMessages, Endpoint = "https://api.anthropic.com", Model = "m", Streaming = true, ApiKey = Key };
        var copy = service.Copy();
        copy.ApiKey = "changed";
        var settings = new TranslationSettings();
        service.ApplyTo(settings);
        Check(service.ApiKey == Key && settings.AiProtocol == AiProtocol.AnthropicMessages && settings.OpenAiApiKey == Key &&
            settings.OpenAiEndpoint == service.Endpoint && settings.OpenAiStreaming, "Profile copy/apply is wrong");
        Check(!JsonSerializer.Serialize(service).Contains(Key), "Profile JSON contains the plaintext key");
        return Task.CompletedTask;
    }

    private static TranslationSettings Settings(AiProtocol protocol, string endpoint, bool streaming = false, string model = "claude-test") => new()
    {
        Provider = ProviderKind.OpenAI, AiProtocol = protocol, OpenAiEndpoint = endpoint, OpenAiApiKey = Key,
        OpenAiModel = protocol == AiProtocol.GeminiGenerateContent && model == "claude-test" ? "gemini-test" : model,
        OpenAiStreaming = streaming, TargetLanguage = "es"
    };

    private static string Event(string name, string data) => $"event: {name}\ndata: {data}\n\n";
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage EventStream(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
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
        catch (TranslationException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}"); return; }
        throw new Exception("Expected " + code);
    }

    private sealed class Progress(Action<TranslationProgress> report) : IProgress<TranslationProgress>
    {
        public void Report(TranslationProgress value) => report(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        public TranslationService Service { get; }
        public int Calls { get; private set; }
        public Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            _client = new HttpClient(new Handler(request => { Calls++; return respond(request); }));
            Service = new TranslationService(_client);
        }
        public void Dispose() { Service.Dispose(); _client.Dispose(); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
}
