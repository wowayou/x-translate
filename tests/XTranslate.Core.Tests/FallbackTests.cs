using System.Net;
using System.Text;
using XTranslate.Core;

/// <summary>Opt-in ordered fallback. Mock HTTP; every response is chosen by request host.</summary>
internal static class FallbackTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Fallback with one candidate keeps single-service errors unchanged", SingleCandidate),
        ("Fallback tries services strictly in order and reports each failure", OrderedFallback),
        ("Fallback skips unconfigured services without HTTP or counting them", SkipsWithoutHttp),
        ("Fallback sends at most three requests and keeps the first error", RequestLimit),
        ("Request problems and cancellation never switch services", NoSwitchForRequestProblems),
        ("Fallback gives each attempt its own progress and dedupes candidates", ProgressAndDedupe)
    };

    private static async Task SingleCandidate()
    {
        using var fixture = new Fixture(_ => Status(503));
        var error = await Expect<TranslationException>(TranslationFallback.TranslateAsync(fixture.Service, "hello", new[] { Ai("a", "a.test") }, CancellationToken.None));
        Check(error.Code == TranslationErrorCode.ServerUnavailable && fixture.Hosts.Count == 1, "Single candidate changed behavior or retried");
    }

    private static async Task OrderedFallback()
    {
        using var fixture = new Fixture(host => host switch { "a.test" => Status(503), "b.test" => Status(429), _ => Ok("hola") });
        var failures = new List<string>();
        var result = await TranslationFallback.TranslateAsync(fixture.Service, "hello", new[] { Ai("a", "a.test"), Ai("b", "b.test"), Ai("c", "c.test") },
            CancellationToken.None, onFailed: (candidate, attempt) => failures.Add($"{candidate.Key}:{attempt.Code}:{attempt.StatusCode}:{attempt.Sent}"));
        Check(result.Result.Text == "hola" && result.Candidate.Key == "c" && string.Join(",", fixture.Hosts) == "a.test,b.test,c.test",
            "Fallback did not follow the given order");
        Check(string.Join("|", failures) == "a:ServerUnavailable:503:True|b:RateLimited:429:True" && result.Failed.Count == 2,
            "Failures were not reported in order");
    }

    private static async Task SkipsWithoutHttp()
    {
        using var fixture = new Fixture(host => host == "e.test" ? Ok("ok") : Status(500));
        var noKey = Ai("nokey", "x.test");
        noKey.Settings.OpenAiApiKey = "";
        var deepL = new TranslationCandidate("deepl", "DeepL", new TranslationSettings { Provider = ProviderKind.DeepL, TargetLanguage = "hi", DeepLApiKey = "k" });
        var result = await TranslationFallback.TranslateAsync(fixture.Service, "hello",
            new[] { Ai("a", "a.test"), noKey, deepL, Ai("b", "b.test"), Ai("e", "e.test") }, CancellationToken.None);
        Check(result.Result.Text == "ok" && string.Join(",", fixture.Hosts) == "a.test,b.test,e.test", "Skipped candidates sent HTTP or counted");
        Check(result.Failed.Select(attempt => $"{attempt.Key}:{attempt.Sent}").SequenceEqual(new[] { "a:True", "nokey:False", "deepl:False", "b:True" }) &&
            result.Failed[1].Code == TranslationErrorCode.MissingApiKey && result.Failed[2].Code == TranslationErrorCode.UnsupportedLanguage,
            "Skip reasons are wrong");
    }

    private static async Task RequestLimit()
    {
        using var fixture = new Fixture(host => host == "a.test" ? Status(401) : Status(503));
        var error = await Expect<FallbackFailedException>(TranslationFallback.TranslateAsync(fixture.Service, "hello",
            new[] { Ai("a", "a.test"), Ai("b", "b.test"), Ai("c", "c.test"), Ai("d", "d.test") }, CancellationToken.None));
        Check(fixture.Hosts.Count == TranslationFallback.MaxSentRequests && error.Attempts.Count == 3 &&
            error.Primary.Code == TranslationErrorCode.Unauthorized && !error.ToString().Contains("fake"), "Limit, primary error or privacy is wrong");
    }

    private static async Task NoSwitchForRequestProblems()
    {
        using var fixture = new Fixture(_ => Ok("x"));
        var error = await Expect<TranslationException>(TranslationFallback.TranslateAsync(fixture.Service, new string('a', 3001),
            new[] { Ai("a", "a.test"), Ai("b", "b.test") }, CancellationToken.None));
        Check(error.Code == TranslationErrorCode.InputTooLong && fixture.Hosts.Count == 0, "Oversize input switched services");
        using var cancel = new CancellationTokenSource();
        using var slow = new Fixture(_ => { cancel.Cancel(); return Status(503); });
        await Expect<OperationCanceledException>(TranslationFallback.TranslateAsync(slow.Service, "hello",
            new[] { Ai("a", "a.test"), Ai("b", "b.test") }, cancel.Token));
        Check(slow.Hosts.Count == 1, "Cancellation continued to another service");
    }

    private static async Task ProgressAndDedupe()
    {
        using var fixture = new Fixture(host => host == "a.test" ? Status(500) : Ok("ok"));
        var progressFor = new List<string>();
        var a = Ai("a", "a.test");
        await TranslationFallback.TranslateAsync(fixture.Service, "hello", new[] { a, a, Ai("b", "b.test") }, CancellationToken.None,
            progressFor: candidate => { progressFor.Add(candidate.Key); return null; });
        Check(string.Join(",", progressFor) == "a,b" && string.Join(",", fixture.Hosts) == "a.test,b.test", "Progress or dedupe is wrong");
    }

    private static TranslationCandidate Ai(string key, string host) => new(key, key, new TranslationSettings
    {
        Provider = ProviderKind.OpenAI, OpenAiEndpoint = $"https://{host}/v1/chat/completions", OpenAiApiKey = "fake-key",
        OpenAiModel = "m", TargetLanguage = "es"
    });

    private static HttpResponseMessage Status(int status) => new((HttpStatusCode)status) { Content = new StringContent("fake body") };
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"" + text + "\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
    };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static async Task<T> Expect<T>(Task task) where T : Exception
    {
        try { await task; }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        public TranslationService Service { get; }
        public List<string> Hosts { get; } = new();
        public Fixture(Func<string, HttpResponseMessage> respond)
        {
            _client = new HttpClient(new Handler(request => { Hosts.Add(request.RequestUri!.Host); return respond(request.RequestUri.Host); }));
            Service = new TranslationService(_client);
        }
        public void Dispose() { Service.Dispose(); _client.Dispose(); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
