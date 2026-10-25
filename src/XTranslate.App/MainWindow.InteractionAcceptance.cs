using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    // Interaction details: every failure offers one next step, settings open where the problem is,
    // and the About page / update notices are present. Mock HTTPS + smoke-data only.
    private async Task RunInteractionAcceptanceAsync(List<string> checks, string output)
    {
        var originalService = _service;
        var originalSettings = _settings.Copy();
        using var handler = new InteractionSmokeHandler();
        using var client = new HttpClient(handler);
        _service = new TranslationService(client);
        try
        {
            ShowForUser(false);
            // A preceding scenario may have just shown a hidden HWND. Let WPF reconnect its visual tree
            // before asserting effective child visibility; keep the actual IsVisible assertion below.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateLayout();
            _settings.Translation.DeepLApiKey = "";
            _settings.AutoFallback = false;
            ConfigureSmokeAiService("https://ux.example.test/v1", "", "smoke-model", false);
            Localize();
            RequireUx(((string)ProviderDeepLItem.Content).Contains(UiLocalization.Get("ProviderNoKey")) &&
                ((string)AiProviderItems().First().Content).Contains(UiLocalization.Get("ProviderNoKey")), "Unconfigured services are not labeled");
            SelectProvider(ProviderKind.GoogleWeb);
            SelectProvider(ProviderKind.OpenAI);
            RequireUx(_statusKey == "StatusKeyMissing" && StatusActionButton.IsVisible && handler.Requests == 0,
                $"Selecting a service without a key did not explain and offer settings: status={_statusKey}, action={_statusAction}, visibility={StatusActionButton.Visibility}/{StatusActionButton.IsVisible}, window={IsVisible}, ready={_ready}, review={ReviewRequested}, fallback={_settings.AutoFallback}, requests={handler.Requests}");
            checks.Add("Services without a key are labeled in the list; selecting one explains it and offers Open settings, without HTTP");

            var serviceId = _settings.ActiveAiService!.Id;
            await RunSettingsDialogAsync(dialog =>
            {
                RequireUx(((TabControl)dialog.FindName("SectionTabs")).SelectedItem == dialog.FindName("ServicesTab") &&
                    ((AiServiceProfile)((ComboBoxItem)((ComboBox)dialog.FindName("AiServiceBox")).SelectedItem).Tag).Id == serviceId,
                    "Open settings did not land on the failing service");
                ((PasswordBox)dialog.FindName("OpenAiKeyBox")).Password = "ux-smoke-not-a-real-key";
                ((TabControl)dialog.FindName("SectionTabs")).SelectedItem = dialog.FindName("ServicesTab");
                ((TextBox)dialog.FindName("HotkeyBox")).Text = "Ctrl+F12";
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequireUx(dialog.IsVisible && ((TabControl)dialog.FindName("SectionTabs")).SelectedItem == dialog.FindName("GeneralTab"),
                    "A save error did not switch to the page that contains the problem");
                ((TextBox)dialog.FindName("HotkeyBox")).Text = "Ctrl+Q";
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, () => StatusAction_Click(this, new RoutedEventArgs()));
            RequireUx(!((string)AiProviderItems().First().Content).Contains(UiLocalization.Get("ProviderNoKey")) && handler.Requests == 0,
                "Saving a key did not clear the label");
            checks.Add("Open settings lands on the failing service; save errors switch to the page with the problem");

            handler.Status = HttpStatusCode.ServiceUnavailable;
            SourceBox.Text = "interaction fixture";
            await TranslateNowAsync();
            RequireUx(_statusAction == StatusAction.Retry && StatusActionButton.IsVisible && handler.Requests == 1, "Transient failure did not offer Retry");
            handler.Status = HttpStatusCode.OK;
            if (_statusAction == StatusAction.Retry) await TranslateNowAsync(); // What StatusAction_Click runs for Retry.
            RequireUx(ResultBox.Text == "ok" && handler.Requests == 2 && !StatusActionButton.IsVisible, "Retry did not send exactly one more request");
            handler.Status = HttpStatusCode.NotFound;
            await TranslateNowAsync();
            RequireUx(_statusAction == StatusAction.Settings && (string)StatusActionButton.Content == UiLocalization.Get("StatusActionSettings"),
                "Configuration failure did not offer settings");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
            RequireUx((string)StatusActionButton.Content == "Open settings" && handler.Requests == 3, "Status action did not relocalize");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            checks.Add("Transient failures offer Retry (one request); configuration failures offer Open settings; both relocalize");

            ShowInfo(InfoKind.RestartPending);
            UpdateLayout();
            RequireUx(InfoBar.IsVisible && InfoBarText.Text == UiLocalization.Get("InfoRestartPending"), "Restart notice missing");
            var originalWidth = Width; var originalHeight = Height;
            try
            {
                Width = MinWidth; Height = MinHeight;
                foreach (var ui in new[] { UiLanguage.English, UiLanguage.SimplifiedChinese })
                {
                    UiLanguageBox.SelectedIndex = (int)ui;
                    WindowViewport.ScrollToTop(); await SettleMainScrollAsync();
                    RequireUx(FullyInsideOcrElement(MainHeader, ViewportFrame) && FullyInsideOcrElement(MainFooter, ViewportFrame) &&
                        FullyInsideOcrElement(InfoBar, WindowViewport) && FullyInsideOcrElement(StatusActionButton, ViewportFrame),
                        "Notification/error layout hid the fixed header/footer, status action or notification");
                    await RevealMainElementAsync(ResultBox);
                    RequireUx(ResultBox.ActualHeight >= 60 && FullyInsideOcrElement(ResultBox, WindowViewport),
                        "Notification/error layout made the translation unreachable");
                    RenderWindowTo(this, Path.ChangeExtension(output, $".interaction.minimum.{(ui == UiLanguage.English ? "en" : "zh")}.png"));
                }
            }
            finally { Width = originalWidth; Height = originalHeight; }
            RenderWindowTo(this, Path.ChangeExtension(output, ".interaction.png"));
            InfoBarClose_Click(this, new RoutedEventArgs());
            RequireUx(!InfoBar.IsVisible, "Notice did not close");
            await RunSettingsDialogAsync(dialog =>
            {
                ((TabControl)dialog.FindName("SectionTabs")).SelectedItem = dialog.FindName("AboutTab");
                dialog.UpdateLayout();
                RequireUx(((TextBlock)dialog.FindName("VersionLabel")).Text.Contains(AppUpdates.Current.ToString()) &&
                    ((CheckBox)dialog.FindName("AutoUpdateBox")).IsChecked == false &&
                    ((Button)dialog.FindName("CheckUpdateButton")).IsEnabled == AppUpdates.CanSelfUpdate, "About page is incomplete");
                RenderWindowTo(dialog, Path.ChangeExtension(output, ".about.png"));
                CancelStreamingSettingsDialog(dialog);
            });
            checks.Add("About shows version and edition; update check is opt-in; restart notice shows and closes; no HTTP");
        }
        finally
        {
            CancelRequest();
            _service.Dispose();
            _service = originalService;
            ShowInfo(InfoKind.None);
            RestoreSmokeSettings(originalSettings);
            Localize();
            _storage.Save(_settings);
            ShowForUser(false);
            SourceBox.Clear();
        }
    }

    private static void RequireUx(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class InteractionSmokeHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(Status == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(Status) { Content = new StringContent("secret body") });
        }
    }
}
