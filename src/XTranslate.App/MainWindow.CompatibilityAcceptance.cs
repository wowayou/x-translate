using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace XTranslate.App;

public partial class MainWindow
{
    private readonly List<object> _compatibilityMonitors = new();

    // Fixed fixtures and this invocation's smoke-data only. Does not change display/OS settings,
    // register a user's hotkey, inspect another application or touch the clipboard.
    private async Task RunCompatibilityAcceptanceAsync(List<string> checks, Func<int> requests)
    {
        var saved = _settings.Copy();
        var requestCount = requests();
        try
        {
            ShowForUser(false);
            SourceBox.Text = "Compatibility fixture";
            ResultBox.Text = "中文 / العربية / עברית / 日本語 / 한글 / 😀 / e\u0301\n<plain & text>";
            var paragraph = (Paragraph)ResultBox.Document.Blocks.FirstBlock;
            var run = (Run)paragraph.Inlines.FirstInline;
            RequireAppearance(ResultBox.Document.FontFamily.Source == ResultBox.FontFamily.Source &&
                paragraph.FontFamily.Source == ResultBox.FontFamily.Source && run.FontFamily.Source == ResultBox.FontFamily.Source,
                $"Output typography does not reach its text: box={ResultBox.FontFamily}, document={ResultBox.Document.FontFamily}, paragraph={paragraph.FontFamily}, run={run.FontFamily}");
            RequireAppearance(run.Text == ResultBox.Text && InputMethodEnabledForSource(), "Unicode output or source input methods changed");
            RequireAppearance(!AllowsTransparency && WindowStyle == WindowStyle.SingleBorderWindow && ResizeMode == ResizeMode.CanResize,
                "Native window frame, resizing or non-layered rendering was lost");
            checks.Add("Unicode/multiline output remains plain text with the selected font; source input methods and native resizable window frame are retained");
            VerifyLegacyAppearanceDefaults();
            checks.Add("0.3-style settings default to system appearance/enabled hotkeys without rewriting on load; saving the new preferences preserves services, target language and encrypted credentials");
            var backdropDisabled = AppContext.TryGetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", out var disabled) && disabled;
            if (backdropDisabled || !UiAppearance.SupportsBackdrop || SystemParameters.HighContrast || SystemParameters.IsRemoteSession)
            {
                var appearance = UiAppearance.Preference;
                try
                {
                    foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
                    {
                        UiAppearance.Apply(theme);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        RequireAppearance(Background is SolidColorBrush background && background.Color.A == 255,
                            "Backdrop-disabled/unsupported window did not use an opaque background");
                    }
                    checks.Add("Light/dark windows use opaque backgrounds when the runtime disables the backdrop or the environment cannot provide it");
                }
                finally { UiAppearance.Apply(appearance); }
            }

            // An existing settings file locked against replacement exercises the actual atomic-save failure.
            // The old file must remain intact; the dialog and tray must not pretend the change was saved.
            _storage.Save(_settings);
            var path = Path.Combine(_storage.DirectoryPath, "settings.json");
            var before = File.ReadAllBytes(path);
            var oldTheme = UiAppearance.Preference;
            var oldEnabled = _settings.CaptureHotkeyEnabled;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await RunSettingsDialogAsync(dialog =>
                {
                    ((ComboBox)dialog.FindName("ThemeBox")).SelectedIndex = (int)(oldTheme == UiTheme.Dark ? UiTheme.Light : UiTheme.Dark);
                    ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    RequireAppearance(dialog.IsVisible && dialog.DialogResult != true &&
                        ((TextBlock)dialog.FindName("ErrorLabel")).Text == UiLocalization.Get("SettingsSaveError"),
                        "A failed settings save closed the dialog or lacked an actionable error");
                    CancelStreamingSettingsDialog(dialog);
                });
                RequireAppearance(UiAppearance.Preference == oldTheme && File.ReadAllBytes(path).SequenceEqual(before),
                    "Failed settings save/Cancel changed persisted settings or retained its preview");
                SetCaptureHotkeyEnabled(!oldEnabled);
                RequireAppearance(_settings.CaptureHotkeyEnabled == oldEnabled && _trayMenu!.Hotkeys.IsChecked == oldEnabled &&
                    _statusKey == "StatusSaveFailed" && File.ReadAllBytes(path).SequenceEqual(before),
                    "Tray reported an unsaved shortcut change as successful");
            }
            checks.Add("Locked settings refuse atomic replacement, keep the old file and show a save error; Cancel restores appearance and tray shortcut changes roll back");

            if (Environment.GetCommandLineArgs().Contains("--native-checks"))
            {
                await VerifyPhysicalMonitorsAsync(checks);
                await VerifyHiddenTrayMenuAsync(checks);
            }
            RequireAppearance(requests() == requestCount, "Compatibility checks issued HTTP");
        }
        finally
        {
            if (_trayMenu is not null) _trayMenu.Menu.IsOpen = false;
            RestoreSmokeSettings(saved);
            UiAppearance.Apply(saved.Theme);
            _storage.Save(_settings);
            SourceBox.Clear();
            Localize();
        }
    }

    private void VerifyLegacyAppearanceDefaults()
    {
        var folder = Path.Combine(_storage.DirectoryPath, "legacy-appearance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new AppStorage(folder);
            var legacy = new UserSettings { UiLanguage = UiLanguage.English, CaptureHotkey = "Ctrl+Alt+F10" };
            legacy.EnsureAiService();
            legacy.Translation.TargetLanguage = "es";
            legacy.ActiveAiService!.ApiKey = "fixed-local-compatibility-key";
            storage.Save(legacy);
            var path = Path.Combine(folder, "settings.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json.Remove("Theme");
            json.Remove("CaptureHotkeyEnabled");
            File.WriteAllText(path, json.ToJsonString());
            var original = File.ReadAllBytes(path);
            var loaded = storage.Load();
            RequireAppearance(loaded.Theme == UiTheme.System && loaded.CaptureHotkeyEnabled &&
                loaded.UiLanguage == UiLanguage.English && loaded.CaptureHotkey == "Ctrl+Alt+F10" &&
                loaded.ActiveAiServiceId == legacy.ActiveAiServiceId && loaded.ActiveAiService!.ApiKey == "fixed-local-compatibility-key" &&
                File.ReadAllBytes(path).SequenceEqual(original), "Loading 0.3-style settings lost preferences/credentials or rewrote the file");
            loaded.Theme = UiTheme.Dark;
            loaded.CaptureHotkeyEnabled = false;
            storage.Save(loaded);
            var reopened = storage.Load();
            RequireAppearance(reopened.Theme == UiTheme.Dark && !reopened.CaptureHotkeyEnabled && reopened.Translation.TargetLanguage == "es" &&
                reopened.ActiveAiServiceId == legacy.ActiveAiServiceId && reopened.ActiveAiService!.ApiKey == "fixed-local-compatibility-key" &&
                !File.ReadAllText(path).Contains("fixed-local-compatibility-key", StringComparison.Ordinal),
                "Saving new appearance/hotkey preferences lost legacy services or exposed credentials");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private bool InputMethodEnabledForSource() => System.Windows.Input.InputMethod.GetIsInputMethodEnabled(SourceBox) &&
        !System.Windows.Input.InputMethod.GetIsInputMethodEnabled(ResultBox);

    private async Task VerifyPhysicalMonitorsAsync(List<string> checks)
    {
        var monitors = new List<NativeMethods.MonitorInfo>();
        NativeMethods.MonitorEnumProc collect = (IntPtr monitor, IntPtr dc, ref NativeMethods.Rect bounds, IntPtr data) =>
        {
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info)) monitors.Add(info);
            return true;
        };
        RequireAppearance(NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero) && monitors.Count > 0,
            "Could not enumerate attached monitors");
        var originalWidth = Width;
        var originalHeight = Height;
        NativeMethods.GetWindowRect(_handle, out var originalBounds);
        var settings = new SettingsWindow(_settings, _storage) { Owner = this, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            settings.Show();
            // Return to the first monitor too: this catches a stale maximum size after a DPI change.
            foreach (var monitor in monitors.Concat(monitors.Take(1)))
            {
                foreach (var window in new Window[] { this, settings })
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    RequireAppearance(NativeMethods.SetWindowPos(handle, IntPtr.Zero, monitor.Work.Left, monitor.Work.Top, 0, 0, 0x0015),
                        "Moving an owned test window failed");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var dpi = NativeMethods.GetDpiForWindow(handle);
                    RequireAppearance(dpi > 0 && Math.Abs(VisualTreeHelper.GetDpi(window).PixelsPerInchX - dpi) < 1,
                        "WPF and native window DPI disagree after monitor movement");
                    var limits = WindowPlacement.WorkAreaInDips(monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top, dpi);
                    RequireAppearance(Math.Abs(window.MaxWidth - limits.Width) < 1 && Math.Abs(window.MaxHeight - limits.Height) < 1,
                        $"Monitor movement retained the previous monitor's work-area limits: window={window.GetType().Name}, dpi={dpi}, max={window.MaxWidth}x{window.MaxHeight}, expected={limits.Width}x{limits.Height}, workOrigin={monitor.Work.Left},{monitor.Work.Top}");
                    // Enlarge to the actual monitor limits; the viewport must remain reachable.
                    window.Width = limits.Width;
                    window.Height = limits.Height;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    RequireAppearance(NativeMethods.GetWindowRect(handle, out var rectangle) &&
                        rectangle.Right - rectangle.Left <= monitor.Work.Right - monitor.Work.Left + 2 &&
                        rectangle.Bottom - rectangle.Top <= monitor.Work.Bottom - monitor.Work.Top + 2,
                        "Window exceeds the physical monitor's work area");
                    RequireAppearance(window == this ? SourceBox.ActualHeight >= 60 && ResultBox.ActualHeight >= 60 :
                        ((Button)settings.FindName("SaveButton")).ActualHeight >= 30,
                        "Monitor transition clipped the main controls");
                    _compatibilityMonitors.Add(new { window = window == this ? "main" : "settings", dpi,
                        work = new { left = monitor.Work.Left, top = monitor.Work.Top, width = monitor.Work.Right - monitor.Work.Left, height = monitor.Work.Bottom - monitor.Work.Top },
                        widthDips = window.ActualWidth, heightDips = window.ActualHeight });
                }
            }
            checks.Add($"Actual native main/settings windows moved across {monitors.Count} attached monitor(s) and back; physical work-area bounds and WPF/native DPI agree (no display settings changed)");
        }
        finally
        {
            settings.Close();
            NativeMethods.SetWindowPos(_handle, IntPtr.Zero, originalBounds.Left, originalBounds.Top, 0, 0, 0x0015);
            Width = originalWidth;
            Height = originalHeight;
        }
    }

    private async Task VerifyHiddenTrayMenuAsync(List<string> checks)
    {
        // A second owned window acts as the outside-click/focus destination. No user window is activated.
        var fixture = new Window { Title = "XTranslate tray acceptance fixture", Content = new TextBlock { Text = "Fixed local fixture", Margin = new Thickness(20) },
            Width = 280, Height = 160, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            fixture.Show();
            Hide();
            _trayMenu!.Show(this);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            RequireAppearance(_trayMenu.Menu.IsVisible && !IsVisible && _trayMenu.Open.IsKeyboardFocused,
                "Opening the tray menu from a hidden window did not focus the menu or unexpectedly showed the main window");
            fixture.Activate();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            RequireAppearance(!_trayMenu.Menu.IsOpen, "Tray menu from a hidden owner did not dismiss when another test window was activated");
            checks.Add("Production tray menu opens with the main window hidden, takes keyboard focus and dismisses on native activation of another owned fixture window");
        }
        finally
        {
            _trayMenu!.Menu.IsOpen = false;
            fixture.Close();
            ShowForUser(false);
        }
    }
}
