using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private async Task RunCapturePolicyAcceptanceAsync(List<string> checks, string output, Func<int> requests)
    {
        var original = _settings.Copy(); var originalService = _service;
        var language = UiLocalization.Preference; var theme = UiAppearance.Preference;
        var initialRequests = requests();
        using var handler = new SmokeHandler(); using var client = new HttpClient(handler);
        _service = new TranslationService(client);
        try
        {
            _settings.CaptureExcludedApps = ""; _storage.Save(_settings);
            await RunSettingsDialogAsync(dialog =>
            {
                dialog.ExcludedAppsBox.Text = " Chrome.EXE \r\nchrome\nCode - Insiders.exe";
                foreach (var lang in new[] { UiLanguage.SimplifiedChinese,UiLanguage.English })
                {
                    UiLocalization.SetPreference(lang); UiAppearance.Apply(lang == UiLanguage.English ? UiTheme.Dark : UiTheme.Light);
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                    dialog.Reveal(dialog.ExcludedAppsBox); dialog.UpdateLayout();
                    RequireStream(dialog.ExcludedAppsBox.Text.Contains("Chrome.EXE") && dialog.ExcludedAppsBox.ActualWidth > 150 &&
                        dialog.SaveButton.ActualHeight >= 30,"Exclusion draft/layout lost on localization");
                    RenderWindowTo(dialog,Path.ChangeExtension(output,lang == UiLanguage.English ? ".exclusions.en.dark.png" : ".exclusions.zh.light.png"));
                }
                dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            const string expected = "chrome.exe\ncode - insiders.exe";
            RequireStream(_settings.CaptureExcludedApps == expected && _storage.Load().CaptureExcludedApps == expected,"Rules did not persist canonically");
            var copied = _settings.Copy(); copied.CaptureExcludedApps = "notepad.exe";
            RequireStream(_settings.CaptureExcludedApps == expected,"Snapshot changed saved exclusion rules");
            await RunSettingsDialogAsync(dialog =>
            {
                RequireStream(dialog.ExcludedAppsBox.Text.Replace("\r\n","\n",StringComparison.Ordinal) == expected,"Reopened rules drifted");
                dialog.ExcludedAppsBox.Text = "notepad.exe"; CancelStreamingSettingsDialog(dialog);
            });
            RequireStream(_storage.Load().CaptureExcludedApps == expected,"Cancel changed exclusions");
            checks.Add("Application exclusions save/reopen/cancel canonically; bilingual light/dark minimum settings keep the editable draft and save action");

            var settingsPath = Path.Combine(_storage.DirectoryPath,"settings.json");
            var before = File.ReadAllBytes(settingsPath);
            await RunSettingsDialogAsync(dialog =>
            {
                dialog.ExcludedAppsBox.Text = "chrome.exe\nC:\\private_marker\\app.exe";
                dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequireStream(dialog.IsVisible && dialog.SectionTabs.SelectedItem == dialog.GeneralTab && dialog.ExcludedAppsSection.IsExpanded &&
                    dialog.ErrorLabel.Text == UiLocalization.Get("SettingsExcludedAppsError") && File.ReadAllBytes(settingsPath).SequenceEqual(before),
                    "Invalid rule was dropped or saved");
                dialog.ExcludedAppsBox.Text = "notepad.exe";
                using (var held = new FileStream(settingsPath,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    RequireStream(dialog.IsVisible && dialog.ExcludedAppsBox.Text == "notepad.exe" &&
                        dialog.ErrorLabel.Text == UiLocalization.Get("SettingsSaveError") && File.ReadAllBytes(settingsPath).SequenceEqual(before),
                        "Failed save discarded rules or claimed persistence");
                }
                CancelStreamingSettingsDialog(dialog);
            });
            RequireStream(_settings.CaptureExcludedApps == expected,"Failed settings save mutated active capture rules");
            checks.Add("Invalid paths/wildcards are not silently ignored; settings validation and locked-file failure retain the draft and original rules");

            // Invalid HWND is a fixture only: identity and text boundaries are replaced, never real UIA/clipboard.
            var fixtureWindow = new IntPtr(-42); var reads = 0; var identities = 0;
            Task<CaptureResult> Read(CancellationToken token)
            { reads++; return Task.FromException<CaptureResult>(new OperationCanceledException(token)); }
            _settings.AllowCopyCapture = _settings.AutoTranslateSelection = true;
            SourceBox.Text = "retained manual source"; ResultBox.Text = "retained manual result"; CopyButton.IsEnabled = true;
            HideToTray(); var generation = _generation;
            using (var unrelatedRequest = new CancellationTokenSource())
            {
                _pending = unrelatedRequest; // Fixed in-flight boundary: no request is actually sent.
                try
                {
                    foreach (var name in new string?[] {"CHROME","code - insiders",null,""})
                    {
                        await CaptureSelectionAsync(fixtureWindow,() => { identities++; return name; },Read);
                        RequireStream(reads == 0 && !IsVisible && SourceBox.Text == "retained manual source" && ResultBox.Text == "retained manual result" &&
                            _generation == generation && !_capturing && _capturePending is null && _pending == unrelatedRequest && !unrelatedRequest.IsCancellationRequested,
                            "Excluded/unknown app read text, altered an unrelated request/input or opened a popup");
                    }
                }
                finally { _pending = null; }
            }
            RequireStream(identities == 4,"Configured policy did not inspect identity per trigger");
            _settings.CaptureExcludedApps = "*.exe";
            await CaptureSelectionAsync(fixtureWindow,() => throw new Exception("Invalid policy must not inspect target"),Read);
            RequireStream(reads == 0 && _statusKey == "SettingsExcludedAppsError","Malformed configuration allowed capture");
            _settings.CaptureExcludedApps = expected;
            await CaptureSelectionAsync(fixtureWindow,() => "chromedriver",Read);
            RequireStream(reads == 1 && !_capturing && !IsVisible,"Allowed exact-name miss failed to reach the capture boundary");
            _settings.CaptureExcludedApps = "";
            await CaptureSelectionAsync(fixtureWindow,() => throw new Exception("Empty policy need not inspect target"),Read);
            RequireStream(reads == 2 && handler.RequestCount == 0 && requests() == initialRequests,"Empty policy regressed capture or filter sent HTTP");
            using (var own = Process.GetCurrentProcess())
                RequireStream(CaptureProcessName(_handle) == own.ProcessName && CaptureProcessName(IntPtr.Zero) is null,"Native process identity boundary drifted");
            checks.Add("Production capture gate blocks excluded/unknown/invalid targets before text or clipboard access, preserves hidden/manual state, and leaves allowed/empty-list capture available with zero HTTP");

            var root = Path.Combine(Path.GetDirectoryName(output)!,"capture-policy-fixture"); Directory.CreateDirectory(root);
            var fixtureStorage = new AppStorage(root); var fixturePath = Path.Combine(root,"settings.json");
            foreach (var json in new[] { "null","{\"CaptureExcludedApps\":null}","{\"CaptureExcludedApps\":[]}",
                "{\"CaptureExcludedApps\":\"chrome.exe\",\"CaptureExcludedApps\":\"\"}",
                JsonSerializer.Serialize(new { CaptureExcludedApps = "chrome.exe\n*.exe" }),"{broken" })
            {
                File.WriteAllText(fixturePath,json); var raw = File.ReadAllBytes(fixturePath);
                var loaded = fixtureStorage.Load();
                RequireStream(!loaded.CaptureHotkeyEnabled && fixtureStorage.Warning is not null && !CaptureAppPolicy.TryParse(loaded.CaptureExcludedApps,out _) &&
                    File.ReadAllBytes(fixturePath).SequenceEqual(raw),"Invalid settings silently dropped capture exclusions or rewrote file");
                try { fixtureStorage.Save(loaded); throw new Exception("Invalid rules were saved"); }
                catch (ArgumentException) { }
                RequireStream(File.ReadAllBytes(fixturePath).SequenceEqual(raw),"Failed validation overwrote original settings");
            }
            File.WriteAllText(fixturePath,"{}");
            var legacy = fixtureStorage.Load();
            RequireStream(legacy.CaptureHotkeyEnabled && legacy.CaptureExcludedApps == "" && fixtureStorage.Warning is null,
                "Older settings without rules no longer default to existing capture behavior");
            checks.Add("Malformed/null/wrong-type exclusion settings disable capture without rewriting the original file; legacy missing rules retain empty-list defaults");

            ShowForUser(false); SelectProvider(ProviderKind.GoogleWeb); ReviewSourceBox.IsChecked = false;
            _settings.AutoFallback = false; _settings.CaptureExcludedApps = "xtranslate.exe\nchrome.exe";
            SourceBox.Text = "manual translation fixture";
            await TranslateNowAsync();
            RequireStream(handler.RequestCount == 1 && CopyButton.IsEnabled && ResultBox.Text.Length > 0 && requests() == initialRequests,
                "Capture exclusions disabled manual translation or added requests");
            checks.Add("Capture exclusion does not prevent an explicit manual translation: exactly one simulated request, no automatic retry or service switch");
        }
        finally
        {
            CancelRequest(); _service.Dispose(); _service = originalService;
            RestoreSmokeSettings(original); UiLocalization.SetPreference(language); UiAppearance.Apply(theme);
            Localize(); _storage.Save(_settings); ShowForUser(false); SourceBox.Clear();
        }
    }
}
