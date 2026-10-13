using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    // Stage A WPF checks: language catalog, endpoint preview, key origin isolation,
    // custom hotkey persistence and safe failure diagnostics. Mock HTTPS + smoke-data only.
    private async Task RunFoundationAcceptanceAsync(List<string> checks, string output)
    {
        var originalService = _service;
        var originalSettings = _settings.Copy();
        using var handler = new FoundationSmokeHandler();
        using var client = new HttpClient(handler);
        _service = new TranslationService(client);
        try
        {
            ShowForUser(false);

            // Language catalog: every target is listed; DeepL disables unmapped targets without HTTP.
            RequireFoundation(LanguageBox.Items.Count == TranslationLanguages.All.Count &&
                LanguageBox.Items.Cast<ComboBoxItem>().Any(item => (string)item.Tag == "es"), "Language catalog is incomplete");
            SelectProvider(ProviderKind.GoogleWeb);
            LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == "es");
            RequireFoundation(_storage.Load().Translation.TargetLanguage == "es", "Spanish target was not persisted");
            SelectProvider(ProviderKind.DeepL);
            var hindi = LanguageBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == "hi");
            RequireFoundation(!hindi.IsEnabled, "DeepL kept an unmapped target selectable");
            LanguageBox.SelectedItem = hindi; // programmatic selection still must not send HTTP
            SourceBox.Text = "unsupported language fixture";
            RequireFoundation(!TranslateButton.IsEnabled && handler.Requests == 0, "Unsupported language allowed a request");
            await TranslateNowAsync();
            RequireFoundation(handler.Requests == 0, "Unsupported language reached HTTP");
            checks.Add("Target catalog lists Spanish and more; unsupported service/language pairs never send HTTP");

            // Endpoint preview, key isolation on origin change, custom hotkey persistence.
            ConfigureSmokeAiService("https://api.openai.com/v1/chat/completions", "foundation-smoke-not-a-real-key", "smoke-model", false);
            _storage.Save(_settings);
            await RunSettingsDialogAsync(dialog =>
            {
                var endpoint = (TextBox)dialog.FindName("EndpointBox");
                var preview = (TextBlock)dialog.FindName("EndpointPreviewLabel");
                var key = (PasswordBox)dialog.FindName("OpenAiKeyBox");
                endpoint.Text = "https://api.openai.com/v1";
                RequireFoundation(preview.Text.Contains("https://api.openai.com/v1/chat/completions") && key.Password.Length > 0,
                    "v1 base URL preview is wrong or same-origin key was dropped");
                endpoint.Text = "https://relay.example.test";
                RequireFoundation(preview.Text.Contains("https://relay.example.test/v1/chat/completions") && key.Password.Length == 0,
                    "Origin change kept the previous key");
                endpoint.Text = "https://api.anthropic.com/v1/messages";
                RequireFoundation(preview.Text == UiLocalization.Get("ErrorUnsupportedProtocol"), "Native non-OpenAI route was not flagged");
                endpoint.Text = "https://relay.example.test/v1";
                key.Password = "relay-smoke-not-a-real-key";
                ((TextBox)dialog.FindName("HotkeyBox")).Text = "Ctrl+Shift+F12";
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequireFoundation(dialog.IsVisible && ((TextBlock)dialog.FindName("ErrorLabel")).Text == UiLocalization.Get("SettingsHotkeyError"),
                    "Reserved F12 hotkey was accepted");
                ((TextBox)dialog.FindName("HotkeyBox")).Text = "Ctrl+Shift+F9";
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var saved = _storage.Load();
            RequireFoundation(saved.CaptureHotkey == "Ctrl+Shift+F9" && saved.ActiveAiService!.Endpoint == "https://relay.example.test/v1" &&
                saved.ActiveAiService.ApiKey == "relay-smoke-not-a-real-key" && saved.Translation.OpenAiApiKey == "relay-smoke-not-a-real-key" &&
                handler.Requests == 0,
                "Settings did not persist resolved endpoint, key and hotkey without HTTP");
            var settingsFile = File.ReadAllText(Path.Combine(_storage.DirectoryPath, "settings.json"));
            RequireFoundation(!settingsFile.Contains("relay-smoke-not-a-real-key"), "Plaintext key reached settings.json");
            await RunSettingsDialogAsync(dialog =>
            {
                ((TextBox)dialog.FindName("HotkeyBox")).Text = "Ctrl+Alt+K";
                CancelStreamingSettingsDialog(dialog);
            });
            RequireFoundation(_storage.Load().CaptureHotkey == "Ctrl+Shift+F9", "Canceling settings changed the hotkey");
            checks.Add("Endpoint preview resolves base URLs, flags native protocols, isolates keys by origin; custom hotkey save/cancel");

            // Failure diagnostics: classified code + status, no key/body leakage, no retry.
            SelectProvider(ProviderKind.OpenAI);
            LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == "es");
            handler.Status = HttpStatusCode.NotFound;
            SourceBox.Text = "diagnostic fixture";
            await TranslateNowAsync();
            RequireFoundation(handler.Requests == 1 && handler.LastUri == "https://relay.example.test/v1/chat/completions" &&
                _statusKey == "ErrorNotFound" && DiagnosticLabel.Text.StartsWith("NotFound · HTTP 404 · #", StringComparison.Ordinal) &&
                !StatusLabel.Text.Contains("relay-smoke") && !DiagnosticLabel.Text.Contains("secret-body") && ResultBox.Text.Length == 0,
                "HTTP 404 was not classified safely or was retried");
            var log = File.ReadLines(Path.Combine(_storage.DirectoryPath, "diagnostics.jsonl")).Last();
            RequireFoundation(log.Contains("\"errorCode\":\"NotFound\"") && log.Contains("\"statusCode\":404") &&
                !log.Contains("relay-smoke") && !log.Contains("diagnostic fixture") && !log.Contains("relay.example.test"),
                "Diagnostics log is missing the category or leaks data");
            UpdateLayout();
            RenderWindowTo(this, Path.ChangeExtension(output, ".diagnostics.png"));
            checks.Add("AI failures show code/status/request id; logs exclude keys, text and URLs; no retry");
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

    private static void RequireFoundation(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FoundationSmokeHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? LastUri { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            LastUri = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("{\"error\":{\"message\":\"secret-body relay-smoke-not-a-real-key\"}}")
            });
        }
    }
}
