using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    // Uses only fixed text, mock HTTPS responses and smoke-data. No clipboard access.
    private async Task RunStreamingAcceptanceAsync(List<string> checks, string output)
    {
        var originalService = _service;
        var originalSettings = _settings.Copy();
        var tasks = new List<Task>();
        using var handler = new StreamingSmokeHandler();
        using var client = new HttpClient(handler);
        _service = new TranslationService(client);
        try
        {
            ShowForUser(false);
            ConfigureSmokeAiService("https://stream.example.test/v1/chat/completions", "stream-smoke-not-a-real-key", "requested-smoke-model", false);
            SelectProvider(ProviderKind.OpenAI);
            await RunSettingsDialogAsync(dialog =>
            {
                var box = (CheckBox)dialog.FindName("OpenAiStreamingBox");
                RequireStream(box.IsChecked == false, "Streaming should start disabled");
                box.IsChecked = true;
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            RequireStream(_settings.Translation.OpenAiStreaming && _storage.Load().Translation.OpenAiStreaming && handler.Requests == 0,
                "Saving streaming did not persist, or issued HTTP");
            await RunSettingsDialogAsync(dialog =>
            {
                var box = (CheckBox)dialog.FindName("OpenAiStreamingBox");
                RequireStream(box.IsChecked == true, "Streaming setting was not restored in the dialog");
                box.IsChecked = false;
                CancelStreamingSettingsDialog(dialog);
            });
            RequireStream(_settings.Translation.OpenAiStreaming && _storage.Load().Translation.OpenAiStreaming && handler.Requests == 0,
                "Canceling the settings dialog changed the streaming preference or issued HTTP");
            checks.Add("AI streaming real settings save/reopen/cancel persists the opt-in without HTTP");

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await RunSettingsDialogAsync(dialog =>
            {
                foreach (var language in new[] { UiLanguage.SimplifiedChinese, UiLanguage.English })
                {
                    ((ComboBox)dialog.FindName("UiLanguageBox")).SelectedIndex = (int)language;
                    dialog.UpdateLayout();
                    dialog.Reveal((FrameworkElement)dialog.FindName("OpenAiStreamingBox"));
                    dialog.UpdateLayout();
                    RenderWindowTo(dialog, Path.ChangeExtension(output, language == UiLanguage.English ? ".ai-settings.en.png" : ".ai-settings.zh.png"));
                }
                CancelStreamingSettingsDialog(dialog);
            });

            var stream = handler.Enqueue(StreamChunk("一个轻巧的翻译工具"));
            SourceBox.Text = "A small translator that helps you keep working.";
            var running = TranslateNowAsync();
            tasks.Add(running);
            await WaitForStreamingUiAsync(() => ResultBox.Text == "一个轻巧的翻译工具");
            RequireStream(!running.IsCompleted && _isStreamingPreview && !CopyButton.IsEnabled && _pending is not null,
                "Incremental preview was missing or exposed as completed");
            RequireStream(!ResultBox.IsUndoEnabled && ResultLabel.Text == UiLocalization.Get("ResultPreviewLabel") &&
                ProvenanceLabel.Text.Contains("stream.example.test") && ProvenanceLabel.Text.Contains("served-smoke-model") &&
                ProvenanceLabel.Text.Contains(UiLocalization.Get("ProvenanceStreaming")), "Preview provenance/state is missing");
            var themeBeforeStream = UiAppearance.Preference;
            var generationBeforeTheme = _generation;
            var requestBeforeTheme = handler.Requests;
            var pendingBeforeTheme = _pending;
            try
            {
                foreach (var theme in new[] { UiTheme.Dark, UiTheme.Light })
                {
                    UiAppearance.Apply(theme);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                    RequireStream(ResultBox.Text == "一个轻巧的翻译工具" && _isStreamingPreview && !CopyButton.IsEnabled &&
                        _pending == pendingBeforeTheme && !running.IsCompleted && _generation == generationBeforeTheme && handler.Requests == requestBeforeTheme,
                        "Changing appearance interrupted or retransmitted the stream");
                }
            }
            finally { UiAppearance.Apply(themeBeforeStream); }
            checks.Add("Light/dark changes during an active AI stream preserve partial text, cancellation ownership and request count");
            var beforeCopy = _statusKey;
            Copy_Click(this, new RoutedEventArgs()); // Same guard as Ctrl+Shift+C; must return before clipboard access.
            RequireStream(_statusKey == beforeCopy, "Disabled whole-result copy bypassed the preview guard");
            var requestsBeforeLanguage = handler.Requests;
            var generationBeforeLanguage = _generation;
            var sourceBeforeLanguage = SourceBox.Text;
            UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            UpdateLayout();
            RenderWindowTo(this, Path.ChangeExtension(output, ".streaming.zh.png"));
            UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
            RequireStream(ResultLabel.Text == UiLocalization.Get("ResultPreviewLabel") &&
                StatusLabel.Text == UiLocalization.Get("StatusStreaming") &&
                ProvenanceLabel.Text.Contains(UiLocalization.Get("ProvenanceStreaming")) &&
                ResultBox.Text == "一个轻巧的翻译工具" && SourceBox.Text == sourceBeforeLanguage &&
                handler.Requests == requestsBeforeLanguage && _generation == generationBeforeLanguage,
                "Language switching reset or retransmitted the in-flight stream");
            Width = MinWidth;
            UpdateLayout();
            RenderWindowTo(this, Path.ChangeExtension(output, ".streaming.en.png"));
            Width = 650;
            UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            stream.Complete(StreamChunk("，帮你继续手头的工作。") + StreamFinish());
            await running.WaitAsync(TimeSpan.FromSeconds(3));
            RequireStream(ResultBox.Text == "一个轻巧的翻译工具，帮你继续手头的工作。" && CopyButton.IsEnabled && !_isStreamingPreview &&
                _pending is null && _streamingPreview is null && _statusKey == "StatusCompleted" && stream.Disposed,
                "Stream did not transition from provisional text to a complete result");
            checks.Add("AI incremental preview, copy guard, provenance, bilingual UI and complete-result transition");

            foreach (var action in new[] { "cancel", "edit", "clear", "provider", "target", "hide", "settings" })
            {
                ShowForUser(false);
                SelectProvider(ProviderKind.OpenAI);
                LanguageBox.SelectedIndex = 0;
                var delayed = handler.Enqueue(StreamChunk("未完成预览"), ignoreCancellation: true);
                SourceBox.Text = "stream interruption: " + action;
                var task = TranslateNowAsync();
                tasks.Add(task);
                await WaitForStreamingUiAsync(() => ResultBox.Text == "未完成预览");
                var oldPreview = _streamingPreview!;
                var oldGeneration = _generation;
                var requests = handler.Requests;
                switch (action)
                {
                    case "cancel": Translate_Click(this, new RoutedEventArgs()); break;
                    case "edit": SourceBox.Text = "edited source"; break;
                    case "clear": Clear_Click(this, new RoutedEventArgs()); break;
                    case "provider": SelectProvider(ProviderKind.GoogleWeb); break;
                    case "target": LanguageBox.SelectedIndex = 1; break;
                    case "hide": HideToTray(); break;
                    case "settings": await RunSettingsDialogAsync(CancelStreamingSettingsDialog); break;
                }
                RequireStream(ResultBox.Text.Length == 0 && !_isStreamingPreview && !CopyButton.IsEnabled &&
                    _pending is null && _streamingPreview is null, action + " retained unfinished text or the preview timer");
                oldPreview.Report(new TranslationProgress("late queued preview", "old-model"));
                ShowStreamingProgress(new TranslationProgress("late generation", "old-model"), _settings.Translation, oldGeneration, CancellationToken.None);
                delayed.Complete(StreamChunk("late network content") + StreamFinish());
                await task.WaitAsync(TimeSpan.FromSeconds(3));
                await Task.Delay(80);
                RequireStream(ResultBox.Text.Length == 0 && handler.Requests == requests && IsVisible == (action != "hide") &&
                    ResultLabel.Text == UiLocalization.Get("ResultLabel"), action + " admitted a late update, reopened, or retried");
            }
            checks.Add("AI cancel/edit/clear/provider/target/hide/settings discard partial and late output without HTTP");

            ShowForUser(false);
            SelectProvider(ProviderKind.OpenAI);
            var oldStream = handler.Enqueue(StreamChunk("旧预览"), ignoreCancellation: true);
            SourceBox.Text = "old streaming request";
            var oldTask = TranslateNowAsync();
            tasks.Add(oldTask);
            await WaitForStreamingUiAsync(() => ResultBox.Text == "旧预览");
            var supersededPreview = _streamingPreview!;
            var newStream = handler.Enqueue(StreamChunk("新预览"));
            SourceBox.Text = "new streaming request";
            var newTask = TranslateNowAsync();
            tasks.Add(newTask);
            await WaitForStreamingUiAsync(() => ResultBox.Text == "新预览");
            oldStream.Complete(StreamChunk("旧内容") + StreamFinish());
            await oldTask.WaitAsync(TimeSpan.FromSeconds(3));
            supersededPreview.Report(new TranslationProgress("queued stale delta", "old-model"));
            await Task.Delay(80);
            RequireStream(ResultBox.Text == "新预览" && _pending is not null && _streamingPreview is not null && !newTask.IsCompleted,
                "Old completion cleared or replaced the new stream");
            newStream.Complete(StreamChunk("完成") + StreamFinish());
            await newTask.WaitAsync(TimeSpan.FromSeconds(3));
            RequireStream(ResultBox.Text == "新预览完成" && CopyButton.IsEnabled, "New stream failed to finish after superseding an old request");
            checks.Add("Latest AI stream wins even when old reads ignore cancellation and complete late");

            foreach (var failure in new[] { "eof", "length", "network", "provider-error" })
            {
                var broken = handler.Enqueue(StreamChunk("不完整内容"));
                SourceBox.Text = "stream failure: " + failure;
                var task = TranslateNowAsync();
                tasks.Add(task);
                await WaitForStreamingUiAsync(() => ResultBox.Text == "不完整内容");
                var requests = handler.Requests;
                if (failure == "network") broken.Fail(new IOException("sensitive stream-smoke-not-a-real-key"));
                else broken.Complete(failure switch
                {
                    "length" => StreamFinish("length"),
                    "provider-error" => "event: error\ndata: sensitive stream-smoke-not-a-real-key\n\n",
                    _ => ""
                });
                await task.WaitAsync(TimeSpan.FromSeconds(3));
                var expectedKey = failure switch
                {
                    "network" => "ErrorNetwork", "provider-error" => "ErrorStreamingFailed", _ => "ErrorIncompleteResponse"
                };
                RequireStream(_statusKey == expectedKey && ResultBox.Text.Length == 0 && !CopyButton.IsEnabled &&
                    _streamingPreview is null && !StatusLabel.Text.Contains("sensitive") && handler.Requests == requests,
                    failure + " was not safely cleared/classified or retried");
                UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
                RequireStream(!ProvenanceLabel.Text.Contains("sensitive") &&
                    ProvenanceLabel.Text.Contains(UiLocalization.Get("ErrorNoTranslation")) && handler.Requests == requests,
                    "Stream failure lost localized provenance or leaked content");
                UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            }
            checks.Add("AI EOF/truncation/network/provider errors clear partial output and retain safe localized errors without retry");

            handler.Enqueue("{\"choices\":[{\"message\":{\"content\":\"not a stream\"}}]}", contentType: "application/json").Complete("");
            SourceBox.Text = "unsupported streaming fixture";
            var beforeUnsupported = handler.Requests;
            await TranslateNowAsync();
            RequireStream(_statusKey == "ErrorStreamingNotSupported" && ResultBox.Text.Length == 0 && !CopyButton.IsEnabled &&
                _streamingPreview is null && handler.Requests == beforeUnsupported + 1,
                "Unsupported streaming silently downgraded, retried, or retained preview state");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
            RequireStream(StatusLabel.Text == UiLocalization.Get("ErrorStreamingNotSupported") && handler.Requests == beforeUnsupported + 1,
                "Streaming compatibility guidance was not localized without HTTP");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            checks.Add("Unsupported AI streaming offers a manual opt-out, never an automatic retry or downgrade");

            var renders = 0;
            var rendered = new StringBuilder();
            using (var preview = new StreamingTranslationPreview(Dispatcher, update => { renders++; rendered.Append(update.TextDelta); }))
            {
                await Task.Run(() =>
                {
                    for (var index = 0; index < 2000; index++) preview.Report(new TranslationProgress("x", "model"));
                });
                await WaitForStreamingUiAsync(() => rendered.Length == 2000);
                RequireStream(renders < 100, "Preview dispatched per token instead of coalescing");
                preview.Dispose();
                var before = renders;
                preview.Report(new TranslationProgress("late", "model"));
                await Task.Delay(120);
                RequireStream(renders == before && rendered.Length == 2000, "Disposed preview still rendered");
            }
            checks.Add("AI high-frequency deltas are coalesced; disposing the preview stops updates");
        }
        finally
        {
            CancelRequest();
            handler.ReleaseAll();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
            _service.Dispose();
            _service = originalService;
            RestoreSmokeSettings(originalSettings);
            UiLocalization.SetPreference(_settings.UiLanguage);
            Localize();
            _storage.Save(_settings);
            ShowForUser(false);
            SourceBox.Clear();
        }
    }

    private static void CancelStreamingSettingsDialog(SettingsWindow dialog)
    {
        var cancel = (Button)dialog.FindName("CancelButton");
        var peer = UIElementAutomationPeer.CreatePeerForElement(cancel) ?? new ButtonAutomationPeer(cancel);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static async Task WaitForStreamingUiAsync(Func<bool> condition)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (limit.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Streaming UI did not reach the expected state");
            await Task.Delay(15);
        }
    }

    private static void RequireStream(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string StreamChunk(string text) => "data: " + JsonSerializer.Serialize(new
    {
        model = "served-smoke-model", choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } }
    }) + "\n\n";

    private static string StreamFinish(string reason = "stop") => "data: " + JsonSerializer.Serialize(new
    {
        choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } }
    }) + "\n\ndata: [DONE]\n\n";

    private sealed class StreamingSmokeHandler : HttpMessageHandler
    {
        private readonly Queue<(StreamingSmokeStream Stream, string ContentType)> _responses = new();
        private readonly List<StreamingSmokeStream> _streams = new();
        public int Requests { get; private set; }
        public StreamingSmokeStream Enqueue(string prefix, bool ignoreCancellation = false, string contentType = "text/event-stream")
        {
            var stream = new StreamingSmokeStream(prefix, ignoreCancellation);
            _responses.Enqueue((stream, contentType));
            _streams.Add(stream);
            return stream;
        }
        public void ReleaseAll() { foreach (var stream in _streams) stream.Complete(""); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            RequireStream(body.RootElement.GetProperty("stream").GetBoolean() && request.RequestUri!.Host == "stream.example.test",
                "Unexpected request in the streaming smoke fixture");
            var (stream, contentType) = _responses.Dequeue();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return response;
        }
    }

    private sealed class StreamingSmokeStream(string prefix, bool ignoreCancellation) : Stream
    {
        private byte[] _current = Encoding.UTF8.GetBytes(prefix);
        private int _offset;
        private bool _tailLoaded;
        private readonly TaskCompletionSource<byte[]> _tail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public void Complete(string tail) => _tail.TrySetResult(Encoding.UTF8.GetBytes(tail));
        public void Fail(Exception error) => _tail.TrySetException(error);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_offset == _current.Length && !_tailLoaded)
            {
                _current = await (ignoreCancellation ? _tail.Task : _tail.Task.WaitAsync(token));
                _offset = 0;
                _tailLoaded = true;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
