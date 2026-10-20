using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private int _appearanceNativeCaptures;

    // Fixed fixture text, mock HTTP and smoke-data only. Does not change Windows appearance, DPI,
    // accent colors or hotkeys. Native crops, when allowed, cover only this foreground test window.
    private async Task RunAppearanceAcceptanceAsync(List<string> checks, string output, Func<int> requests)
    {
        var original = _settings.Copy();
        var theme = UiAppearance.Preference;
        var language = UiLocalization.Preference;
        var width = Width;
        var height = Height;
        var pin = _pinned;
        try
        {
            ShowForUser(false);
            SelectProvider(ProviderKind.GoogleWeb);
            SourceBox.Text = "Keep the translation close to your work.";
            ResultBox.Text = "让翻译贴近你的工作。";
            CopyButton.IsEnabled = true;
            _resultProvider = "Google";
            _resultEndpoint = "Google Web（非官方）";
            _resultSource = "en";
            _resultTarget = "zh-CN";
            _resultDuration = 42;
            SetStatus("StatusCompleted");
            RefreshProvenance();
            SourceBox.Select(5, 11);
            var generation = _generation;
            var requestCount = requests();
            var source = SourceBox.Text;
            var result = ResultBox.Text;
            var beforeProvider = ProviderBox.SelectedItem;
            Width = 620;
            Height = 660;

            foreach (var selected in new[] { UiTheme.Light, UiTheme.Dark })
            {
                UiAppearance.Apply(selected);
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                UpdateLayout();
                RequireAppearance(SourceBox.Text == source && ResultBox.Text == result && SourceBox.SelectionStart == 5 && SourceBox.SelectionLength == 11 &&
                    ProviderBox.SelectedItem == beforeProvider && _generation == generation && requests() == requestCount && CopyButton.IsEnabled,
                    "Changing theme reset translation state, selection or issued HTTP");
                var canvas = ((SolidColorBrush)FindResource("ApplicationBackgroundBrush")).Color;
                RequireAppearance(SourceBox.FontFamily.Source.StartsWith("Microsoft YaHei UI", StringComparison.Ordinal) &&
                    ResultBox.FontFamily.Source.StartsWith("Microsoft YaHei UI", StringComparison.Ordinal) &&
                    ResultLabel.FontFamily.Source.StartsWith("Microsoft YaHei UI", StringComparison.Ordinal),
                    $"Typography inheritance changed: window={FontFamily.Source}; source={SourceBox.FontFamily.Source}; result={ResultBox.FontFamily.Source}; label={ResultLabel.FontFamily.Source}");
                var text = Composite(((SolidColorBrush)FindResource("TextFillColorPrimaryBrush")).Color, canvas);
                RequireAppearance(AccentForegroundConverter.Contrast(text, canvas) >= 4.5, "Body contrast fell below AA");
                RequireAppearance(selected == UiTheme.Dark ? AccentForegroundConverter.Luminance(canvas) < 0.1 : AccentForegroundConverter.Luminance(canvas) > 0.85,
                    "The selected appearance did not load its palette");
                var action = ((SolidColorBrush)TranslateButton.Background).Color;
                var actionText = ((SolidColorBrush)TranslateButton.Foreground).Color;
                RequireAppearance(AccentForegroundConverter.Contrast(actionText, action) >= 4.5, "System accent made the main action unreadable");
                foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                    RenderWindowTo(this, Path.ChangeExtension(output, $".appearance.{selected.ToString().ToLowerInvariant()}.{scale * 100:0}.png"), scale);
                if (Environment.GetCommandLineArgs().Contains("--native-checks"))
                {
                    await Task.Delay(120); // Let DWM present after the dictionary switch; not an application animation.
                    if (CaptureTestWindow(this, Path.ChangeExtension(output, $".appearance.{selected.ToString().ToLowerInvariant()}.native.png")))
                        _appearanceNativeCaptures++;
                }
            }
            checks.Add("Light/dark palettes preserve text, selection, result, provider and generation; AA body/action contrast; no HTTP");

            var resultPeer = new RichTextBoxAutomationPeer(ResultBox);
            var textProvider = (ITextProvider)resultPeer.GetPattern(PatternInterface.Text)!;
            RequireAppearance(InputMethod.GetIsInputMethodEnabled(SourceBox) && !InputMethod.GetIsInputMethodEnabled(ResultBox) &&
                textProvider.DocumentRange.GetText(-1).Contains(result, StringComparison.Ordinal),
                "The read-only result must keep its accessible text while source IME stays enabled");
            checks.Add("Read-only output keeps text automation while source text retains IME support");
            ResultBox.Text = "selected text";
            var run = ((System.Windows.Documents.Paragraph)ResultBox.Document.Blocks.FirstBlock).Inlines.FirstInline as System.Windows.Documents.Run;
            RequireAppearance(run is not null, "Output is no longer a single plain-text run");
            ResultBox.Selection.Select(run!.ContentStart, run.ContentStart.GetPositionAtOffset(8)!);
            ResultBox.AppendText(" plus more");
            RequireAppearance(ResultBox.Selection.Text == "selected" && ResultBox.Text == "selected text plus more",
                "Appending output moved the user's existing selection");
            ResultBox.Text = "<b>plain text</b>";
            RequireAppearance(textProvider.DocumentRange.GetText(-1).Contains("<b>plain text</b>", StringComparison.Ordinal),
                "Output was interpreted as markup");
            ResultBox.FlowDirection = FlowDirection.RightToLeft;
            RequireAppearance(ResultBox.Document.FlowDirection == FlowDirection.RightToLeft &&
                ((System.Windows.Documents.Paragraph)ResultBox.Document.Blocks.FirstBlock).TextAlignment == TextAlignment.Right,
                "Right-to-left output lost its direction or alignment");
            ResultBox.Text = "مرحباً بالعالم";
            UpdateLayout();
            RenderWindowTo(this, Path.ChangeExtension(output, ".output.rtl.png"));
            ResultBox.FlowDirection = FlowDirection.LeftToRight;
            ResultBox.Text = result;
            checks.Add("Plain output is never parsed as markup; append preserves selection; RTL output mirrors direction/alignment");
            foreach (var accent in new[] { Colors.Black, Colors.White, Colors.Yellow, Colors.Cyan, Colors.Blue, Colors.Red,
                Color.FromRgb(0, 159, 170), Color.FromRgb(127, 127, 127) })
                RequireAppearance(AccentForegroundConverter.Contrast(AccentForegroundConverter.BestForeground(accent), accent) >= 4.5,
                    "A bright/dark user accent has unreadable text");
            // Load the system high-contrast resource contract without toggling the user's OS settings.
            var highContrast = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Resources/Theme/HC.xaml", UriKind.Absolute)
            };
            foreach (var key in new[] { "WindowBackground", "TextFillColorPrimaryBrush", "CardStrokeColorDefaultBrush", "KeyboardFocusBorderColorBrush" })
                RequireAppearance(highContrast[key] is Brush, "High contrast resource missing: " + key);
            checks.Add("Bright/dark accent foregrounds remain AA; system high-contrast text/card/focus resources exist (OS settings untouched)");

            UiAppearance.Apply(UiTheme.Light);
            _settings.Theme = UiTheme.Light;
            _storage.Save(_settings);
            await RunSettingsDialogAsync(dialog =>
            {
                var box = (ComboBox)dialog.FindName("ThemeBox");
                box.SelectedIndex = (int)UiTheme.Dark;
                RequireAppearance(UiAppearance.Preference == UiTheme.Dark && _storage.Load().Theme == UiTheme.Light,
                    "Theme preview did not apply immediately or wrote settings prematurely");
                CancelStreamingSettingsDialog(dialog);
            });
            RequireAppearance(UiAppearance.Preference == UiTheme.Light && _storage.Load().Theme == UiTheme.Light, "Cancel failed to restore the original theme");
            await RunSettingsDialogAsync(dialog =>
            {
                ((ComboBox)dialog.FindName("ThemeBox")).SelectedIndex = (int)UiTheme.Dark;
                ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            RequireAppearance(UiAppearance.Preference == UiTheme.Dark && _storage.Load().Theme == UiTheme.Dark && SourceBox.Text == source && ResultBox.Text == result,
                "Saving the appearance lost the translation or failed to persist");
            checks.Add("Theme preview is local to the dialog until Save; Cancel restores the old look; reopening restores the saved preference");

            var pinPeer = new ToggleButtonAutomationPeer(PinButton);
            var toggle = (IToggleProvider)pinPeer.GetPattern(PatternInterface.Toggle)!;
            if (PinButton.IsChecked == true) toggle.Toggle();
            toggle.Toggle();
            RequireAppearance(_pinned && Topmost && PinButton.IsChecked == true && !ShouldAutoHide(false) &&
                AutomationProperties.GetName(PinButton) == UiLocalization.Get("Unpin"), "Accessible pin toggle did not set Topmost or its accessible name");
            toggle.Toggle();
            RequireAppearance(!_pinned && !Topmost && PinButton.IsChecked == false, "Accessible unpin left the window locked on top");
            RequireAppearance(AutomationProperties.GetName(SettingsButton) == UiLocalization.Get("Settings") &&
                AutomationProperties.GetLabeledBy(SourceBox) == SourceLabel && AutomationProperties.GetLabeledBy(ResultBox) == ResultLabel,
                "Icons or translation fields lost accessible labels");
            checks.Add("Pin uses the Toggle automation pattern; checked state/Topmost/name agree; text fields and icon buttons have accessible labels");

            var oldEnabled = _settings.CaptureHotkeyEnabled;
            var oldRegistered = _hotkeyRegistered;
            var oldHotkeys = _hotkeys;
            var oldText = _hotkeyText;
            var releases = new List<int>();
            try
            {
                // Real policy with a fake Win32 boundary: do not steal a user's globally registered key.
                _hotkeys = new HotkeyRegistration(701, 702, (_, _, _) => true, id => releases.Add(id));
                _settings.CaptureHotkeyEnabled = true;
                RegisterShortcut();
                RequireAppearance(_hotkeyRegistered, "Enabled shortcut was not registered");
                _settings.CaptureHotkeyEnabled = false;
                RegisterShortcut();
                RequireAppearance(!_hotkeyRegistered && _hotkeys is null && releases.SequenceEqual(new[] { 701 }) &&
                    HotkeyLabel.Text == UiLocalization.Get("HotkeyDisabledFooter"), "Disabling did not release the registration");
                _hotkeys = new HotkeyRegistration(703, 704, (_, _, _) => true, id => releases.Add(id));
                _settings.CaptureHotkeyEnabled = true;
                RegisterShortcut();
                RequireAppearance(_hotkeyRegistered && _statusKey != "StatusHotkeyDisabled", "Re-enabling retained the paused state");
            }
            finally
            {
                _hotkeys?.Dispose();
                _hotkeys = oldHotkeys;
                _hotkeyRegistered = oldRegistered;
                _hotkeyText = oldText;
                _settings.CaptureHotkeyEnabled = oldEnabled;
            }
            _trayMenu!.Refresh(oldEnabled, oldRegistered, oldText);
            _trayMenu.Hotkeys.IsChecked = !oldEnabled;
            _trayMenu.Hotkeys.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            RequireAppearance(_storage.Load().CaptureHotkeyEnabled == !oldEnabled, "Tray shortcut toggle was not persisted");
            _trayMenu.Hotkeys.IsChecked = oldEnabled;
            _trayMenu.Hotkeys.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            RequireAppearance(_settings.CaptureHotkeyEnabled == oldEnabled && requests() == requestCount, "Tray action sent a translation");
            checks.Add("Shortcut disable releases registration; re-enable restores it; tray state persists without translating (mock Win32 boundary)");

            // Use a deterministic position instead of moving the real mouse into the tray.
            var menu = _trayMenu.Menu;
            menu.PlacementTarget = SettingsButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            menu.UpdateLayout();
            RequireAppearance(menu.IsVisible && _trayMenu.Open.IsKeyboardFocused, "Tray menu did not receive keyboard focus");
            await Task.Delay(200); // The stock Fluent menu has a 167 ms entrance animation; capture its final state.
            RenderElementTo(menu, Path.ChangeExtension(output, ".tray.dark.png"));
            UiAppearance.Apply(UiTheme.Light);
            await Dispatcher.Yield(DispatcherPriority.Render);
            RenderElementTo(menu, Path.ChangeExtension(output, ".tray.light.png"));
            var menuSource = PresentationSource.FromVisual(menu)!;
            menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, menuSource, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.Input);
            RequireAppearance(!menu.IsOpen && IsVisible, "Escape did not dismiss only the menu");
            menu.Placement = PlacementMode.MousePoint;
            checks.Add("Fluent tray menu opens with keyboard focus, follows theme changes and dismisses with Escape without hiding the main window");

            foreach (var selected in new[] { UiTheme.Light, UiTheme.Dark })
            {
                UiAppearance.Apply(selected);
                Width = MinWidth;
                Height = MinHeight;
                UpdateLayout();
                RenderWindowTo(this, Path.ChangeExtension(output, $".minimum.{selected.ToString().ToLowerInvariant()}.png"));
                RequireAppearance(SourceBox.ActualHeight >= 60 && ResultBox.ActualHeight >= 60 &&
                    TranslateButton.ActualHeight >= 30 && UiLanguageBox.ActualWidth >= 110,
                    $"Compact layout squeezed out a control: source={SourceBox.ActualHeight:0.0}, result={ResultBox.ActualHeight:0.0}, action={TranslateButton.ActualHeight:0.0}, language={UiLanguageBox.ActualWidth:0.0}, window={ActualWidth:0}x{ActualHeight:0}");
                await RunSettingsDialogAsync(dialog =>
                {
                    foreach (var ui in new[] { UiLanguage.SimplifiedChinese, UiLanguage.English })
                    {
                        ((ComboBox)dialog.FindName("UiLanguageBox")).SelectedIndex = (int)ui;
                        dialog.Width = 800;
                        dialog.Height = 680;
                        dialog.Reveal((FrameworkElement)dialog.FindName("ThemeBox"));
                        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                            RenderWindowTo(dialog, Path.ChangeExtension(output,
                                $".settings.{selected.ToString().ToLowerInvariant()}.{(ui == UiLanguage.English ? "en" : "zh")}.{scale * 100:0}.png"), scale);
                    }
                    dialog.Width = dialog.MinWidth;
                    dialog.Height = dialog.MinHeight;
                    dialog.Reveal((FrameworkElement)dialog.FindName("ThemeBox"));
                    RequireAppearance(((Button)dialog.FindName("SaveButton")).IsVisible && ((ComboBox)dialog.FindName("ThemeBox")).ActualWidth > 150,
                        "Minimum settings size lost its primary controls");
                    RenderWindowTo(dialog, Path.ChangeExtension(output, $".settings.minimum.{selected.ToString().ToLowerInvariant()}.png"));
                    CancelStreamingSettingsDialog(dialog);
                });
            }
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                var area = WindowPlacement.WorkAreaInDips(1920, 1040, dpi);
                RequireAppearance(Math.Abs(area.Height * dpi / 96 - 1040) < 0.1, "Monitor limits mixed pixels and DIPs");
            }
            checks.Add("Main/settings minimum layouts remain usable; zh/en light/dark 100/150/200 percent raster snapshots; monitor bounds convert pixels to DIPs");
        }
        finally
        {
            if (_trayMenu is not null) _trayMenu.Menu.IsOpen = false;
            PinButton.IsChecked = pin;
            Width = width;
            Height = height;
            RestoreSmokeSettings(original);
            UiLocalization.SetPreference(language);
            UiAppearance.Apply(theme);
            _storage.Save(_settings);
            SourceBox.Clear();
            Localize();
        }
    }

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255.0;
        return Color.FromRgb((byte)(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)(foreground.G * alpha + background.G * (1 - alpha)), (byte)(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static void RequireAppearance(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeMethods.Rect value, int size);

    private static bool CaptureTestWindow(Window window, string path)
    {
        var handle = new WindowInteropHelper(window).Handle;
        // Never capture a region occupied by another foreground application.
        if (NativeMethods.GetForegroundWindow() != handle || !window.IsVisible ||
            DwmGetWindowAttribute(handle, 9 /* EXTENDED_FRAME_BOUNDS */, out var bounds, Marshal.SizeOf<NativeMethods.Rect>()) != 0) return false;
        var w = bounds.Right - bounds.Left;
        var h = bounds.Bottom - bounds.Top;
        if (w <= 0 || h <= 0) return false;
        using var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return true;
    }
}
