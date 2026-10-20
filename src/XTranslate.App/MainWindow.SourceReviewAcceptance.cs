using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    // Real WPF controls with fixed mock data only; never reads the clipboard or calls a live service.
    private async Task RunSourceReviewAcceptanceAsync(List<string> checks, string output)
    {
        var originalService = _service;
        var originalSettings = _settings.Copy();
        var originalWidth = Width;
        var originalHeight = Height;
        var originalTheme = UiAppearance.Preference;
        using var handler = new ReviewSmokeHandler();
        using var client = new HttpClient(handler);
        _service = new TranslationService(client);
        var tasks = new List<Task>();
        const string source = "  He go to school every day.\n";
        try
        {
            ReviewSourceBox.IsChecked = false;
            SelectProvider(ProviderKind.GoogleWeb);
            _settings.AutoTranslateSelection = true;
            SourceBox.Text = source;
            var requests = handler.Requests;
            RequireStream(ShouldAutoTranslateSelection && SourceReviewSummary.Text == UiLocalization.Get("SourceNotChecked"), "Ordinary mode lost unchecked status");
            ReviewSourceBox.IsChecked = true;
            RequireStream(handler.Requests == requests && !ShouldAutoTranslateSelection && !TranslateButton.IsEnabled &&
                ReviewRequested && SourceBox.Text == source && ResultBox.Text == "", "Opt-in sent HTTP, changed source or allowed automatic review");
            await TranslateNowAsync();
            RequireStream(handler.Requests == requests && _statusKey == "ErrorUnsupportedSourceReview" && _sourceReview is null,
                "Unsupported provider sent HTTP or implied successful review");
            checks.Add("Learning review is session-only opt-in, suppresses automatic selection sending and blocks non-AI review with zero HTTP");

            ConfigureSmokeAiService("https://review.example.test", "review-smoke-not-real", "review-mock-model", true);
            SelectProvider(ProviderKind.OpenAI);
            SourceBox.Text = new string(' ', 3000) + "x";
            RequireStream(!TranslateButton.IsEnabled, "Review ignored original whitespace when checking input limits");
            await TranslateNowAsync();
            RequireStream(handler.Requests == requests && _statusKey == "StatusTooLong", "Oversized untrimmed review reached HTTP");
            SourceBox.Text = "   \n";
            RequireStream(!TranslateButton.IsEnabled, "Whitespace-only review enabled translation");
            SourceBox.Text = source;
            _settings.AutoFallback = true;
            _settings.NormalizeFallbackOrder();
            RequireStream(BuildCandidates().Count == 1 && BuildCandidates()[0].Settings.ReviewSource &&
                !BuildCandidates()[0].Settings.SourceReviewInEnglish, "Learning expanded recipients or lost locale");
            handler.Enqueue(ReviewFixture("issues"));
            await TranslateNowAsync();
            RequireStream(handler.Requests == requests + 1 && _sourceReview?.Status == SourceReviewStatus.Issues &&
                SourceBox.Text == source && ResultBox.Text == "他每天去上学。" && CopyButton.IsEnabled &&
                ReviewDetails.IsExpanded && SourceReviewText.Text.Contains("He goes") &&
                _streamingPreview is null && !_isStreamingPreview && _settings.Translation.OpenAiStreaming,
                "Review mutated input, leaked JSON, streamed, or lost correction details");
            RequireStream(SourceReviewText.Text.Contains("<script>literal fixture</script>") &&
                ProvenanceLabel.Text.Contains("review.example.test") && RequestHint.Text.Contains("review.example.test"),
                "Review did not preserve plain-text content or recipient provenance");
            checks.Add("One-request learning UI preserves exact source and shows correction, interpretation, literal text and translation separately");

            var review = _sourceReview;
            var generation = _generation;
            requests = handler.Requests;
            UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
            RequireStream(_sourceReview == review && SourceBox.Text == source && ResultBox.Text == "他每天去上学。" &&
                SourceReviewSummary.Text == UiLocalization.Get("SourceReviewIssues") && handler.Requests == requests && _generation == generation,
                "Review relocalization retranslated or changed source/result");
            Width = MinWidth; Height = MinHeight;
            foreach (var language in new[] { UiLanguage.English, UiLanguage.SimplifiedChinese })
            {
                UiLanguageBox.SelectedIndex = (int)language;
                UiAppearance.Apply(language == UiLanguage.English ? UiTheme.Dark : UiTheme.Light);
                UpdateLayout();
                RequireStream(WindowViewport.ScrollableHeight > 0 && SourceReviewText.ActualHeight > 0 && ResultBox.ActualHeight > 30,
                    "Learning details clipped the translation at minimum size");
                RenderElementTo(MainLayout, Path.ChangeExtension(output, language == UiLanguage.English ? ".source-review.en.dark.png" : ".source-review.zh.light.png"));
            }
            UiAppearance.Apply(originalTheme); Width = originalWidth; Height = originalHeight;
            checks.Add("Source review labels relocalize without HTTP; English/dark and Chinese/light minimum-width scrollable layouts render");

            handler.Enqueue(ReviewFixture("uncertain"));
            await TranslateNowAsync();
            RequireStream(_sourceReview?.Status == SourceReviewStatus.Uncertain && ResultBox.Text == "" && !CopyButton.IsEnabled &&
                _statusKey == "StatusReviewClarification" && ResultPlaceholder.Text == UiLocalization.Get("SourceReviewUncertainPlaceholder"),
                "Uncertain meaning became a definitive/copyable translation");
            var status = _statusKey;
            Copy_Click(this, new RoutedEventArgs()); // Guard returns before touching clipboard.
            RequireStream(_statusKey == status, "Uncertain-result copy bypassed guard");
            checks.Add("Ambiguous source exposes clarification and disables definitive translation and whole-result copy");

            handler.Enqueue(ReviewFixture("no_obvious_issues"));
            await TranslateNowAsync();
            RequireStream(_sourceReview?.Status == SourceReviewStatus.NoObviousIssues &&
                SourceReviewSummary.Text == UiLocalization.Get("SourceReviewNoObviousIssues"), "No-issues result falsely certified correctness");
            requests = handler.Requests;
            handler.Enqueue("not structured private_review_marker");
            await TranslateNowAsync();
            RequireStream(handler.Requests == requests + 1 && _sourceReview is null && ResultBox.Text == "" &&
                !CopyButton.IsEnabled && _statusKey == "ErrorInvalidSourceReview" &&
                !SourceReviewText.Text.Contains("private_review_marker") && !StatusLabel.Text.Contains("private_review_marker"),
                "Malformed review fell back, leaked output or retained old assessment");
            checks.Add("Clean-source disclaimer and invalid-response failure are distinct; invalid review never falls back or keeps old output");

            foreach (var action in new[] { "cancel", "edit", "mode", "provider", "target", "hide", "settings" })
            {
                ShowForUser(false);
                SelectProvider(ProviderKind.OpenAI);
                ReviewSourceBox.IsChecked = true;
                SourceBox.Text = source;
                var release = handler.EnqueueDelayed();
                requests = handler.Requests;
                var running = TranslateNowAsync(); tasks.Add(running);
                await WaitForStreamingUiAsync(() => handler.Requests == requests + 1);
                RequireStream(_sourceReview is null && ResultBox.Text == "" && !CopyButton.IsEnabled && _streamingPreview is null,
                    "Pending review exposed raw data");
                switch (action)
                {
                    case "cancel": Cancel_Click(this, new RoutedEventArgs()); break;
                    case "edit": SourceBox.Text = "edited source"; break;
                    case "mode": ReviewSourceBox.IsChecked = false; break;
                    case "provider": SelectProvider(ProviderKind.GoogleWeb); break;
                    case "target": LanguageBox.SelectedIndex = LanguageBox.SelectedIndex == 0 ? 1 : 0; break;
                    case "hide": HideToTray(); break;
                    case "settings": await RunSettingsDialogAsync(CancelStreamingSettingsDialog); break;
                }
                release.TrySetResult(ReviewFixture("issues")); // Deliberately ignore HTTP cancellation.
                await running.WaitAsync(TimeSpan.FromSeconds(3));
                RequireStream(_sourceReview is null && ResultBox.Text == "" && !CopyButton.IsEnabled &&
                    handler.Requests == requests + 1 && _pending is null &&
                    SourceReviewSummary.Text != UiLocalization.Get("SourceReviewWaiting"), action + " admitted late review or left pending state");
            }
            checks.Add("Review cancel/edit/mode/provider/target/hide/settings invalidate late responses without retry or stale assessment");

            ShowForUser(false); SelectProvider(ProviderKind.OpenAI); ReviewSourceBox.IsChecked = true; SourceBox.Text = source;
            var oldResponse = handler.EnqueueDelayed();
            requests = handler.Requests;
            var oldTask = TranslateNowAsync(); tasks.Add(oldTask);
            await WaitForStreamingUiAsync(() => handler.Requests == requests + 1);
            var pending = _pending; generation = _generation;
            UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
            RequireStream(_pending == pending && _generation == generation && handler.Requests == requests + 1 &&
                SourceReviewSummary.Text == UiLocalization.Get("SourceReviewWaiting"), "In-flight language switch restarted review");
            SourceBox.Text = source + " ";
            handler.Enqueue(ReviewFixture("no_obvious_issues"));
            await TranslateNowAsync();
            var newestReview = _sourceReview;
            oldResponse.TrySetResult(ReviewFixture("issues"));
            await oldTask.WaitAsync(TimeSpan.FromSeconds(3));
            RequireStream(_sourceReview == newestReview && _sourceReview?.Status == SourceReviewStatus.NoObviousIssues &&
                handler.Requests == requests + 2 && CopyButton.IsEnabled && SourceBox.Text == source + " ",
                "Old completion replaced the latest review");
            var log = File.ReadAllText(Path.Combine(_storage.DirectoryPath, "diagnostics.jsonl"));
            var saved = File.ReadAllText(Path.Combine(_storage.DirectoryPath, "settings.json"));
            RequireStream(!log.Contains("He go") && !log.Contains("private_review_marker") && !log.Contains("review-smoke-not-real") &&
                !saved.Contains("ReviewSource") && !saved.Contains("He go"), "Review contents or opt-in leaked to persistence");
            checks.Add("In-flight relocalization preserves one request; latest review wins; source, assessment and session opt-in are not persisted");
        }
        finally
        {
            CancelRequest(); handler.ReleaseAll();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
            ReviewSourceBox.IsChecked = false;
            _service.Dispose(); _service = originalService;
            RestoreSmokeSettings(originalSettings);
            UiAppearance.Apply(originalTheme); Width = originalWidth; Height = originalHeight;
            UiLocalization.SetPreference(_settings.UiLanguage); Localize();
            ShowForUser(false); SourceBox.Clear();
        }
    }

    private static string ReviewFixture(string status)
    {
        var uncertain = status == "uncertain";
        var clean = status == "no_obvious_issues";
        return JsonSerializer.Serialize(new
        {
            version = 1, status,
            issues = clean ? Array.Empty<object>() : new object[] { new
            {
                kind = uncertain ? "ambiguity" : "grammar", quote = "He go",
                explanation = uncertain ? "Please clarify the intended context." : "第三人称单数用 goes。<script>literal fixture</script>",
                suggestion = uncertain ? "" : "He goes"
            } },
            revisedSource = uncertain || clean ? "" : "  He goes to school every day.\n",
            translationBasis = uncertain ? "uncertain" : clean ? "original" : "minimal_correction",
            interpretation = clean ? "" : uncertain ? "Which reading did you intend?" : "按最小修正理解，不改变句意。",
            translation = uncertain ? "" : "他每天去上学。"
        });
    }

    private sealed class ReviewSmokeHandler : HttpMessageHandler
    {
        private readonly Queue<Task<string>> _responses = new();
        private readonly List<TaskCompletionSource<string>> _delayed = new();
        public int Requests { get; private set; }
        public void Enqueue(string text) => _responses.Enqueue(Task.FromResult(text));
        public TaskCompletionSource<string> EnqueueDelayed()
        {
            var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _delayed.Add(source); _responses.Enqueue(source.Task); return source;
        }
        public void ReleaseAll() { foreach (var source in _delayed) source.TrySetResult("{}"); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            RequireStream(!body.RootElement.GetProperty("stream").GetBoolean() && request.RequestUri!.Host == "review.example.test",
                "Learning unexpectedly streamed or changed recipient");
            var text = await _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "served-review-model", choices = new[] { new { finish_reason = "stop", message = new { content = text } } }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
