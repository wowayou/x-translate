using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTranslate.Core;
using Forms = System.Windows.Forms;

namespace XTranslate.App;

public partial class MainWindow : Window
{
    private readonly AppStorage _storage;
    private UserSettings _settings;
    private TranslationService _service = new();
    private readonly SelectionCapture _capture = new();
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayOpenItem;
    private Forms.ToolStripMenuItem? _traySettingsItem;
    private Forms.ToolStripMenuItem? _trayExitItem;
    private HwndSource? _source;
    private IntPtr _handle;
    private CancellationTokenSource? _pending;
    private CancellationTokenSource? _capturePending;
    private long _generation;
    private bool _ready;
    private bool _allowClose;
    private bool _capturing;
    private bool _settingsOpen;
    private bool _hotkeyRegistered;
    private bool _exiting;
    private bool _pinned;
    private bool _fromCapture;
    private bool _suppressUiLangEvent;
    private Task? _captureTask;
    private string _hotkeyText = "Ctrl+Q";
    private string _statusKey = "";
    private object[] _statusArgs = System.Array.Empty<object>();
    private string? _resultProvider;
    private string? _resultEndpoint;
    private string? _resultModel;
    private string? _resultSource;
    private string? _resultTarget;
    private long _resultDuration;
    private ProviderKind? _errorProviderKind;

    public MainWindow(AppStorage storage)
    {
        _storage = storage;
        _settings = storage.Load();
        UiLocalization.SetPreference(_settings.UiLanguage);
        InitializeComponent();
        var appImage = LoadAppImage();
        Icon = appImage;
        AppIconImage.Source = appImage;
        SelectProvider(_settings.Translation.Provider);
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == _settings.Translation.TargetLanguage) ?? LanguageBox.Items[0];
        _settings.Translation.TargetLanguage = (string)((ComboBoxItem)LanguageBox.SelectedItem).Tag;
        _suppressUiLangEvent = true;
        UiLanguageBox.SelectedIndex = (int)UiLocalization.Preference;
        _suppressUiLangEvent = false;
        _ready = true;

        Localize();
        UiLocalization.Changed += OnLanguageChanged;

