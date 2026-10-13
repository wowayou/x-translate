using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    // Opt-in service fallback through the real settings dialog and request path. Mock HTTPS + smoke-data only.
    private async Task RunFallbackAcceptanceAsync(List<string> checks, string output)
    {
        var originalService = _service;
        var originalSettings = _settings.Copy();
        using var handler = new FallbackSmokeHandler(() => Dispatcher.Invoke(() => ResultBox.Text == "半成品" && _isStreamingPreview));
        using var client = new HttpClient(handler);
        _service = new TranslationService(client);
        try
        {
            ShowForUser(false);
            ConfigureSmokeAiService("https://fail.example.test/v1", "fallback-smoke-not-a-real-key", "smoke-model", false);
            _settings.Translation.DeepLApiKey = "";
            _settings.AutoFallback = false;
            _settings.FallbackOrder.Clear();
            SelectProvider(ProviderKind.OpenAI);
            _storage.Save(_settings);

            await RunSettingsDialogAsync(dialog =>
            {
                var toggle = (CheckBox)dialog.FindName("AutoFallbackBox");
                var list = (ListBox)dialog.FindName("FallbackList");
                RequireFallback(toggle.IsChecked == false && !list.IsEnabled && list.Items.Count == 2 + _settings.AiServices.Count &&
                    (string)((ListBoxItem)list.Items[0]).Tag == UserSettings.GoogleKey, "Fallback must start off with every service listed");
                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                list.SelectedIndex = 1; // DeepL
                dialog.MoveFallback(-1);
                RequireFallback((string)((ListBoxItem)list.Items[0]).Tag == UserSettings.DeepLKey && list.SelectedIndex == 0,
                    "Move up did not reorder the backup list");
                dialog.MoveFallback(1);
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var saved = _storage.Load();
            RequireFallback(saved.AutoFallback && saved.FallbackOrder[0].Key == UserSettings.GoogleKey &&
                saved.FallbackOrder[1].Key == UserSettings.DeepLKey && handler.Hosts.Count == 0, "Fallback setting/order did not persist or sent HTTP");
            RequireFallback(RequestHint.Text.Contains("fail.example.test") && RequestHint.Text.Contains("Google") && RequestHint.Text.Contains("DeepL") &&
                RequestHint.Text.IndexOf("Google", StringComparison.Ordinal) < RequestHint.Text.IndexOf("DeepL", StringComparison.Ordinal),
                "Request hint does not disclose the backup order");
            await RunSettingsDialogAsync(dialog =>
            {
                ((CheckBox)dialog.FindName("AutoFallbackBox")).IsChecked = false;
                CancelStreamingSettingsDialog(dialog);
            });
            RequireFallback(_storage.Load().AutoFallback, "Canceling the dialog changed the fallback setting");
            checks.Add("Fallback is off by default; settings enable it, reorder backups, persist, and cancel keeps it; the hint lists every recipient");

            SourceBox.Text = "fallback fixture";
            await TranslateNowAsync();
            RequireFallback(ResultBox.Text == "备用译文" && string.Join(",", handler.Hosts) == "fail.example.test,translate.googleapis.com" &&
                _statusKey == "StatusFallbackCompleted" && StatusLabel.Text.Contains("Google") && StatusLabel.Text.Contains("503") &&
                ProvenanceLabel.Text.Contains(UiLocalization.Get("ProvenanceFallback")) && CopyButton.IsEnabled,
                "AI failure did not fall back to Google with a disclosed reason");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
            RequireFallback(StatusLabel.Text.Contains("server error") && handler.Hosts.Count == 2, "Fallback status did not relocalize, or sent HTTP");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            var log = File.ReadLines(Path.Combine(_storage.DirectoryPath, "diagnostics.jsonl")).TakeLast(2).ToArray();
            RequireFallback(log[0].Contains("\"outcome\":\"failed\"") && log[0].Contains("\"statusCode\":503") && log[1].Contains("\"action\":\"fallback\"") &&
                log[1].Contains("\"outcome\":\"success\"") && !log[1].Contains("fallback fixture") && !log[0].Contains("fail.example.test"),
                "Fallback attempts were not logged safely");
            checks.Add("A failed AI request falls back to the first backup in the user's order, discloses why, and logs each attempt without content");

            ConfigureSmokeAiService("https://stream-fail.example.test/v1", "fallback-smoke-not-a-real-key", "smoke-model", true);
            _storage.Save(_settings);
            handler.Hosts.Clear();
            SourceBox.Text = "fallback streaming fixture";
            await TranslateNowAsync();
            RequireFallback(handler.SawPartial && ResultBox.Text == "备用译文" && !ResultBox.Text.Contains("半成品") &&
                !_isStreamingPreview && _streamingPreview is null && handler.Hosts.Count == 2,
                "Partial streamed text survived the switch to a backup service");
            checks.Add("A stream that breaks mid-way clears its partial text before the backup's translation is shown");

            handler.Hosts.Clear();
            SourceBox.Text = "fallback allfail fixture";
            await TranslateNowAsync();
            RequireFallback(ResultBox.Text.Length == 0 && !CopyButton.IsEnabled && _statusKey == "ErrorAllFallbacksFailed" &&
                handler.Hosts.Count == 2 && DiagnosticLabel.Text.Contains("DeepL: MissingApiKey") &&
                StatusLabel.Text.Contains(UiLocalization.Get("FallbackReasonMissingKey")) &&
                ProvenanceLabel.Text.Contains(UiLocalization.Format("ErrorNoTranslationFallback", 2)) &&
                ProviderBox.SelectedItem is ComboBoxItem { DataContext: AiServiceProfile }, "All-failed fallback was not reported per service");
            UpdateLayout();
            RenderWindowTo(this, Path.ChangeExtension(output, ".fallback.png"));
            checks.Add("When every service fails, no text is shown and each attempt (sent or skipped) is listed");

            _settings.AutoFallback = false;
            _storage.Save(_settings);
            UpdateRequestHint();
            handler.Hosts.Clear();
            SourceBox.Text = "fallback disabled fixture";
            await TranslateNowAsync();
            RequireFallback(handler.Hosts.Count == 1 && ResultBox.Text.Length == 0 && ProvenanceLabel.Text.Contains(UiLocalization.Get("ErrorNoTranslation")) &&
                !RequestHint.Text.Contains("Google"), "Disabled fallback still switched services or disclosed backups");
            checks.Add("With fallback off, a failure stays on the selected service and nothing else receives the text");
        }
        finally
        {
            CancelRequest();
            _service.Dispose();
            _service = originalService;
            RestoreSmokeSettings(originalSettings);
            Localize();
            _storage.Save(_settings);
            ShowForUser(false);
            SourceBox.Clear();
        }
    }

    private static void RequireFallback(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FallbackSmokeHandler(Func<bool> partialVisible) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = new();
        public bool SawPartial { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Hosts.Add(uri.Host);
            if (uri.Host == "translate.googleapis.com")
                return Uri.UnescapeDataString(uri.Query).Contains("allfail")
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[[[\"备用译文\",\"x\"]],null,\"en\"]", Encoding.UTF8, "application/json") };
            if (uri.Host == "stream-fail.example.test")
            {
                // One delta, a pause long enough for the 50 ms preview to render, then EOF without [DONE].
                var stream = new DelayedEofStream("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"半成品\"},\"finish_reason\":null}]}\n\n", () => SawPartial = partialVisible());
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return response;
            }
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("secret body") };
        }
    }

    private sealed class DelayedEofStream(string prefix, Action beforeEof) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(prefix);
        private int _offset;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_offset < _bytes.Length)
            {
                var count = Math.Min(buffer.Length, _bytes.Length - _offset);
                _bytes.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            await Task.Delay(250, token);
            beforeEof();
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
