using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace XTranslate.App;

public partial class MainWindow
{
    // Runs on the existing smoke-test UI thread and uses its injected HTTP
    // handler. The dialog checks intentionally exercise the real ShowDialog and
    // SettingsWindow event paths rather than calling private setters directly.
    private async Task RunExtendedAcceptanceAsync(List<string> checks, Func<int> requestCount)
    {
        SourceBox.Text = "second";
        await TranslateNowAsync();
        if (string.IsNullOrWhiteSpace(ResultBox.Text)) throw new Exception("Baseline translation did not produce a result");

        var sourceBefore = SourceBox.Text;
        var resultBefore = ResultBox.Text;
        var providerBefore = ProviderBox.SelectedIndex;
        var targetBefore = ((ComboBoxItem)LanguageBox.SelectedItem).Tag?.ToString();
        var requestsBefore = requestCount();

        await RunSettingsDialogAsync(dialog =>
        {
            var language = dialog.FindName("UiLanguageBox") as ComboBox ?? throw new Exception("Settings language control not found");
            var save = dialog.FindName("SaveButton") as Button ?? throw new Exception("Settings save control not found");
            language.SelectedIndex = (int)UiLanguage.English;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        if (SourceBox.Text != sourceBefore || ResultBox.Text != resultBefore ||
            ProviderBox.SelectedIndex != providerBefore ||
            ((ComboBoxItem)LanguageBox.SelectedItem).Tag?.ToString() != targetBefore ||
            requestCount() != requestsBefore)
            throw new Exception("Saving interface language changed translation state or issued HTTP");
        if (_storage.Load().UiLanguage != UiLanguage.English)
            throw new Exception("Settings dialog save did not persist interface language");
        checks.Add("Real SettingsWindow save preserves source/result/provider/target and persists UI language without HTTP");

        var persistedAfterSave = _storage.Load().UiLanguage;
        var preferenceAfterSave = UiLocalization.Preference;
        requestsBefore = requestCount();
        await RunSettingsDialogAsync(dialog =>
        {
            var language = dialog.FindName("UiLanguageBox") as ComboBox ?? throw new Exception("Settings language control not found");
            var cancel = dialog.FindName("CancelButton") as Button ?? throw new Exception("Settings cancel control not found");
            language.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            // IsCancel has no Click handler in SettingsWindow; invoke the actual
            // WPF button command so the dialog's cancel semantics close it.
            var peer = UIElementAutomationPeer.CreatePeerForElement(cancel) ?? new ButtonAutomationPeer(cancel);
            ((IInvokeProvider)(peer.GetPattern(PatternInterface.Invoke) ?? throw new Exception("Settings cancel invoke pattern not found"))).Invoke();
        });
        if (_storage.Load().UiLanguage != persistedAfterSave || UiLocalization.Preference != preferenceAfterSave ||
            SourceBox.Text != sourceBefore || ResultBox.Text != resultBefore || requestCount() != requestsBefore)
            throw new Exception("Cancel preview did not roll back language/settings state");
        checks.Add("Real SettingsWindow cancel rolls back language preview and issues no HTTP");

        VerifyTemporaryTrayLocalization(checks);

        // Ensure this error is rendered in Chinese first, then exercise the real
        // header switch to English (not a no-op selection of the same index).
        UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
        SourceBox.Text = "fail";
        var requestsBeforeFailure = requestCount();
        await TranslateNowAsync();
        if (requestCount() != requestsBeforeFailure + 1 || ResultBox.Text.Length != 0)
            throw new Exception("Injected service failure did not complete exactly one failed request");
        if (!ProvenanceLabel.Text.Contains(UiLocalization.Get("ErrorNoTranslation"), StringComparison.Ordinal))
            throw new Exception("Failure provenance was not rendered");
        UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
        if (requestCount() != requestsBeforeFailure + 1 ||
            !StatusLabel.Text.Contains(UiLocalization.Format("ErrorServerUnavailable", 503), StringComparison.Ordinal) ||
            !ProvenanceLabel.Text.Contains(UiLocalization.Get("ErrorNoTranslation"), StringComparison.Ordinal))
            throw new Exception("English header switch did not relocalize the service error/provenance without HTTP");
        UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
        checks.Add("Service error and provenance relocalize through the real header language selector without HTTP");

        SourceBox.Text = "rate-limit";
        var requestsBeforeRateLimit = requestCount();
        await TranslateNowAsync();
        if (requestCount() != requestsBeforeRateLimit + 1 ||
            _statusKey != "ErrorGoogleRateLimited" || _statusArgs.Length != 2 ||
            !long.TryParse(_statusArgs[1]?.ToString(), out var firstRateLimitSeconds) ||
            firstRateLimitSeconds is < 1 or > 30 ||
            !StatusLabel.Text.Contains("Google 网页接口限制", StringComparison.Ordinal) ||
            StatusLabel.Text.Contains("sensitive", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Google 429 did not render its provider-specific first-error cooldown");
        var requestsAfterRateLimit = requestCount();
        await TranslateNowAsync();
        if (requestCount() != requestsAfterRateLimit ||
            _statusKey != "ErrorGoogleRateLimitCooldown" || _statusArgs.Length != 1 ||
            !long.TryParse(_statusArgs[0]?.ToString(), out var cooldownSeconds) ||
            cooldownSeconds is < 1 or > 30 ||
            !StatusLabel.Text.Contains("Google 网页接口限流", StringComparison.Ordinal))
            throw new Exception("Google local cooldown issued HTTP or was not rendered");
        UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
        if (requestCount() != requestsAfterRateLimit ||
            _statusKey != "ErrorGoogleRateLimitCooldown" ||
            !StatusLabel.Text.Contains("Google's web-endpoint rate limit", StringComparison.Ordinal) ||
            StatusLabel.Text.Contains("sensitive", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Google cooldown did not relocalize without HTTP");
        UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
        checks.Add("Google 429 shows provider-specific cooldown, blocks repeat HTTP, and relocalizes without HTTP");

        SetStatus("StatusCaptureFailed");
        UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
        if (StatusLabel.Text != UiLocalization.Get("StatusCaptureFailed"))
            throw new Exception("Capture failure status did not localize to English");
        UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
        if (StatusLabel.Text != UiLocalization.Get("StatusCaptureFailed"))
            throw new Exception("Capture failure status did not restore to Chinese");
        checks.Add("Capture error status survives real language switching");

        VerifyAutoHideBoundaries(checks);
    }

    private async Task RunSettingsDialogAsync(Action<SettingsWindow> interaction)
    {
        SettingsWindow? dialog = null;
        Exception? callbackError = null;
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        watchdog.Tick += (_, _) =>
        {
            callbackError ??= new TimeoutException("Settings dialog automation timed out");
            watchdog.Stop();
            try
            {
                dialog?.Close();
                foreach (Window window in Application.Current.Windows)
                    if (window is SettingsWindow settings && settings.IsVisible) settings.Close();
            }
            catch { }
        };
        watchdog.Start();

        var callback = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                dialog = Application.Current.Windows.OfType<SettingsWindow>().LastOrDefault();
                if (dialog is null) throw new InvalidOperationException("SettingsWindow was not opened");
                interaction(dialog);
            }
            catch (Exception error)
            {
                callbackError = error;
                try
                {
                    if (dialog is not null && dialog.IsVisible) dialog.DialogResult = false;
                    else dialog?.Close();
                }
                catch { }
            }
        }), DispatcherPriority.Input);

        try
        {
            // ShowDialog enters WPF's nested dispatcher. The callback above runs
            // inside that loop, clicks a real button, and closes the dialog.
            Settings_Click(this, new RoutedEventArgs());
            if (!callback.Task.IsCompleted) await callback.Task;
        }
        finally { watchdog.Stop(); }
        if (callbackError is not null) ExceptionDispatchInfo.Capture(callbackError).Throw();
    }

    private void VerifyTemporaryTrayLocalization(List<string> checks)
    {
        var oldTray = _tray;
        var oldOpen = _trayOpenItem;
        var oldSettings = _traySettingsItem;
        var oldExit = _trayExitItem;
        var oldPreference = UiLocalization.Preference;
        using var menu = new Forms.ContextMenuStrip();
        using var tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Visible = false };
        var open = new Forms.ToolStripMenuItem();
        var settings = new Forms.ToolStripMenuItem();
        var exit = new Forms.ToolStripMenuItem();
        menu.Items.Add(open);
        menu.Items.Add(settings);
        menu.Items.Add(exit);
        tray.ContextMenuStrip = menu;
        _tray = tray;
        _trayOpenItem = open;
        _traySettingsItem = settings;
        _trayExitItem = exit;
        try
        {
            UiLocalization.SetPreference(UiLanguage.English);
            Localize();
            if (open.Text != UiLocalization.Get("TrayOpen") || settings.Text != UiLocalization.Get("TraySettings") || exit.Text != UiLocalization.Get("TrayExit"))
                throw new Exception("English temporary tray menu was not localized");
            UiLocalization.SetPreference(UiLanguage.SimplifiedChinese);
            Localize();
            if (open.Text != UiLocalization.Get("TrayOpen") || settings.Text != UiLocalization.Get("TraySettings") || exit.Text != UiLocalization.Get("TrayExit"))
                throw new Exception("Chinese temporary tray menu was not localized");
            checks.Add("Hidden temporary tray menu follows the production Localize path without showing or registering hotkeys");
        }
        finally
        {
            _tray = oldTray;
            _trayOpenItem = oldOpen;
            _traySettingsItem = oldSettings;
            _trayExitItem = oldExit;
            UiLocalization.SetPreference(oldPreference);
            Localize();
        }
    }

    private void VerifyAutoHideBoundaries(List<string> checks)
    {
        var oldFromCapture = _fromCapture;
        var oldPinned = _pinned;
        var oldSettingsOpen = _settingsOpen;
        var oldCapturing = _capturing;
        var oldPending = _pending;
        var oldAutoHide = _settings.AutoHideSelectionPopup;
        using var pending = new CancellationTokenSource();
        try
        {
            ShowForUser(true);
            _settings.AutoHideSelectionPopup = true;
            _pinned = false;
            _settingsOpen = false;
            _capturing = false;
            _pending = null;
            if (!ShouldAutoHide(false)) throw new Exception("Expected capture popup to auto-hide");
            _settingsOpen = true;
            if (ShouldAutoHide(false)) throw new Exception("Settings dialog boundary did not suppress auto-hide");
            _settingsOpen = false;
            _capturing = true;
            if (ShouldAutoHide(false)) throw new Exception("Capture-in-progress boundary did not suppress auto-hide");
            _capturing = false;
            _pending = pending;
            if (ShouldAutoHide(false)) throw new Exception("Pending translation boundary did not suppress auto-hide");
            checks.Add("Auto-hide honors pending/settings/capturing boundaries");
        }
        finally
        {
            _fromCapture = oldFromCapture;
            _pinned = oldPinned;
            _settingsOpen = oldSettingsOpen;
            _capturing = oldCapturing;
            _pending = oldPending;
            _settings.AutoHideSelectionPopup = oldAutoHide;
        }
    }
}