        if (!App.IsSmokeTest)
        {
            _trayIcon = AppIcon.CreateTrayIcon();
            _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = UiLocalization.Get("TrayText"), Visible = true };
            var menu = new Forms.ContextMenuStrip();
            _trayOpenItem = new Forms.ToolStripMenuItem(UiLocalization.Get("TrayOpen"));
            _traySettingsItem = new Forms.ToolStripMenuItem(UiLocalization.Get("TraySettings"));
            _trayExitItem = new Forms.ToolStripMenuItem(UiLocalization.Get("TrayExit"));
            _trayOpenItem.Click += (_, _) => Dispatcher.Invoke(() => ShowForUser(false));
            _traySettingsItem.Click += (_, _) => Dispatcher.Invoke(() => Settings_Click(this, new RoutedEventArgs()));
            _trayExitItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);
            menu.Items.Add(_trayOpenItem);
            menu.Items.Add(_traySettingsItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_trayExitItem);
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowForUser(false));
        }
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_handle);
            _source.AddHook(WindowProc);
            if (!App.IsSmokeTest && !App.IsBenchmark) RegisterShortcut();
            if (_storage.Warning is not null) SetStatus("StorageWarning");
        };
        Deactivated += (_, _) =>
        {
            if (!_fromCapture || !_settings.AutoHideSelectionPopup || _pinned || _settingsOpen || _capturing || _pending is not null || _exiting) return;
            // A ComboBox dropdown, context menu or our settings dialog activates another
            // window owned by this process. Never auto-hide for our own popups; only hide
            // when focus genuinely leaves XTranslate. This is the QTranslate/Pot boundary.
            var foreground = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
            if (processId == Environment.ProcessId) return;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var currentProcessId);
                if (ShouldAutoHide(currentProcessId == Environment.ProcessId)) HideToTray();
            }), DispatcherPriority.Background);
        };
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            UiLocalization.Changed -= OnLanguageChanged;
            CancelRequest();
            _capturePending?.Cancel();
            if (_hotkeyRegistered) NativeMethods.UnregisterHotKey(_handle, NativeMethods.HotkeyId);
            _source?.RemoveHook(WindowProc);
            _tray?.Dispose();
            _trayIcon?.Dispose();
            _service.Dispose();
        };
    }

    private static BitmapImage LoadAppImage()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/xtranslate.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Localize();

    // Re-applies every static label from the catalog. Deliberately does not trigger a
    // new request, change the target language, or alter the current translation text.
    private void Localize()
    {
        Title = UiLocalization.Get("WindowTitle");
        TitleText.Text = UiLocalization.Get("WindowTitle");
        TaglineText.Text = UiLocalization.Get("Tagline");
        UiLanguageLabel.Text = UiLocalization.Get("UiLanguageLabel");
        UiAutoItem.Content = UiLocalization.Get("UiAuto");
        UiChineseItem.Content = UiLocalization.Get("UiChinese");
        UiEnglishItem.Content = UiLocalization.Get("UiEnglish");
        _suppressUiLangEvent = true;
        UiLanguageBox.SelectedIndex = (int)UiLocalization.Preference;
        _suppressUiLangEvent = false;
        PinButton.Content = UiLocalization.Get(_pinned ? "Unpin" : "Pin");
        SettingsButton.Content = UiLocalization.Get("Settings");
        ProviderLabel.Text = UiLocalization.Get("ProviderLabel");
        ProviderGoogleItem.Content = UiLocalization.Get("GoogleProvider");
        ProviderDeepLItem.Content = UiLocalization.Get("DeepLProvider");
        ProviderOpenAiItem.Content = UiLocalization.Get("OpenAiProvider");
        ProviderMicrosoftItem.Content = UiLocalization.Get("MicrosoftProviderPlanned");
        ProviderPapagoItem.Content = UiLocalization.Get("PapagoProviderPlanned");
        TargetLabel.Text = UiLocalization.Get("TargetLabel");
        SourceLabel.Text = UiLocalization.Get("SourceLabel");
        PasteButton.Content = UiLocalization.Get("Paste");
        ClearButton.Content = UiLocalization.Get("Clear");
        SourceBox.ToolTip = UiLocalization.Get("SourceToolTip");
        ResultLabel.Text = UiLocalization.Get("ResultLabel");
        ResultBox.ToolTip = UiLocalization.Get("ResultToolTip");
        CopyButton.Content = UiLocalization.Get("Copy");
        TranslateButton.Content = UiLocalization.Get(_pending is null ? "Translate" : "Cancel");
        CancelButton.Content = UiLocalization.Get("Cancel");
        RequestHint.Text = UiLocalization.Get("RequestHint");
        CountLabel.Text = UiLocalization.Format("Count", SourceBox.Text.Length);
        if (!CopyButton.IsEnabled) ProvenanceLabel.Text = UiLocalization.Get("Provenance");
        TrayFooterLabel.Text = UiLocalization.Get("TrayFooter");
        UpdateHotkeyLabels();
        if (_tray is not null)
        {
            _tray.Text = _hotkeyRegistered ? UiLocalization.Format("TrayHotkeyText", _hotkeyText) : UiLocalization.Get("TrayUnavailableText");
            if (_trayOpenItem is not null) _trayOpenItem.Text = UiLocalization.Get("TrayOpen");
            if (_traySettingsItem is not null) _traySettingsItem.Text = UiLocalization.Get("TraySettings");
            if (_trayExitItem is not null) _trayExitItem.Text = UiLocalization.Get("TrayExit");
        }
        RefreshProvenance();
        if (_statusKey.Length > 0)
            StatusLabel.Text = RenderStatus();
        else if (_storage.Warning is null)
            SetStatus("StatusReady", _hotkeyText);
        UpdateInputState();
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        StatusLabel.Text = RenderStatus();
    }

    private string RenderStatus()
    {
        if (_statusArgs.Length == 0) return UiLocalization.Get(_statusKey);
        var args = _statusArgs.ToArray();
        if (_statusKey is "StatusSelectionCaptured" or "StatusSelectionCapturedManual" && args.Length > 0 && args[0] is string method)
            args[0] = CaptureMethodDisplay(method);
        return UiLocalization.Format(_statusKey, args);
    }

    private static string CaptureMethodDisplay(string method) => method switch
    {
        "UI Automation" => UiLocalization.Get("CaptureMethodUia"),
        "兼容取词（已恢复剪贴板）" => UiLocalization.Get("CaptureMethodCopyRestored"),
        "兼容取词" => UiLocalization.Get("CaptureMethodCopy"),
        _ => method
    };

    private string TranslationErrorKey(TranslationException error) => error.Code switch
    {
        TranslationErrorCode.InputEmpty => "ErrorInputEmpty",
        TranslationErrorCode.InputTooLong => "ErrorInputTooLong",
        TranslationErrorCode.UnsupportedProvider => "ErrorUnsupportedProvider",
        TranslationErrorCode.Timeout => "ErrorTimeout",
        TranslationErrorCode.InvalidResponse => "ErrorInvalidResponse",
        TranslationErrorCode.EmptyResponse => "ErrorEmptyResponse",
        TranslationErrorCode.Network => "ErrorNetwork",
        TranslationErrorCode.MissingTargetLanguage => "ErrorMissingTarget",
        TranslationErrorCode.MissingApiKey => "ErrorMissingApiKey",
        TranslationErrorCode.MissingModel => "ErrorMissingModel",
        TranslationErrorCode.InvalidEndpoint => "ErrorInvalidEndpoint",
        TranslationErrorCode.Unauthorized => "ErrorUnauthorized",
        TranslationErrorCode.Forbidden => "ErrorForbidden",
        TranslationErrorCode.RateLimited when _errorProviderKind == ProviderKind.GoogleWeb => "ErrorGoogleRateLimited",
        TranslationErrorCode.RateLimited => "ErrorRateLimited",
        TranslationErrorCode.RateLimitCooldown => "ErrorGoogleRateLimitCooldown",
        TranslationErrorCode.ServerUnavailable => "ErrorServerUnavailable",
        TranslationErrorCode.RequestFailed => "ErrorRequestFailed",
        _ => "ErrorUnknown"
    };

    private static object[] TranslationErrorArguments(TranslationException error)
    {
        if (error.Code == TranslationErrorCode.RateLimitCooldown)
            return new object[] { RetryAfterSeconds(error.RetryAfter) };
        if (error.Code == TranslationErrorCode.RateLimited)
            return new object[] { error.StatusCode ?? 429, RetryAfterSeconds(error.RetryAfter) };
        return new object[] { error.StatusCode ?? 0 };
    }

    private static long RetryAfterSeconds(TimeSpan? retryAfter)
    {
        if (retryAfter is null || retryAfter.Value <= TimeSpan.Zero) return 0;
        return Math.Max(1, (long)Math.Ceiling(retryAfter.Value.TotalSeconds));
    }

    private void UpdateInputState()
    {
        if (_pending is not null)
        {
            TranslateButton.IsEnabled = true;
            return;
        }
        var length = SourceBox.Text.Trim().Length;
        TranslateButton.IsEnabled = length is > 0 and <= 3000;
    }

    private void UpdateHotkeyLabels()
    {
        HotkeyLabel.Text = _hotkeyRegistered ? UiLocalization.Format("HotkeyFooter", _hotkeyText) : UiLocalization.Get("HotkeyUnavailable");
    }

    private void RefreshProvenance()
    {
        if (_resultProvider is not null)
        {
            ProvenanceLabel.Text = FormatProvenance(_resultProvider, _resultEndpoint!, _resultModel, _resultDuration, _resultSource!, _resultTarget!);
            return;
        }
        if (_errorProviderKind is not null)
            ProvenanceLabel.Text = $"{ProviderDisplay(_errorProviderKind.Value)} · {UiLocalization.Get("ErrorNoTranslation")}";
        else if (!CopyButton.IsEnabled)
            ProvenanceLabel.Text = UiLocalization.Get("Provenance");
    }

    private static string FormatProvenance(string provider, string endpoint, string? model, long duration, string source, string target)
    {
        var providerText = provider switch
        {
            "Google" => UiLocalization.Get("GoogleProvider"),
            "DeepL" => UiLocalization.Get("DeepLProvider"),
            _ when provider.StartsWith("OpenAI 兼容 · ", StringComparison.Ordinal) =>
                UiLocalization.Format("OpenAiHost", provider["OpenAI 兼容 · ".Length..]),
            _ => provider
        };
        var endpointText = endpoint switch
        {
            "Google Web（非官方）" => UiLocalization.Get("GoogleEndpoint"),
            "DeepL API" => UiLocalization.Get("DeepLEndpoint"),
            "AI 接口" => UiLocalization.Get("AiEndpoint"),
            _ => endpoint
        };
        return $"{providerText} · {endpointText}" +
               (string.IsNullOrWhiteSpace(model) ? "" : $" · {model}") + $" · {duration} ms · {source} → {target}";
    }

    private static string ProviderDisplay(ProviderKind provider) => provider switch
    {
        ProviderKind.GoogleWeb => UiLocalization.Get("GoogleProvider"),
        ProviderKind.DeepL => UiLocalization.Get("DeepLProvider"),
        ProviderKind.OpenAI => UiLocalization.Get("OpenAiProvider"),
        ProviderKind.MicrosoftTranslator => UiLocalization.Get("MicrosoftProviderPlanned"),
        ProviderKind.Papago => UiLocalization.Get("PapagoProviderPlanned"),
        _ => UiLocalization.Get("ErrorUnsupportedProvider")
    };

    private void SelectProvider(ProviderKind provider)
    {
        ProviderBox.SelectedItem = ProviderBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Enum.TryParse<ProviderKind>(item.Tag?.ToString(), out var value) && value == provider && item.IsEnabled)
            ?? ProviderGoogleItem;
    }

    private static bool TryGetProvider(ComboBoxItem item, out ProviderKind provider) =>
        Enum.TryParse(item.Tag?.ToString(), out provider) && ProviderCatalog.IsAvailable(provider);

    internal bool ShouldAutoHide(bool foregroundIsOwnProcess) =>
        _fromCapture && _settings.AutoHideSelectionPopup && !_pinned && !_settingsOpen && !_capturing &&
        _pending is null && !_exiting && IsVisible && !foregroundIsOwnProcess;

    private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiLangEvent || !_ready || _settingsOpen) return;
        var language = (UiLanguage)Math.Max(0, UiLanguageBox.SelectedIndex);
        _settings.UiLanguage = language;
        UiLocalization.SetPreference(language); // Raises Changed -> Localize on every open window.
        try { _storage.Save(_settings); }
        catch { SetStatus("StatusSaveFailed"); }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == NativeMethods.ShowMessage && !_settingsOpen)
        {
            ShowForUser(false);
            handled = true;
        }
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == NativeMethods.HotkeyId && !_settingsOpen)
        {
            if (!_exiting && !_capturing) _captureTask = CaptureSelectionAsync();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RegisterShortcut()
    {
        if (_hotkeyRegistered) NativeMethods.UnregisterHotKey(_handle, NativeMethods.HotkeyId);
        var alt = _settings.UseCtrlAltQ;
        _hotkeyRegistered = NativeMethods.RegisterHotKey(_handle, NativeMethods.HotkeyId, 0x4002u | (alt ? 1u : 0u), 0x51);
        _hotkeyText = alt ? "Ctrl+Alt+Q" : "Ctrl+Q";
        if (!_hotkeyRegistered && !alt)
        {
            _hotkeyRegistered = NativeMethods.RegisterHotKey(_handle, NativeMethods.HotkeyId, 0x4003, 0x51);
            if (_hotkeyRegistered)
            {
                _hotkeyText = "Ctrl+Alt+Q";
                SetStatus("StatusHotkeyOccupied");
            }
        }
        if (!_hotkeyRegistered) SetStatus("StatusHotkeyUnavailable");
        else if (_statusKey.Length == 0 || _statusKey == "StatusReady") SetStatus("StatusReady", _hotkeyText);
        UpdateHotkeyLabels();
        if (_tray is not null) _tray.Text = _hotkeyRegistered ? UiLocalization.Format("TrayHotkeyText", _hotkeyText) : UiLocalization.Get("TrayUnavailableText");
    }

    internal async Task CaptureSelectionAsync()
    {
        if (_capturing) return;
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId) { ShowForUser(false); return; }
        _capturing = true;
        CancelRequest();
        using var cancellation = new CancellationTokenSource();
        _capturePending = cancellation;
        var watch = Stopwatch.StartNew();
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var captured = await _capture.CaptureAsync(foreground, _handle, _settings.AllowCopyCapture, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            // User navigation takes priority; never open a delayed result over a new task.
            if (NativeMethods.GetForegroundWindow() != foreground)
            {
                _storage.Log(id, "capture", captured.Method, watch.ElapsedMilliseconds, "foreground_changed");
                return;
            }
            ShowForUser(true);
            NativeMethods.PositionNearCursor(_handle);
            SourceBox.Text = captured.Text ?? "";
            _storage.Log(id, "capture", captured.Method, watch.ElapsedMilliseconds, captured.Text is null ? "no_selection" : "success");
            if (captured.Text is null) { SetStatus(CaptureErrorKey(captured)); return; }
            SetStatus("StatusSelectionCaptured", captured.Method, watch.ElapsedMilliseconds);
            if (_settings.AutoTranslateSelection) await TranslateNowAsync();
            else { TranslateButton.Focus(); SetStatus("StatusSelectionCapturedManual", captured.Method, watch.ElapsedMilliseconds); }
        }
        catch (OperationCanceledException) { _storage.Log(id, "capture", "selection", watch.ElapsedMilliseconds, "cancelled"); }
        catch { ShowForUser(true); SetStatus("StatusCaptureFailed"); }
        finally { _capturePending = null; _capturing = false; }
    }

    public void ShowForUser() => ShowForUser(false);

    public void ShowForUser(bool fromCapture)
    {
        _fromCapture = fromCapture;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        _fromCapture = false;
        CancelRequest();
        _capturePending?.Cancel();
        Hide();
    }

    private static string CaptureErrorKey(CaptureResult result) => result.Code switch
    {
        CaptureErrorCode.ProtectedInput => "CaptureProtected",
        CaptureErrorCode.Unavailable => "CaptureUnavailable",
        CaptureErrorCode.Terminal => "CaptureTerminal",
        CaptureErrorCode.TargetExited => "CaptureTargetExited",
        CaptureErrorCode.ReleaseKeys => "CaptureReleaseKeys",
        CaptureErrorCode.ForegroundChanged => "CaptureForegroundChanged",
        CaptureErrorCode.ClipboardBusy => "CaptureClipboardBusy",
        CaptureErrorCode.ClipboardUnsupported => "CaptureClipboardUnsupported",
        CaptureErrorCode.ClipboardBackupFailed => "CaptureClipboardBackupFailed",
        CaptureErrorCode.ClipboardLockFailed => "CaptureClipboardLockFailed",
        CaptureErrorCode.ClipboardChanged => "CaptureClipboardChanged",
        CaptureErrorCode.CopyFailed => "CaptureCopyFailed",
        CaptureErrorCode.OtherClipboardChanged => "CaptureOtherClipboard",
        CaptureErrorCode.RestoreFailed => "CaptureRestoreFailed",
        CaptureErrorCode.RestoreChanged => "CaptureRestoreChanged",
        CaptureErrorCode.WindowChanged => "CaptureWindowChanged",
        CaptureErrorCode.NoText => "CaptureNoText",
        CaptureErrorCode.NoResponse => "CaptureNoResponse",
        _ => string.IsNullOrWhiteSpace(result.Error) ? "StatusSelectionMissing" : "StatusSelectionMissing"
    };

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HideToTray();
    }

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        CancelRequest();
        _capturePending?.Cancel();
        // Keep the Dispatcher alive while an injected copy completes restoration.
        if (_captureTask is not null)
        {
            try { await _captureTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { _storage.Log("exit", "capture_cleanup", "selection", 3000, "timeout"); }
            catch (OperationCanceledException) { }
        }
        _allowClose = true;
        Close();
        Application.Current.Shutdown();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideToTray(); e.Handled = true; return; }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_pending is not null) Cancel_Click(this, new RoutedEventArgs());
            else _ = TranslateNowAsync();
            e.Handled = true;
        }
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SourceBox.Focus();
            SourceBox.SelectAll();
            e.Handled = true;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            Copy_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        Topmost = _pinned;
        PinButton.Content = UiLocalization.Get(_pinned ? "Unpin" : "Pin");
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            SourceBox.Text = Clipboard.GetText();
            SourceBox.Focus();
            SourceBox.CaretIndex = SourceBox.Text.Length;
        }
        catch { SetStatus("StatusClipboardBusy"); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SourceBox.Clear();
        SourceBox.Focus();
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is not null) { Cancel_Click(sender, e); return; }
        await TranslateNowAsync();
    }

    internal async Task TranslateNowAsync()
    {
        CancelRequest();
        var text = SourceBox.Text.Trim();
        ClearResult();
        if (text.Length == 0) { SetStatus("StatusInputRequired"); UpdateInputState(); SourceBox.Focus(); return; }
        if (text.Length > 3000) { SetStatus("StatusTooLong"); UpdateInputState(); return; }
        var settings = JsonSerializer.Deserialize<TranslationSettings>(JsonSerializer.Serialize(_settings.Translation))!;
        var generation = _generation;
        var requestId = Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        _pending = cancellation;
        CancelButton.Visibility = Visibility.Collapsed;
        TranslateButton.Content = UiLocalization.Get("Cancel");
        TranslateButton.IsEnabled = true;
        SetStatus("StatusTranslating");
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await _service.TranslateAsync(text, settings, cancellation.Token);
            if (generation != _generation || cancellation.IsCancellationRequested || !IsVisible) return;
            ResultBox.Text = result.Text;
            CopyButton.IsEnabled = true;
            _resultProvider = result.ProviderName;
            _resultEndpoint = result.EndpointType;
            _resultModel = result.Model;
            _resultSource = result.SourceLanguage;
            _resultTarget = result.TargetLanguage;
            _resultDuration = result.DurationMs;
            _errorProviderKind = null;
            RefreshProvenance();
            SetStatus("StatusCompleted");
            _storage.Log(requestId, "translate", settings.Provider.ToString(), watch.ElapsedMilliseconds, "success");
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation) SetStatus("StatusTimeout");
            _storage.Log(requestId, "translate", settings.Provider.ToString(), watch.ElapsedMilliseconds, "cancelled_or_timeout");
        }
        catch (Exception error)
        {
            if (generation != _generation || !IsVisible) return;
            _errorProviderKind = settings.Provider;
            if (error is TranslationException translationError)
                SetStatus(TranslationErrorKey(translationError), TranslationErrorArguments(translationError));
            else SetStatus("ErrorUnknown");
            _resultProvider = _resultEndpoint = _resultModel = _resultSource = _resultTarget = null;
            RefreshProvenance();
            _storage.Log(requestId, "translate", settings.Provider.ToString(), watch.ElapsedMilliseconds, error.GetType().Name);
        }
        finally
        {
            if (generation == _generation)
            {
                _pending = null;
                CancelButton.Visibility = Visibility.Collapsed;
                TranslateButton.Content = UiLocalization.Get("Translate");
                UpdateInputState();
            }
        }
    }

    private void CancelRequest()
    {
        _generation++;
        _pending?.Cancel();
        _pending = null;
        if (!_ready) return;
        CancelButton.Visibility = Visibility.Collapsed;
        TranslateButton.Content = UiLocalization.Get("Translate");
        UpdateInputState();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelRequest(); SetStatus("StatusCanceled"); }

    private void ClearResult()
    {
        ResultBox.Clear();
        CopyButton.IsEnabled = false;
        _resultProvider = _resultEndpoint = _resultModel = _resultSource = _resultTarget = null;
        _errorProviderKind = null;
        ProvenanceLabel.Text = UiLocalization.Get("Provenance");
    }

    private void Source_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        CancelRequest();
        ClearResult();
        CountLabel.Text = UiLocalization.Format("Count", SourceBox.Text.Length);
        CountLabel.Foreground = SourceBox.Text.Length > 3000 ? Brushes.Firebrick : (Brush)FindResource("Muted");
        SetStatus(SourceBox.Text.Length > 3000 ? "StatusTooLong" : "StatusUpdated");
        UpdateInputState();
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ProviderBox.SelectedItem is not ComboBoxItem providerItem ||
            !TryGetProvider(providerItem, out var provider) ||
            LanguageBox.SelectedItem is not ComboBoxItem language) return;
        CancelRequest();
        ClearResult();
        _settings.Translation.Provider = provider;
        _settings.Translation.TargetLanguage = (string)language.Tag;
        SetStatus("StatusSelectionChanged");
        try { _storage.Save(_settings); }
        catch { SetStatus("StatusSaveFailed"); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultBox.Text)) return;
        try { Clipboard.SetText(ResultBox.Text); SetStatus("StatusCopied"); }
        catch { SetStatus("StatusClipboardBusy"); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsOpen) return;
        CancelRequest();
        _capturePending?.Cancel();
        _settingsOpen = true;
        ShowForUser(false);
        try
        {
            var dialog = new SettingsWindow(_settings, _storage) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _settings = dialog.Settings;
                UiLocalization.SetPreference(_settings.UiLanguage);
                SetStatus("StatusSettingsSaved");
                if (!App.IsSmokeTest && !App.IsBenchmark) RegisterShortcut();
            }
        }
        finally { _settingsOpen = false; }
    }

    // Real WPF lifecycle tests with deterministic HTTP; no user text, credentials or clipboard access.
    internal async Task RunSmokeTestAsync(string? output)
    {
        // Acceptance artifacts are documented in Simplified Chinese; pin the UI language so
        // the rendered screenshot and provenance strings stay deterministic.
        UiLocalization.SetPreference(UiLanguage.SimplifiedChinese);
        output ??= Path.Combine(AppContext.BaseDirectory, "smoke-result.json");
        var checks = new List<string>();
        try
        {
            await Task.Delay(120);
            if (_handle == IntPtr.Zero) throw new Exception("No window handle");
            checks.Add("WPF window and native handle created");
            if (ProviderMicrosoftItem.IsEnabled || ProviderPapagoItem.IsEnabled ||
                ProviderCatalog.IsAvailable(ProviderKind.MicrosoftTranslator) || ProviderCatalog.IsAvailable(ProviderKind.Papago) ||
                !ProviderCatalog.IsAvailable(ProviderKind.OpenAI))
                throw new Exception("Provider availability catalog and UI placeholders disagree");
            checks.Add("Available providers stay selectable while Microsoft Translator and Papago remain disabled placeholders");
            if (Environment.GetCommandLineArgs().Contains("--native-checks"))
                await RunNativeChecksAsync(checks);
            _service.Dispose();
            var smokeHandler = new SmokeHandler();
            using var client = new HttpClient(smokeHandler);
            _service = new TranslationService(client);
            ProviderBox.SelectedIndex = 0;
            SourceBox.Text = "first";
            var first = TranslateNowAsync();
            await Task.Delay(15);
            SourceBox.Text = "second";
            await TranslateNowAsync();
            await first;
            if (ResultBox.Text != "第二份译文") throw new Exception("Stale response overwrote latest result");
            checks.Add("Latest request wins; editing invalidates previous result");
            if (!ProvenanceLabel.Text.Contains("Google")) throw new Exception("Missing provenance");
            SourceBox.Text = "first";
            var cancelable = TranslateNowAsync();
            await Task.Delay(20);
            if (TranslateButton.Content?.ToString() != UiLocalization.Get("Cancel")) throw new Exception("Translate button did not become Cancel");
            Translate_Click(this, new RoutedEventArgs());
            await cancelable;
            if (ResultBox.Text.Length != 0) throw new Exception("Canceled request produced a result");
            checks.Add("Translate button cancels an in-flight request without stale output");
            SourceBox.Text = "second";
            await TranslateNowAsync();
            if (ResultBox.Text != "第二份译文") throw new Exception("Could not restore a completed result for language test");
            checks.Add("Provider provenance rendered");
            var resultBeforeLanguage = ResultBox.Text;
            var sourceBeforeLanguage = SourceBox.Text;
            var targetBeforeLanguage = ((ComboBoxItem)LanguageBox.SelectedItem).Tag?.ToString();
            var providerBeforeLanguage = ProviderBox.SelectedIndex;
            var requestsBeforeLanguage = smokeHandler.RequestCount;
            UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
            if (SourceBox.Text != sourceBeforeLanguage || ResultBox.Text != resultBeforeLanguage ||
                ((ComboBoxItem)LanguageBox.SelectedItem).Tag?.ToString() != targetBeforeLanguage ||
                ProviderBox.SelectedIndex != providerBeforeLanguage || smokeHandler.RequestCount != requestsBeforeLanguage)
                throw new Exception("UI language switch changed translation state or issued a request");
            if (!ProvenanceLabel.Text.Contains("Google web endpoint (unofficial)", StringComparison.Ordinal))
                throw new Exception("English provenance was not localized");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            checks.Add("UI language switch preserves source/result/provider/target and localizes provenance without HTTP");
            SourceBox.Text = "first";
            var hidden = TranslateNowAsync();
            HideToTray();
            await hidden;
            if (IsVisible || ResultBox.Text.Length != 0) throw new Exception("Hidden request resurfaced");
            checks.Add("Hide cancels request and prevents reappearance");
            ShowForUser(false);
            ShowForUser(true);
            if (ShouldAutoHide(true) || !ShouldAutoHide(false)) throw new Exception("Auto-hide foreground boundary is incorrect");
            _pinned = true;
            if (ShouldAutoHide(false)) throw new Exception("Pinned capture popup still auto-hides");
            _pinned = false;
            _settings.AutoHideSelectionPopup = false;
            if (ShouldAutoHide(false)) throw new Exception("Disabled auto-hide still hides");
            _settings.AutoHideSelectionPopup = true;
            ShowForUser(false);
            if (ShouldAutoHide(false)) throw new Exception("Manual window incorrectly auto-hides");
            checks.Add("Capture auto-hide honors foreground, pin, setting, and manual-window boundaries");
            SourceBox.Text = "fail";
            await TranslateNowAsync();
            if (ResultBox.Text.Length != 0 || !ProvenanceLabel.Text.Contains("未切换服务")) throw new Exception("Failure did not preserve provider boundary");
            checks.Add("HTTP failure remains on selected provider");
            SourceBox.Text = new string('a', 3001);
            await TranslateNowAsync();
            if (!StatusLabel.Text.Contains("3000")) throw new Exception("Length validation missing");
            if (TranslateButton.IsEnabled) throw new Exception("Oversize request did not disable Translate button");
            checks.Add("Oversize request blocked before HTTP");
            SourceBox.Text = "";
            if (TranslateButton.IsEnabled) throw new Exception("Empty request did not disable Translate button");
            checks.Add("Empty input keeps the prompt and disables Translate");
            await RunExtendedAcceptanceAsync(checks, () => smokeHandler.RequestCount);
            // The acceptance mock deliberately leaves the Google service in its
            // local cooldown state. Reset only this smoke-owned service so the
            // later screenshot translation remains an independent check.
            _service.Dispose();
            _service = new TranslationService(client);
            // Interface-language switch changes only labels; it must not clear text or translate.
            SourceBox.Text = "language switch guard";
            UiLocalization.SetPreference(UiLanguage.English);
            if (SourceBox.Text != "language switch guard") throw new Exception("Language switch cleared source text");
            if (TitleText.Text != "XTranslate" || SettingsButton.Content is not "Settings") throw new Exception("English labels not applied");
            UiLocalization.SetPreference(UiLanguage.SimplifiedChinese);
            if (SettingsButton.Content is not "设置") throw new Exception("Chinese labels not restored");
            checks.Add("UI language switch relabels without touching source or issuing a request");
            var settings = new UserSettings();
            settings.Translation.DeepLApiKey = "smoke-secret-not-real";
            _storage.Save(settings);
            if (File.ReadAllText(Path.Combine(_storage.DirectoryPath, "settings.json")).Contains("smoke-secret-not-real")) throw new Exception("Credential persisted in plaintext");
            if (_storage.Load().Translation.DeepLApiKey != "smoke-secret-not-real") throw new Exception("Credential roundtrip failed");
            settings.UiLanguage = UiLanguage.English;
            _storage.Save(settings);
            if (_storage.Load().UiLanguage != UiLanguage.English) throw new Exception("Interface language persistence failed");
            settings.UiLanguage = UiLanguage.SimplifiedChinese;
            _storage.Save(settings);
            checks.Add("Windows DPAPI credential roundtrip; no plaintext key on disk");
            SourceBox.Text = "A small tool that helps you understand and return to your work.";
            if (Environment.GetCommandLineArgs().Contains("--live-google"))
            {
                _service.Dispose();
                _service = new TranslationService();
            }
            await TranslateNowAsync();
            if (string.IsNullOrWhiteSpace(ResultBox.Text)) throw new Exception("Final translation failed: " + StatusLabel.Text);
            if (Environment.GetCommandLineArgs().Contains("--live-google")) checks.Add("Live Google translation of a fixed public test sentence succeeded");
            UpdateLayout();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            UiLocalization.SetPreference(UiLanguage.SimplifiedChinese);
            RenderWindowTo(this, Path.ChangeExtension(output, ".zh.png"));
            UiLocalization.SetPreference(UiLanguage.English);
            Width = MinWidth;
            UpdateLayout();
            RenderWindowTo(this, Path.ChangeExtension(output, ".en.png"));
            Width = 650;
            UpdateLayout();
            var settingsWindow = new SettingsWindow(_settings, _storage) { Owner = this };
            settingsWindow.Show();
            await Task.Delay(80);
            UiLocalization.SetPreference(UiLanguage.SimplifiedChinese);
            settingsWindow.UpdateLayout();
            RenderWindowTo(settingsWindow, Path.ChangeExtension(output, ".settings.zh.png"));
            UiLocalization.SetPreference(UiLanguage.English);
            settingsWindow.UpdateLayout();
            RenderWindowTo(settingsWindow, Path.ChangeExtension(output, ".settings.en.png"));
            settingsWindow.Close();
            UiLocalization.SetPreference(UiLanguage.SimplifiedChinese);
            checks.Add("Simplified Chinese/English main and settings UI rendered to PNG");
            File.WriteAllText(output, JsonSerializer.Serialize(new { success = true, checks, workingSetMb = Environment.WorkingSet / 1024 / 1024 }, new JsonSerializerOptions { WriteIndented = true }));
            _allowClose = true;
            Close();
            Application.Current.Shutdown(0);
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            _allowClose = true;
            Close();
            Application.Current.Shutdown(1);
        }
    }

    private async Task RunNativeChecksAsync(List<string> checks)
    {
        const int testHotkeyId = NativeMethods.HotkeyId + 1;
        if (!NativeMethods.RegisterHotKey(_handle, testHotkeyId, 0x4007, 0x7B))
            throw new Exception("Test shortcut Ctrl+Alt+Shift+F12 could not be registered");
        NativeMethods.UnregisterHotKey(_handle, testHotkeyId);
        checks.Add("Win32 global shortcut registration and cleanup");
        var fixtureText = new TextBox { Text = "Native selection fixture: hello world", Margin = new Thickness(20), FontSize = 16 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(fixtureText, "NativeFixtureText");
        var fixture = new Window { Title = "XTranslate 验收 · 固定测试文本", Width = 440, Height = 160, Content = fixtureText, ShowInTaskbar = false, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            fixture.Show();
            fixture.Activate();
            fixtureText.Focus();
            fixtureText.SelectAll();
            await Task.Delay(150);
            var fixtureHandle = new WindowInteropHelper(fixture).Handle;
            if (NativeMethods.GetForegroundWindow() == fixtureHandle)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var selection = await _capture.CaptureAsync(fixtureHandle, _handle, false, cancellation.Token);
                if (selection.Text != fixtureText.Text) throw new Exception("UI Automation fixture selection failed: " + selection.Error);
                checks.Add("Real foreground UI Automation selection from a controlled WPF fixture");
            }
            else
            {
                // Hidden launches can be refused foreground by Windows. Query only our named
                // fixture instead; never compensate by reading the user's current application.
                var selected = await Task.Run(() =>
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(fixtureHandle);
                    var element = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "NativeFixtureText"));
                    if (element?.GetCurrentPattern(System.Windows.Automation.TextPattern.Pattern) is not System.Windows.Automation.TextPattern textPattern)
                        throw new Exception("Fixture does not expose TextPattern");
                    return string.Join(Environment.NewLine, textPattern.GetSelection().Select(range => range.GetText(3001)));
                }).WaitAsync(TimeSpan.FromSeconds(3));
                if (selected != fixtureText.Text) throw new Exception("Background UI Automation fixture selection failed");
                checks.Add("Real TextPattern selection from explicit background fixture; foreground hotkey flow still requires manual verification");
            }
        }
        finally { fixture.Close(); }
        ShowForUser(false);
        NativeMethods.PositionNearCursor(_handle);
        NativeMethods.GetWindowRect(_handle, out var windowRect);
        NativeMethods.GetCursorPos(out var cursor);
        var monitor = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromPoint(cursor, 2), ref monitor) &&
            (windowRect.Left < monitor.Work.Left || windowRect.Top < monitor.Work.Top || windowRect.Right > monitor.Work.Right || windowRect.Bottom > monitor.Work.Bottom))
            throw new Exception("Popup did not fit current monitor work area");
        checks.Add("Popup fits the current monitor work area");
    }

    private static void RenderWindowTo(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)window.ActualWidth), Math.Max(1, (int)window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class SmokeHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var url = request.RequestUri!.Query;
            if (url.Contains("first")) await Task.Delay(160); // Deliberately ignore cancellation to simulate late delivery.
            else await Task.Delay(10, cancellationToken);
            if (url.Contains("rate-limit"))
            {
                var limited = new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("sensitive provider body must not reach the UI")
                };
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return limited;
            }
            if (url.Contains("fail")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var translated = url.Contains("second") ? "第二份译文" : "一个小工具，帮助你理解内容，然后继续手头的工作。";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new object?[] { new object[] { new object?[] { translated, "source", null, null } }, null, "en" }), Encoding.UTF8, "application/json")
            };
        }
    }

}
