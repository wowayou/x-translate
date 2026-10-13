using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    // Stage B WPF checks: legacy migration, per-service keys, add/remove through the real
    // SettingsWindow, native Anthropic/Gemini requests and key isolation. Mock HTTPS + smoke-data only.
    private async Task RunServicesAcceptanceAsync(List<string> checks, string output)
    {
        var originalService = _service;
        var originalSettings = _settings.Copy();
        using var handler = new ServicesSmokeHandler();
        using var client = new HttpClient(handler);
        _service = new TranslationService(client);
        try
        {
            ShowForUser(false);
            CheckLegacyMigration();
            checks.Add("Legacy single-AI settings migrate to a named service; unreadable keys are kept as ciphertext, not erased");

            ConfigureSmokeAiService("https://api.openai.com/v1", "openai-smoke-not-a-real-key", "smoke-model", false);
            _storage.Save(_settings);
            var openAiId = _settings.ActiveAiServiceId;
            await RunSettingsDialogAsync(dialog =>
            {
                dialog.AddService(AiServiceTemplates.All.Single(template => template.Id == "anthropic"));
                RequireServices(((PasswordBox)dialog.FindName("OpenAiKeyBox")).Password.Length == 0 &&
                    ((ComboBox)dialog.FindName("AiProtocolBox")).SelectedIndex == (int)AiProtocol.AnthropicMessages &&
                    ((TextBlock)dialog.FindName("EndpointPreviewLabel")).Text.Contains("https://api.anthropic.com/v1/messages"),
                    "Anthropic template did not start credential-free with the native route");
                ((PasswordBox)dialog.FindName("OpenAiKeyBox")).Password = "anthropic-smoke-not-a-real-key";
                dialog.AddService(AiServiceTemplates.All.Single(template => template.Id == "gemini"));
                ((PasswordBox)dialog.FindName("OpenAiKeyBox")).Password = "gemini-smoke-not-a-real-key";
                ((CheckBox)dialog.FindName("OpenAiStreamingBox")).IsChecked = true;
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var saved = _storage.Load();
            var anthropic = saved.AiServices.Single(service => service.Protocol == AiProtocol.AnthropicMessages);
            var gemini = saved.AiServices.Single(service => service.Protocol == AiProtocol.GeminiGenerateContent);
            var file = File.ReadAllText(Path.Combine(_storage.DirectoryPath, "settings.json"));
            RequireServices(saved.AiServices.Count == 3 && saved.ActiveAiServiceId == openAiId &&
                anthropic.ApiKey == "anthropic-smoke-not-a-real-key" && gemini.ApiKey == "gemini-smoke-not-a-real-key" && gemini.Streaming &&
                saved.AiSecrets.Count == 3 && !file.Contains("smoke-not-a-real-key") && AiProviderItems().Count() == 3 && handler.Requests.Count == 0,
                "Services, per-service keys or provider list were not saved correctly");
            checks.Add("Settings add Anthropic and Gemini from templates with separate encrypted keys, without HTTP");

            SelectAiProvider(anthropic.Id);
            SourceBox.Text = "service fixture";
            await TranslateNowAsync();
            var anthropicRequest = handler.Requests.Last();
            RequireServices(ResultBox.Text == "hola" && anthropicRequest.Uri == "https://api.anthropic.com/v1/messages" &&
                anthropicRequest.Key == "anthropic-smoke-not-a-real-key" && anthropicRequest.Authorization is null &&
                ProvenanceLabel.Text.Contains("Anthropic · api.anthropic.com") && RequestHint.Text.Contains("api.anthropic.com") &&
                _storage.Load().ActiveAiServiceId == anthropic.Id, "Anthropic service request, key or provenance is wrong");
            SelectAiProvider(gemini.Id);
            SourceBox.Text = "service fixture two";
            await TranslateNowAsync();
            var geminiRequest = handler.Requests.Last();
            RequireServices(ResultBox.Text == "hola" && geminiRequest.Uri.EndsWith("/models/gemini-3.8-flash:streamGenerateContent?alt=sse") &&
                geminiRequest.Key == "gemini-smoke-not-a-real-key" && !geminiRequest.Uri.Contains("smoke-not-a-real-key") &&
                ProvenanceLabel.Text.Contains("Gemini") && handler.Requests.Count == 2, "Gemini service request, key or count is wrong");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
            RequireServices(AiProviderItems().All(item => ((string)item.Content).StartsWith("AI · ")) && handler.Requests.Count == 2,
                "AI service entries did not relocalize, or switching language sent HTTP");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            checks.Add("Selecting a service routes native Anthropic Messages and Gemini streaming with that service's key only");

            await RunSettingsDialogAsync(dialog =>
            {
                var box = (ComboBox)dialog.FindName("AiServiceBox");
                box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(item => ((AiServiceProfile)item.Tag).Id == anthropic.Id);
                RequireServices(((PasswordBox)dialog.FindName("OpenAiKeyBox")).Password == "anthropic-smoke-not-a-real-key", "Editor did not load the selected key");
                ((TextBox)dialog.FindName("EndpointBox")).Text = "https://other-host.example.test";
                RequireServices(((PasswordBox)dialog.FindName("OpenAiKeyBox")).Password.Length == 0 &&
                    ((TextBlock)dialog.FindName("ErrorLabel")).Text == UiLocalization.Get("SettingsKeyOriginChanged"), "Key followed a new host");
                box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(item => ((AiServiceProfile)item.Tag).Id == gemini.Id);
                RequireServices(((PasswordBox)dialog.FindName("OpenAiKeyBox")).Password == "gemini-smoke-not-a-real-key", "Switching services mixed keys");
                CancelStreamingSettingsDialog(dialog);
            });
            RequireServices(_storage.Load().AiServices.Single(service => service.Id == anthropic.Id).ApiKey == "anthropic-smoke-not-a-real-key" &&
                _settings.AiServices.Single(service => service.Id == anthropic.Id).Endpoint == "https://api.anthropic.com",
                "Canceling the dialog changed a service");
            checks.Add("Per-service editor clears a key on host change, never mixes keys between services, and cancel keeps saved services");

            await RunSettingsDialogAsync(dialog =>
            {
                dialog.AddService(AiServiceTemplates.All.Single(template => template.Id == "custom"));
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequireServices(dialog.IsVisible && ((TextBlock)dialog.FindName("ErrorLabel")).Text.Length > 0, "Incomplete custom service was saved");
                CancelStreamingSettingsDialog(dialog);
            });
            await RunSettingsDialogAsync(dialog =>
            {
                var box = (ComboBox)dialog.FindName("AiServiceBox");
                box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(item => ((AiServiceProfile)item.Tag).Id == gemini.Id);
                ((Button)dialog.FindName("RemoveAiServiceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            saved = _storage.Load();
            RequireServices(saved.AiServices.Count == 2 && !saved.AiSecrets.ContainsKey(gemini.Id) && saved.ActiveAiService!.Id != gemini.Id &&
                AiProviderItems().Count() == 2 && ProviderBox.SelectedItem is ComboBoxItem { DataContext: AiServiceProfile } && handler.Requests.Count == 2,
                "Removing the active service kept its key, left a dangling selection or sent HTTP");
            checks.Add("Incomplete services are rejected on save; removing the active service drops its key and reselects a remaining service");

            await RunSettingsDialogAsync(dialog =>
            {
                dialog.AddService(AiServiceTemplates.All.Single(template => template.Id == "gemini"));
                foreach (var language in new[] { UiLanguage.SimplifiedChinese, UiLanguage.English })
                {
                    ((ComboBox)dialog.FindName("UiLanguageBox")).SelectedIndex = (int)language;
                    dialog.UpdateLayout();
                    dialog.Reveal((FrameworkElement)dialog.FindName("AiServiceBox"));
                    dialog.UpdateLayout();
                    RenderWindowTo(dialog, Path.ChangeExtension(output, language == UiLanguage.English ? ".services.en.png" : ".services.zh.png"));
                }
                CancelStreamingSettingsDialog(dialog);
            });
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

    private void CheckLegacyMigration()
    {
        var directory = Path.Combine(_storage.DirectoryPath, "legacy-migration");
        Directory.CreateDirectory(directory);
        try
        {
            var legacy = new
            {
                Translation = new { Provider = 2, TargetLanguage = "en", OpenAiEndpoint = "https://api.deepseek.com/", OpenAiModel = "legacy-model", OpenAiStreaming = true },
                OpenAiSecret = SecretProtection.Protect("legacy-smoke-not-a-real-key")
            };
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(legacy));
            var storage = new AppStorage(directory);
            var migrated = storage.Load();
            var service = migrated.AiServices.Single();
            RequireServices(service.Name == "DeepSeek" && service.Endpoint == "https://api.deepseek.com/" && service.Model == "legacy-model" &&
                service.Streaming && service.ApiKey == "legacy-smoke-not-a-real-key" && migrated.Translation.OpenAiApiKey == "legacy-smoke-not-a-real-key" &&
                migrated.Translation.Provider == ProviderKind.OpenAI, "Legacy AI settings did not migrate");
            storage.Save(migrated);
            var file = File.ReadAllText(Path.Combine(directory, "settings.json"));
            using (var document = JsonDocument.Parse(file))
                RequireServices(document.RootElement.GetProperty("OpenAiSecret").GetString() == "" &&
                    document.RootElement.GetProperty("AiSecrets").GetProperty(service.Id).GetString()!.Length > 0 &&
                    !file.Contains("legacy-smoke-not-a-real-key"), "Migrated key was not moved into the per-service store");

            // A blob from another PC/user: kept intact, flagged, and not a reason to block unrelated saves.
            var foreign = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a dpapi blob from this user"));
            var reloaded = storage.Load();
            reloaded.AiSecrets[service.Id] = foreign;
            reloaded.Translation.OpenAiApiKey = ""; // This fixture file is written directly; keep it key-free.
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(reloaded));
            var unreadable = storage.Load();
            RequireServices(unreadable.AiServices.Single().SecretUnavailable && unreadable.AiServices.Single().ApiKey.Length == 0 &&
                storage.Warning is not null, "Unreadable key was not flagged");
            unreadable.UiLanguage = UiLanguage.English;
            storage.Save(unreadable);
            RequireServices(storage.Load().AiSecrets[service.Id] == foreign && storage.Load().UiLanguage == UiLanguage.English,
                "Unrelated save erased the unreadable key or failed");
        }
        finally { Directory.Delete(directory, true); }
    }

    private void SelectAiProvider(string id) =>
        ProviderBox.SelectedItem = AiProviderItems().First(item => ((AiServiceProfile)item.DataContext).Id == id);

    /// <summary>Points the active AI service at a smoke endpoint and refreshes the provider list.</summary>
    private void ConfigureSmokeAiService(string endpoint, string key, string model, bool streaming)
    {
        _settings.EnsureAiService();
        var service = _settings.ActiveAiService!;
        service.Protocol = AiProtocol.OpenAiChat;
        service.Endpoint = endpoint;
        service.ApiKey = key;
        service.Model = model;
        service.Streaming = streaming;
        _settings.ApplyActiveAiService();
        RebuildAiProviderItems();
        UpdateRequestHint();
    }

    private void RestoreSmokeSettings(UserSettings original)
    {
        _settings = original;
        _ready = false;
        RebuildAiProviderItems();
        SelectProvider(_settings.Translation.Provider);
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == _settings.Translation.TargetLanguage);
        _ready = true;
    }

    private static void RequireServices(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed record ServicesRequest(string Uri, string? Key, string? Authorization);

    private sealed class ServicesSmokeHandler : HttpMessageHandler
    {
        public List<ServicesRequest> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            var key = request.Headers.TryGetValues("x-api-key", out var anthropic) ? anthropic.Single()
                : request.Headers.TryGetValues("x-goog-api-key", out var google) ? google.Single() : null;
            Requests.Add(new ServicesRequest(uri.AbsoluteUri, key, request.Headers.Authorization?.ToString()));
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host == "api.anthropic.com")
                response.Content = new StringContent("{\"model\":\"claude-smoke\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"hola\"}]}",
                    Encoding.UTF8, "application/json");
            else if (uri.Host == "generativelanguage.googleapis.com")
                response.Content = new StringContent("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ho\"}]}}]}\n\n" +
                    "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"la\"}]},\"finishReason\":\"STOP\"}]}\n\n",
                    Encoding.UTF8, "text/event-stream");
            else response.StatusCode = HttpStatusCode.NotFound;
            return Task.FromResult(response);
        }
    }
}
