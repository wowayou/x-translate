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
    private TrayMenu? _trayMenu;
    private HwndSource? _source;
    private IntPtr _handle;
    private CancellationTokenSource? _pending;
    private StreamingTranslationPreview? _streamingPreview;
    private bool _isStreamingPreview;
    private CancellationTokenSource? _capturePending;
    private long _generation;
    private bool _ready;
    private bool _allowClose;
    private bool _capturing;
    private bool _settingsOpen;
    private bool _hotkeyRegistered;
    private HotkeyRegistration? _hotkeys;
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
    private bool _resultFellBack;
    private int _errorAttempts;
    private enum StatusAction { None, Settings, Retry }
    private StatusAction _statusAction;
    private SettingsWindow.Section _actionSection;
    private string? _actionServiceId;
    private enum InfoKind { None, UpdateAvailable, RestartPending }
    private InfoKind _info;
    private UpdateVersion? _infoVersion;

    public MainWindow(AppStorage storage)
    {
        _storage = storage;
        _settings = storage.Load();
        UiLocalization.SetPreference(_settings.UiLanguage);
        UiAppearance.Apply(App.DiagnosticTheme ?? _settings.Theme);
        InitializeComponent();
        WindowPlacement.Attach(this);
        var appImage = LoadAppImage();
        Icon = appImage;
        AppIconImage.Source = appImage;
        foreach (var language in TranslationLanguages.All)
            LanguageBox.Items.Add(new ComboBoxItem { Content = language.NativeName, Tag = language.Code });
        RebuildAiProviderItems();
        SelectProvider(_settings.Translation.Provider);
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == _settings.Translation.TargetLanguage) ?? LanguageBox.Items[0];
        _settings.Translation.TargetLanguage = (string)((ComboBoxItem)LanguageBox.SelectedItem).Tag;
        _suppressUiLangEvent = true;
        UiLanguageBox.SelectedIndex = (int)UiLocalization.Preference;
        _suppressUiLangEvent = false;
        _ready = true;

        _trayMenu = CreateTrayMenu();
        Localize();
        UiLocalization.Changed += OnLanguageChanged;

        if (!App.IsSmokeTest)
        {
            _trayIcon = AppIcon.CreateTrayIcon();
            _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = UiLocalization.Get("TrayText"), Visible = true };
            _tray.MouseUp += (_, args) =>
            {
                if (args.Button == Forms.MouseButtons.Right)
                    Dispatcher.BeginInvoke(new Action(() => _trayMenu?.Show(this)));
            };
            _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowForUser(false));
        }
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_handle);
            _source.AddHook(WindowProc);
            if (!App.IsSmokeTest && !App.IsBenchmark) { RegisterShortcut(); ConfigureMouseSelection(); }
            if (_storage.Warning is not null) SetStatus("StorageWarning");
            _ = AutoCheckUpdatesAsync();
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
            _hotkeys?.Dispose();
            StopMouseSelection();
            _source?.RemoveHook(WindowProc);
            _trayMenu?.Dispose();
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
        RefreshPin();
        SettingsButton.Content = UiLocalization.Get("Settings");
        LearningButton.Content = LearningUi.Text("Book");
        CollectLearningButton.Content = LearningUi.Text("Collect");
        ProviderLabel.Text = UiLocalization.Get("ProviderLabel");
        ProviderGoogleItem.Content = UiLocalization.Get("GoogleProvider");
        ProviderDeepLItem.Content = UiLocalization.Get("DeepLProvider") + NoKeySuffix(_settings.Translation.DeepLApiKey);
        foreach (var item in AiProviderItems())
            item.Content = AiItemText((AiServiceProfile)item.DataContext);
        RefreshStatusAction();
        RefreshInfoBar();
        ProviderMicrosoftItem.Content = UiLocalization.Get("MicrosoftProviderPlanned");
        ProviderPapagoItem.Content = UiLocalization.Get("PapagoProviderPlanned");
        TargetLabel.Text = UiLocalization.Get("TargetLabel");
        SourceLabel.Text = UiLocalization.Get("SourceLabel");
        PasteButton.Content = UiLocalization.Get("Paste");
        ClearButton.Content = UiLocalization.Get("Clear");
        SourceBox.ToolTip = UiLocalization.Get("SourceToolTip");
        ResultLabel.Text = UiLocalization.Get(_isStreamingPreview ? "ResultPreviewLabel" : "ResultLabel");
        ResultBox.ToolTip = UiLocalization.Get("ResultToolTip");
        CopyButton.Content = UiLocalization.Get("Copy");
        TranslateButton.Content = UiLocalization.Get(_pending is null ? "Translate" : "Cancel");
        CancelButton.Content = UiLocalization.Get("Cancel");
        RefreshLanguageChoices();
        UpdateRequestHint();
        CountLabel.Text = UiLocalization.Format("Count", SourceBox.Text.Length);
        if (!CopyButton.IsEnabled) ProvenanceLabel.Text = UiLocalization.Get("Provenance");
        TrayFooterLabel.Text = UiLocalization.Get("TrayFooter");
        UpdateHotkeyLabels();
        if (_tray is not null) _tray.Text = TrayToolTip();
        RefreshTrayState();
        RefreshProvenance();
        RefreshSourceReview();
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
        _statusAction = StatusAction.None;
        StatusActionButton.Visibility = Visibility.Collapsed;
        StatusLabel.Text = RenderStatus();
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty,
            key.StartsWith("Error", StringComparison.Ordinal) || key == "StorageWarning" ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
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
        TranslationErrorCode.UnsupportedSourceReview => "ErrorUnsupportedSourceReview",
        TranslationErrorCode.InvalidSourceReview => "ErrorInvalidSourceReview",
        TranslationErrorCode.EmptyResponse => "ErrorEmptyResponse",
        TranslationErrorCode.StreamingNotSupported => "ErrorStreamingNotSupported",
        TranslationErrorCode.IncompleteResponse => "ErrorIncompleteResponse",
        TranslationErrorCode.ResponseTooLarge => "ErrorResponseTooLarge",
        TranslationErrorCode.Network => "ErrorNetwork",
        TranslationErrorCode.MissingTargetLanguage => "ErrorMissingTarget",
        TranslationErrorCode.MissingApiKey => "ErrorMissingApiKey",
        TranslationErrorCode.MissingModel => "ErrorMissingModel",
        TranslationErrorCode.InvalidEndpoint => "ErrorInvalidEndpoint",
        TranslationErrorCode.UnsupportedProtocol => "ErrorUnsupportedProtocol",
        TranslationErrorCode.UnsupportedLanguage => "ErrorUnsupportedLanguage",
        TranslationErrorCode.InvalidApiKey => "ErrorInvalidApiKey",
        TranslationErrorCode.BadRequest => "ErrorBadRequest",
        TranslationErrorCode.NotFound => "ErrorNotFound",
        TranslationErrorCode.MethodNotAllowed => "ErrorMethodNotAllowed",
        TranslationErrorCode.RedirectDenied => "ErrorRedirectDenied",
        TranslationErrorCode.Unauthorized => "ErrorUnauthorized",
        TranslationErrorCode.Forbidden => "ErrorForbidden",
        TranslationErrorCode.RateLimited when _errorProviderKind == ProviderKind.GoogleWeb => "ErrorGoogleRateLimited",
        TranslationErrorCode.RateLimited => "ErrorRateLimited",
        TranslationErrorCode.RateLimitCooldown => "ErrorGoogleRateLimitCooldown",
        TranslationErrorCode.ServerUnavailable => "ErrorServerUnavailable",
        TranslationErrorCode.RequestFailed when error.StatusCode is null => "ErrorStreamingFailed",
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
        CollectLearningButton.IsEnabled = !string.IsNullOrWhiteSpace(SourceBox.Text);
        ResultPlaceholder.Text = UiLocalization.Get(_sourceReview?.Status == SourceReviewStatus.Uncertain
            ? "SourceReviewUncertainPlaceholder" : _pending is null ? "ResultPlaceholder" : "ResultWaiting");
        if (_pending is not null)
        {
            TranslateButton.IsEnabled = true;
            return;
        }
        var length = SourceBox.Text.Trim().Length;
        TranslateButton.IsEnabled = length is > 0 and <= 3000 &&
            (!ReviewRequested || (SourceBox.Text.Length <= 3000 && _settings.Translation.Provider == ProviderKind.OpenAI)) &&
            BuildCandidates().Any(candidate => TranslationLanguages.Supports(candidate.Settings.Provider, candidate.Settings.TargetLanguage));
    }

    private void UpdateHotkeyLabels()
    {
        HotkeyLabel.Text = !_settings.CaptureHotkeyEnabled ? UiLocalization.Get("HotkeyDisabledFooter")
            : _hotkeyRegistered ? UiLocalization.Format("HotkeyFooter", _hotkeyText) : UiLocalization.Get("HotkeyUnavailable");
        SourcePlaceholder.Text = _hotkeyRegistered
            ? UiLocalization.Format("SourcePlaceholderHotkey", _hotkeyText)
            : UiLocalization.Get("SourcePlaceholderPaste");
    }

    private string TrayToolTip() => !_settings.CaptureHotkeyEnabled ? UiLocalization.Get("TrayHotkeyDisabled")
        : _hotkeyRegistered ? UiLocalization.Format("TrayHotkeyText", _hotkeyText) : UiLocalization.Get("TrayUnavailableText");

    private TrayMenu CreateTrayMenu() => new(() => ShowForUser(false),
        () => OpenSettings(SettingsWindow.Section.General), SetCaptureHotkeyEnabled,
        () => OpenSettings(SettingsWindow.Section.About), ExitApplication, SetMouseSelectionEnabled);

    private void SetCaptureHotkeyEnabled(bool enabled)
    {
        if (_settingsOpen || _settings.CaptureHotkeyEnabled == enabled) return;
        var previous = _settings.CaptureHotkeyEnabled;
        _settings.CaptureHotkeyEnabled = enabled;
        try { _storage.Save(_settings); }
        catch
        {
            _settings.CaptureHotkeyEnabled = previous;
            RefreshTrayState();
            SetStatus("StatusSaveFailed");
            return;
        }
        if (!App.IsSmokeTest && !App.IsBenchmark) RegisterShortcut();
        else UpdateHotkeyLabels();
        RefreshTrayState();
    }

    private void RefreshProvenance()
    {
        if (_resultProvider is not null)
        {
            ProvenanceLabel.Text = FormatProvenance(_resultProvider, _resultEndpoint!, _resultModel, _resultDuration, _resultSource!, _resultTarget!, _isStreamingPreview) +
                (_resultFellBack ? " · " + UiLocalization.Get("ProvenanceFallback") : "");
            return;
        }
        if (_errorProviderKind is not null)
            ProvenanceLabel.Text = $"{ProviderDisplay(_errorProviderKind.Value)} · " +
                (_errorAttempts > 1 ? UiLocalization.Format("ErrorNoTranslationFallback", _errorAttempts) : UiLocalization.Get("ErrorNoTranslation"));
        else if (!CopyButton.IsEnabled)
            ProvenanceLabel.Text = UiLocalization.Get("Provenance");
    }

    private static string FormatProvenance(string provider, string endpoint, string? model, long duration, string source, string target, bool isPreview = false)
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
        var state = isPreview ? UiLocalization.Get("ProvenanceStreaming") : $"{duration} ms";
        return $"{providerText} · {endpointText}" +
               (string.IsNullOrWhiteSpace(model) ? "" : $" · {model}") + $" · {state} · {source} → {target}";
    }

    private string ProviderDisplay(ProviderKind provider) => provider switch
    {
        ProviderKind.GoogleWeb => UiLocalization.Get("GoogleProvider"),
        ProviderKind.DeepL => UiLocalization.Get("DeepLProvider"),
        ProviderKind.OpenAI => UiLocalization.Format("OpenAiProvider", _settings.ActiveAiService?.DisplayName() ?? ""),
        ProviderKind.MicrosoftTranslator => UiLocalization.Get("MicrosoftProviderPlanned"),
        ProviderKind.Papago => UiLocalization.Get("PapagoProviderPlanned"),
        _ => UiLocalization.Get("ErrorUnsupportedProvider")
    };

    private void SelectProvider(ProviderKind provider)
    {
        ProviderBox.SelectedItem = provider == ProviderKind.OpenAI
            ? AiProviderItems().FirstOrDefault(item => item.DataContext == _settings.ActiveAiService) ?? ProviderGoogleItem
            : ProviderBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => Enum.TryParse<ProviderKind>(item.Tag?.ToString(), out var value) && value == provider && item.IsEnabled)
              ?? ProviderGoogleItem;
    }

    /// <summary>Unconfigured services stay selectable but say so before anything is sent.</summary>
    private static string NoKeySuffix(string? key) => string.IsNullOrWhiteSpace(key) ? " · " + UiLocalization.Get("ProviderNoKey") : "";
    private static string AiItemText(AiServiceProfile service) => UiLocalization.Format("OpenAiProvider", service.DisplayName()) + NoKeySuffix(service.ApiKey);

    /// <summary>Configuration problems point to the exact settings page; transient ones offer a retry.</summary>
    private void SetStatusAction(TranslationErrorCode code, ProviderKind provider)
    {
        if (code is TranslationErrorCode.MissingApiKey or TranslationErrorCode.InvalidApiKey or TranslationErrorCode.Unauthorized or
            TranslationErrorCode.Forbidden or TranslationErrorCode.MissingModel or TranslationErrorCode.InvalidModel or TranslationErrorCode.InvalidEndpoint or
            TranslationErrorCode.UnsupportedProtocol or TranslationErrorCode.NotFound or TranslationErrorCode.MethodNotAllowed or
            TranslationErrorCode.StreamingNotSupported or TranslationErrorCode.BadRequest)
        {
            _statusAction = StatusAction.Settings;
            _actionSection = SettingsWindow.Section.Services;
            _actionServiceId = provider == ProviderKind.OpenAI ? _settings.ActiveAiService?.Id : null;
        }
        else if (code is TranslationErrorCode.Timeout or TranslationErrorCode.Network or TranslationErrorCode.ServerUnavailable or
                 TranslationErrorCode.RateLimited or TranslationErrorCode.IncompleteResponse or TranslationErrorCode.EmptyResponse or
                 TranslationErrorCode.InvalidResponse or TranslationErrorCode.InvalidSourceReview or TranslationErrorCode.RequestFailed)
            _statusAction = StatusAction.Retry;
        RefreshStatusAction();
    }

    private void RefreshStatusAction()
    {
        StatusActionButton.Visibility = _statusAction == StatusAction.None ? Visibility.Collapsed : Visibility.Visible;
        StatusActionButton.Content = UiLocalization.Get(_statusAction == StatusAction.Retry ? "StatusActionRetry" : "StatusActionSettings");
    }

    private async void StatusAction_Click(object sender, RoutedEventArgs e)
    {
        if (_statusAction == StatusAction.Retry) await TranslateNowAsync();
        else if (_statusAction == StatusAction.Settings) OpenSettings(_actionSection, _actionServiceId);
    }

    private void ShowInfo(InfoKind kind, UpdateVersion? version = null)
    {
        _info = kind;
        _infoVersion = version;
        RefreshInfoBar();
    }

    private void RefreshInfoBar()
    {
        InfoBar.Visibility = _info == InfoKind.None ? Visibility.Collapsed : Visibility.Visible;
        InfoBarText.Text = _info switch
        {
            InfoKind.UpdateAvailable => UiLocalization.Format("InfoUpdateAvailable", _infoVersion!),
            InfoKind.RestartPending => UiLocalization.Get("InfoRestartPending"),
            _ => ""
        };
        InfoBarActionButton.Content = UiLocalization.Get(_info == InfoKind.RestartPending ? "InfoRestartNow" : "InfoUpdateView");
        InfoBarCloseButton.Content = UiLocalization.Get("Close");
        InfoBarCloseButton.ToolTip = UiLocalization.Get("Close");
        System.Windows.Automation.AutomationProperties.SetName(InfoBarCloseButton, UiLocalization.Get("Close"));
    }

    private void InfoBarAction_Click(object sender, RoutedEventArgs e)
    {
        if (_info == InfoKind.RestartPending) RestartForUpdate();
        else OpenSettings(SettingsWindow.Section.About);
    }

    private void InfoBarClose_Click(object sender, RoutedEventArgs e) => ShowInfo(InfoKind.None);

    private void RestartForUpdate()
    {
        try { AppUpdates.StartReplacement(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { SetStatus("ErrorUnknown"); return; }
        ExitApplication();
    }

    /// <summary>Opt-in, at most once a day, 20 s after start. Failures are logged only (no content, no URL).</summary>
    private async Task AutoCheckUpdatesAsync()
    {
        if (!_settings.AutoCheckUpdates || !AppUpdates.CanSelfUpdate || App.IsSmokeTest || App.IsBenchmark) return;
        if (_settings.LastUpdateCheckUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24)) return;
        await Task.Delay(TimeSpan.FromSeconds(20));
        if (_exiting || !_settings.AutoCheckUpdates) return;
        try
        {
            using var client = new UpdateClient();
            var release = await client.GetLatestAsync(AppUpdates.Flavor!.Value, AppUpdates.Current, CancellationToken.None);
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            try { _storage.Save(_settings); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (release.Version > AppUpdates.Current && _info == InfoKind.None) ShowInfo(InfoKind.UpdateAvailable, release.Version);
            _storage.Log("update", "check", "github", 0, release.Version > AppUpdates.Current ? "available" : "latest");
        }
        catch (UpdateException error) { _storage.Log("update", "check", "github", 0, error.Code.ToString(), statusCode: error.StatusCode); }
    }

    private IEnumerable<ComboBoxItem> AiProviderItems() =>
        ProviderBox.Items.OfType<ComboBoxItem>().Where(item => item.DataContext is AiServiceProfile).ToArray();

    /// <summary>One provider entry per configured AI service, placed after DeepL. Never sends HTTP.</summary>
    private void RebuildAiProviderItems()
    {
        var ready = _ready;
        _ready = false; // Rebuilding the list must not count as a user selection.
        var hadAiSelected = ProviderBox.SelectedItem is ComboBoxItem { DataContext: AiServiceProfile };
        try
        {
            foreach (var item in AiProviderItems()) ProviderBox.Items.Remove(item);
            var index = ProviderBox.Items.IndexOf(ProviderDeepLItem) + 1;
            foreach (var service in _settings.AiServices)
                ProviderBox.Items.Insert(index++, new ComboBoxItem
                {
                    Tag = nameof(ProviderKind.OpenAI), DataContext = service,
                    Content = AiItemText(service)
                });
            // Removing the selected entry clears the ComboBox; keep showing the active service.
            if (hadAiSelected || ProviderBox.SelectedItem is null) SelectProvider(_settings.Translation.Provider);
        }
        finally { _ready = ready; }
    }

    private static bool TryGetProvider(ComboBoxItem item, out ProviderKind provider) =>
        Enum.TryParse(item.Tag?.ToString(), out provider) && ProviderCatalog.IsAvailable(provider);

    internal bool ShouldAutoHide(bool foregroundIsOwnProcess) =>
        _fromCapture && _settings.AutoHideSelectionPopup && !_pinned && !_settingsOpen && !_capturing &&
        _pending is null && !_exiting && IsVisible && _trayMenu?.Menu.IsOpen != true && !foregroundIsOwnProcess;

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
        // Never while Settings is open: that would discard the user's unsaved edits. The installer then stops.
        if ((uint)message == NativeMethods.ExitMessage && !App.IsSmokeTest && !App.IsBenchmark && !_settingsOpen)
        {
            ExitApplication(); // Same path as tray "Exit".
            handled = true;
        }
        if ((uint)message == NativeMethods.ShowMessage && !_settingsOpen)
        {
            ShowForUser(false);
            handled = true;
        }
        if (message == NativeMethods.WmHotkey && _settings.CaptureHotkeyEnabled && _hotkeys?.Id == wParam.ToInt32() && !_settingsOpen)
        {
            if (!_exiting && !_capturing) _captureTask = CaptureSelectionAsync();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RegisterShortcut()
    {
        if (!_settings.CaptureHotkeyEnabled)
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            _hotkeyRegistered = false;
            SetStatus("StatusHotkeyDisabled");
            UpdateHotkeyLabels();
            RefreshTrayState();
            if (_tray is not null) _tray.Text = TrayToolTip();
            return;
        }
        _hotkeys ??= new HotkeyRegistration(NativeMethods.HotkeyId, NativeMethods.HotkeyId + 2,
            (id, modifiers, key) => NativeMethods.RegisterHotKey(_handle, id, modifiers, key),
            id => NativeMethods.UnregisterHotKey(_handle, id));
        if (!HotkeyGesture.TryParse(_settings.EffectiveCaptureHotkey, out var requested))
        {
            SetStatus("SettingsHotkeyError");
            return;
        }
        var applied = _hotkeys.TryApply(requested);
        var fellBack = false;
        if (!applied && _hotkeys.Current is null && requested.Display == "Ctrl+Q")
        {
            HotkeyGesture.TryParse("Ctrl+Alt+Q", out var fallback);
            fellBack = _hotkeys.TryApply(fallback);
        }
        _hotkeyRegistered = _hotkeys.Current is not null;
        _hotkeyText = _hotkeys.Current?.Display ?? requested.Display;
        if (fellBack) SetStatus("StatusHotkeyOccupied");
        else if (!applied && _hotkeyRegistered)
            SetStatus("StatusHotkeyKept", requested.Display, _hotkeyText);
        else if (!_hotkeyRegistered) SetStatus("StatusHotkeyUnavailable");
        else if (_statusKey.Length == 0 || _statusKey is "StatusReady" or "StatusHotkeyDisabled") SetStatus("StatusReady", _hotkeyText);
        UpdateHotkeyLabels();
        if (_tray is not null) _tray.Text = TrayToolTip();
        RefreshTrayState();
    }

    internal Task CaptureSelectionAsync()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return CaptureSelectionAsync(foreground, () => CaptureProcessName(foreground),
            token => _capture.CaptureAsync(foreground, _handle, _settings.AllowCopyCapture, token));
    }

    // The OS identity/text boundaries can be replaced by fixed fixtures without touching other applications.
    internal async Task CaptureSelectionAsync(IntPtr foreground, Func<string?> identify, Func<CancellationToken, Task<CaptureResult>> capture)
    {
        if (_capturing || _settingsOpen || _exiting) return;
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId) { ShowForUser(false); return; }
        if (!AllowAppCapture(identify)) return; // Before UIA, simulated copy, cancellation, focus or text changes.
        ResetMouseSelection();
        _capturing = true;
        CancelRequest();
        using var cancellation = new CancellationTokenSource();
        _capturePending = cancellation;
        var watch = Stopwatch.StartNew();
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var captured = await capture(cancellation.Token);
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
            if (ShouldAutoTranslateSelection) await TranslateNowAsync();
            else
            {
                TranslateButton.Focus();
                if (ReviewRequested) SetStatus("StatusReviewCaptured");
                else SetStatus("StatusSelectionCapturedManual", captured.Method, watch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) { _storage.Log(id, "capture", "selection", watch.ElapsedMilliseconds, "cancelled"); }
        catch { ShowForUser(true); SetStatus("StatusCaptureFailed"); }
        finally { _capturePending = null; _capturing = false; }
    }

    public void ShowForUser() => ShowForUser(false);

    public void ShowForUser(bool fromCapture)
    {
        ResetMouseSelection();
        _fromCapture = fromCapture;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        ResetMouseSelection();
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
        StopMouseSelection();
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

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        _pinned = PinButton.IsChecked == true;
        Topmost = _pinned;
        RefreshPin();
    }

    private void RefreshPin()
    {
        PinButton.IsChecked = _pinned;
        PinGlyph.Text = _pinned ? "\uE77A" : "\uE718";
        var label = UiLocalization.Get(_pinned ? "Unpin" : "Pin");
        PinButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(PinButton, label);
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

    private void ShowStreamingProgress(TranslationProgress update, TranslationSettings settings, long generation, CancellationToken token)
    {
        if (generation != _generation || token.IsCancellationRequested || _pending is null || !IsVisible) return;
        ResultBox.AppendText(update.TextDelta);
        _isStreamingPreview = true;
        _resultProvider = AiEndpoint.ProviderName(settings.AiProtocol,
            AiEndpoint.Resolve(settings.AiProtocol, settings.OpenAiEndpoint, settings.OpenAiModel, settings.OpenAiStreaming));
        _resultEndpoint = "AI 接口";
        _resultModel = update.Model;
        _resultSource = "auto";
        _resultTarget = settings.TargetLanguage;
        ResultLabel.Text = UiLocalization.Get("ResultPreviewLabel");
        RefreshProvenance();
        SetStatus("StatusStreaming");
    }

    internal async Task TranslateNowAsync()
    {
        CancelRequest();
        var text = ReviewRequested ? SourceBox.Text : SourceBox.Text.Trim();
        ClearResult();
        if (string.IsNullOrWhiteSpace(text)) { SetStatus("StatusInputRequired"); UpdateInputState(); SourceBox.Focus(); return; }
        if (text.Length > 3000) { SetStatus("StatusTooLong"); UpdateInputState(); return; }
        var candidates = BuildCandidates();
        var primary = candidates[0];
        var generation = _generation;
        var requestId = Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        _pending = cancellation;
        CancelButton.Visibility = Visibility.Collapsed;
        TranslateButton.Content = UiLocalization.Get("Cancel");
        TranslateButton.IsEnabled = true;
        SetStatus(ReviewRequested ? "StatusReviewing" : "StatusTranslating");
        RefreshSourceReview();
        UpdateInputState();
        var watch = Stopwatch.StartNew();
        var attemptWatch = Stopwatch.StartNew();
        var attemptIndex = 0;
        // Each streaming attempt gets its own preview; a failed attempt's preview is disposed before the next starts.
        IProgress<TranslationProgress>? ProgressFor(TranslationCandidate candidate)
        {
            _streamingPreview?.Dispose();
            _streamingPreview = null;
            if (generation != _generation || candidate.Settings.ReviewSource || candidate.Settings.Provider != ProviderKind.OpenAI || !candidate.Settings.OpenAiStreaming) return null;
            var settings = candidate.Settings;
            _streamingPreview = new StreamingTranslationPreview(Dispatcher, update => ShowStreamingProgress(update, settings, generation, cancellation.Token));
            return _streamingPreview;
        }
        void OnFailed(TranslationCandidate candidate, TranslationAttempt attempt)
        {
            _storage.Log(requestId, attemptIndex++ == 0 ? "translate" : "fallback", candidate.Settings.Provider.ToString(),
                attemptWatch.ElapsedMilliseconds, attempt.Sent ? "failed" : "skipped", attempt.Code, attempt.StatusCode);
            attemptWatch.Restart();
            if (generation != _generation) return;
            ClearResult();
            var next = candidates.SkipWhile(item => item != candidate).Skip(1).FirstOrDefault();
            if (next is not null) SetStatus("StatusFallbackTrying", candidate.DisplayName, Reason(attempt), next.DisplayName);
        }
        try
        {
            var outcome = await TranslationFallback.TranslateAsync(_service, text, candidates, cancellation.Token, ProgressFor, OnFailed);
            var result = outcome.Result;
            if (generation != _generation || cancellation.IsCancellationRequested || !IsVisible) return;
            ResultBox.Text = result.Text;
            _isStreamingPreview = false;
            ResultLabel.Text = UiLocalization.Get("ResultLabel");
            CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(result.Text);
            _sourceReview = result.SourceReview;
            ReviewDetails.IsExpanded = _sourceReview is not null;
            RefreshSourceReview();
            _resultProvider = result.ProviderName;
            _resultEndpoint = result.EndpointType;
            _resultModel = result.Model;
            _resultSource = result.SourceLanguage;
            _resultTarget = result.TargetLanguage;
            _resultDuration = result.DurationMs;
            _resultFellBack = outcome.Failed.Count > 0;
            _errorProviderKind = null;
            RefreshProvenance();
            if (_resultFellBack) SetStatus("StatusFallbackCompleted", outcome.Candidate.DisplayName, Failures(outcome.Failed));
            else SetStatus(_sourceReview?.Status == SourceReviewStatus.Uncertain ? "StatusReviewClarification" : "StatusCompleted");
            _storage.Log(requestId, attemptIndex == 0 ? "translate" : "fallback", outcome.Candidate.Settings.Provider.ToString(),
                attemptWatch.ElapsedMilliseconds, "success");
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation) { ClearResult(); SetStatus("StatusTimeout"); }
            _storage.Log(requestId, "translate", primary.Settings.Provider.ToString(), watch.ElapsedMilliseconds, "cancelled_or_timeout");
        }
        catch (FallbackFailedException failure)
        {
            if (generation != _generation || !IsVisible) return;
            ClearResult();
            _errorProviderKind = primary.Settings.Provider;
            _errorAttempts = failure.Attempts.Count(attempt => attempt.Sent); // Skipped services never received the text.
            SetStatus("ErrorAllFallbacksFailed", Failures(failure.Attempts));
            if (failure.Attempts.Any(attempt => attempt.Sent)) { _statusAction = StatusAction.Retry; RefreshStatusAction(); }
            else SetStatusAction(failure.Primary.Code, primary.Settings.Provider);
            RefreshProvenance();
            DiagnosticLabel.Text = string.Join(" → ", failure.Attempts.Select(attempt =>
                $"{attempt.DisplayName}: {attempt.Code}" + (attempt.StatusCode is int status ? $" {status}" : ""))) + $" · #{requestId[..8]}";
        }
        catch (Exception error)
        {
            if (generation != _generation || !IsVisible) return;
            ClearResult();
            _errorProviderKind = primary.Settings.Provider;
            if (error is TranslationException translationError)
            {
                SetStatus(TranslationErrorKey(translationError), TranslationErrorArguments(translationError));
                SetStatusAction(translationError.Code, primary.Settings.Provider);
            }
            else SetStatus("ErrorUnknown");
            RefreshProvenance();
            var classified = error as TranslationException;
            DiagnosticLabel.Text = $"{classified?.Code ?? TranslationErrorCode.Unknown}" +
                (classified?.StatusCode is int status ? $" · HTTP {status}" : "") + $" · #{requestId[..8]}";
            _storage.Log(requestId, "translate", primary.Settings.Provider.ToString(), watch.ElapsedMilliseconds, "failed", classified?.Code, classified?.StatusCode);
        }
        finally
        {
            if (generation == _generation)
            {
                _pending = null;
                _streamingPreview?.Dispose();
                _streamingPreview = null;
                CancelButton.Visibility = Visibility.Collapsed;
                TranslateButton.Content = UiLocalization.Get("Translate");
                RefreshSourceReview();
                UpdateInputState();
            }
        }
    }

    private void CancelRequest()
    {
        _generation++;
        var wasStreaming = _streamingPreview is not null;
        _streamingPreview?.Dispose();
        _streamingPreview = null;
        _pending?.Cancel();
        _pending = null;
        if (!_ready) return;
        if (wasStreaming) { ClearResult(); SetStatus("StatusCanceled"); }
        CancelButton.Visibility = Visibility.Collapsed;
        TranslateButton.Content = UiLocalization.Get("Translate");
        RefreshSourceReview();
        UpdateInputState();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelRequest(); SetStatus("StatusCanceled"); }

    private void ClearResult()
    {
        ResultBox.Clear();
        DiagnosticLabel.Text = "";
        _isStreamingPreview = false;
        ResultLabel.Text = UiLocalization.Get("ResultLabel");
        CopyButton.IsEnabled = false;
        _resultProvider = _resultEndpoint = _resultModel = _resultSource = _resultTarget = null;
        _errorProviderKind = null;
        _resultFellBack = false;
        _errorAttempts = 0;
        ProvenanceLabel.Text = UiLocalization.Get("Provenance");
        _sourceReview = null;
        ReviewDetails.IsExpanded = false;
        RefreshSourceReview();
    }

    private void Source_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        CancelRequest();
        ClearResult();
        CountLabel.Text = UiLocalization.Format("Count", SourceBox.Text.Length);
        CountLabel.SetResourceReference(TextBlock.ForegroundProperty, SourceBox.Text.Length > 3000 ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
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
        if (providerItem.DataContext is AiServiceProfile service)
        {
            _settings.ActiveAiServiceId = service.Id;
            _settings.ApplyActiveAiService();
        }
        _settings.Translation.Provider = provider;
        _settings.Translation.TargetLanguage = (string)language.Tag;
        RefreshLanguageChoices();
        UpdateRequestHint();
        RefreshSourceReview();
        var missingKey = provider == ProviderKind.DeepL ? string.IsNullOrWhiteSpace(_settings.Translation.DeepLApiKey)
            : provider == ProviderKind.OpenAI && string.IsNullOrWhiteSpace(_settings.Translation.OpenAiApiKey);
        if (!TranslationLanguages.Supports(provider, _settings.Translation.TargetLanguage)) SetStatus("ErrorUnsupportedLanguage");
        else if (missingKey && !_settings.AutoFallback)
        {
            SetStatus("StatusKeyMissing", BuildCandidates()[0].DisplayName);
            SetStatusAction(TranslationErrorCode.MissingApiKey, provider);
        }
        else SetStatus("StatusSelectionChanged");
        UpdateInputState();
        try { _storage.Save(_settings); }
        catch { SetStatus("StatusSaveFailed"); }
    }

    private void RefreshLanguageChoices()
    {
        foreach (ComboBoxItem item in LanguageBox.Items)
            item.IsEnabled = TranslationLanguages.Supports(_settings.Translation.Provider, (string)item.Tag);
        ResultBox.FlowDirection = TranslationLanguages.Find(_settings.Translation.TargetLanguage)?.RightToLeft == true
            ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private void UpdateRequestHint()
    {
        RequestHint.Text = UiLocalization.Get("RequestHint");
        RequestHint.ToolTip = null;
        var ai = _settings.Translation;
        var primary = ai.Provider == ProviderKind.GoogleWeb ? "translate.googleapis.com"
            : ai.Provider == ProviderKind.DeepL ? (Uri.TryCreate(ai.DeepLEndpoint, UriKind.Absolute, out var deepL) ? deepL.Host : "DeepL")
            : null;
        if (primary is null)
        {
            try { primary = AiEndpoint.Resolve(ai.AiProtocol, ai.OpenAiEndpoint, ai.OpenAiModel, ai.OpenAiStreaming).Host; }
            catch (TranslationException) { return; }
        }
        if (ReviewRequested)
        {
            RequestHint.Text = UiLocalization.Format("SourceReviewRequestHost", primary);
            RequestHint.ToolTip = RequestHint.Text;
            return;
        }
        RequestHint.Text = UiLocalization.Format("RequestHintHost", primary);
        var backups = BuildCandidates().Skip(1).Select(candidate => candidate.DisplayName).ToArray();
        if (backups.Length == 0) return;
        // Everything that may receive the text is listed, in order; the tooltip is never shorter than the label.
        RequestHint.Text = UiLocalization.Format("RequestHintFallback", primary, string.Join(UiLocalization.Get("ListSeparator"), backups));
        RequestHint.ToolTip = RequestHint.Text;
    }

    /// <summary>Selected service first; then, only if enabled, ticked backups in the user's order.</summary>
    private List<TranslationCandidate> BuildCandidates()
    {
        var primary = _settings.Translation.Copy();
        primary.ReviewSource = ReviewRequested;
        primary.SourceReviewInEnglish = UiLocalization.EffectiveLanguage == UiLanguage.English;
        var primaryKey = primary.Provider switch
        {
            ProviderKind.GoogleWeb => UserSettings.GoogleKey,
            ProviderKind.DeepL => UserSettings.DeepLKey,
            _ => _settings.ActiveAiService is { } active ? UserSettings.AiKey(active) : "ai"
        };
        var candidates = new List<TranslationCandidate> { new(primaryKey, CandidateName(primaryKey), primary) };
        if (ReviewRequested || !_settings.AutoFallback) return candidates;
        foreach (var entry in _settings.FallbackOrder.Where(entry => entry.Enabled && entry.Key != primaryKey))
        {
            var settings = _settings.Translation.Copy();
            if (entry.Key == UserSettings.GoogleKey) settings.Provider = ProviderKind.GoogleWeb;
            else if (entry.Key == UserSettings.DeepLKey) settings.Provider = ProviderKind.DeepL;
            else if (_settings.AiServices.FirstOrDefault(service => UserSettings.AiKey(service) == entry.Key) is { } service)
            {
                settings.Provider = ProviderKind.OpenAI;
                service.ApplyTo(settings);
            }
            else continue;
            candidates.Add(new TranslationCandidate(entry.Key, CandidateName(entry.Key), settings));
        }
        return candidates;
    }

    private string CandidateName(string key) => key switch
    {
        UserSettings.GoogleKey => "Google",
        UserSettings.DeepLKey => "DeepL",
        _ => _settings.AiServices.FirstOrDefault(service => UserSettings.AiKey(service) == key)?.DisplayName() ?? "AI"
    };

    private static string FallbackReasonKey(TranslationErrorCode code) => code switch
    {
        TranslationErrorCode.Timeout => "FallbackReasonTimeout",
        TranslationErrorCode.Network => "FallbackReasonNetwork",
        TranslationErrorCode.RateLimited or TranslationErrorCode.RateLimitCooldown => "FallbackReasonRateLimited",
        TranslationErrorCode.ServerUnavailable => "FallbackReasonServer",
        TranslationErrorCode.Unauthorized or TranslationErrorCode.Forbidden => "FallbackReasonAuth",
        TranslationErrorCode.MissingApiKey => "FallbackReasonMissingKey",
        TranslationErrorCode.UnsupportedLanguage => "FallbackReasonLanguage",
        TranslationErrorCode.InvalidApiKey or TranslationErrorCode.MissingModel or TranslationErrorCode.InvalidModel or
            TranslationErrorCode.InvalidEndpoint or TranslationErrorCode.UnsupportedProtocol or TranslationErrorCode.UnsupportedProvider => "FallbackReasonConfig",
        TranslationErrorCode.IncompleteResponse or TranslationErrorCode.EmptyResponse or TranslationErrorCode.InvalidResponse or
            TranslationErrorCode.StreamingNotSupported or TranslationErrorCode.ResponseTooLarge => "FallbackReasonIncomplete",
        _ => "FallbackReasonOther"
    };

    /// <summary>Formats at render time so a later UI-language switch relabels status arguments.</summary>
    private sealed class LocalizedText(Func<string> render)
    {
        public override string ToString() => render();
    }

    private static LocalizedText Reason(TranslationAttempt attempt) => new(() =>
        UiLocalization.Get(FallbackReasonKey(attempt.Code)) + (attempt.StatusCode is int status ? $" {status}" : ""));

    private static LocalizedText Failures(IEnumerable<TranslationAttempt> attempts)
    {
        var list = attempts.ToArray();
        return new(() => string.Join(UiLocalization.Get("ListSeparator"), list.Select(attempt => $"{attempt.DisplayName} {Reason(attempt)}")));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!CopyButton.IsEnabled || string.IsNullOrWhiteSpace(ResultBox.Text)) return;
        try { Clipboard.SetText(ResultBox.Text); SetStatus("StatusCopied"); }
        catch { SetStatus("StatusClipboardBusy"); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsWindow.Section.General);

    internal void OpenSettings(SettingsWindow.Section section, string? aiServiceId = null)
    {
        if (_settingsOpen) return;
        ResetMouseSelection();
        CancelRequest();
        _capturePending?.Cancel();
        _settingsOpen = true;
        ShowForUser(false);
        var restart = false;
        try
        {
            var dialog = new SettingsWindow(_settings, _storage, section, aiServiceId) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _settings = dialog.Settings;
                var ready = _ready;
                RebuildAiProviderItems();
                _ready = false;
                SelectProvider(_settings.Translation.Provider);
                _ready = ready;
                UiLocalization.SetPreference(_settings.UiLanguage);
                Localize();
                UpdateRequestHint();
                UpdateInputState();
                SetStatus("StatusSettingsSaved");
                if (!App.IsSmokeTest && !App.IsBenchmark) RegisterShortcut();
            }
            if (dialog.UpdateApplied) ShowInfo(InfoKind.RestartPending);
            restart = dialog.RestartRequested;
        }
        finally { _settingsOpen = false; ConfigureMouseSelection(); }
        if (restart) RestartForUpdate();
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
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            checks.Add("Repeated main-window Loaded events cannot launch overlapping smoke acceptance workflows");
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
            string LanguageState() => $"preference={UiLocalization.Preference}, effective={UiLocalization.EffectiveLanguage}, " +
                $"selected={UiLanguageBox.SelectedIndex}, ready={_ready}, suppressed={_suppressUiLangEvent}, settingsOpen={_settingsOpen}";
            var languageBefore = LanguageState();
            UiLanguageBox.SelectedIndex = (int)UiLanguage.English;
            if (SourceBox.Text != sourceBeforeLanguage || ResultBox.Text != resultBeforeLanguage ||
                ((ComboBoxItem)LanguageBox.SelectedItem).Tag?.ToString() != targetBeforeLanguage ||
                ProviderBox.SelectedIndex != providerBeforeLanguage || smokeHandler.RequestCount != requestsBeforeLanguage)
                throw new Exception($"UI language switch changed translation state or issued a request; before: {languageBefore}; after: {LanguageState()}");
            if (UiLocalization.Preference != UiLanguage.English ||
                !ProvenanceLabel.Text.Contains("Google web endpoint (unofficial)", StringComparison.Ordinal))
                throw new Exception($"English provenance was not localized; before: {languageBefore}; after: {LanguageState()}; " +
                    $"provider={_resultProvider}, endpoint={_resultEndpoint}, provenance={ProvenanceLabel.Text}");
            UiLanguageBox.SelectedIndex = (int)UiLanguage.SimplifiedChinese;
            checks.Add("UI language switch preserves source/result/provider/target and localizes provenance without HTTP");
            await RunHeaderLanguageAcceptanceAsync(checks, () => smokeHandler.RequestCount);
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
            await RunStreamingAcceptanceAsync(checks, output);
            await RunFoundationAcceptanceAsync(checks, output);
            await RunServicesAcceptanceAsync(checks, output);
            await RunFallbackAcceptanceAsync(checks, output);
            await RunSourceReviewAcceptanceAsync(checks, output);
            await RunLearningAcceptanceAsync(checks, output, () => smokeHandler.RequestCount);
            await RunCapturePolicyAcceptanceAsync(checks, output, () => smokeHandler.RequestCount);
            await RunMouseSelectionAcceptanceAsync(checks, output, () => smokeHandler.RequestCount);
            await RunInteractionAcceptanceAsync(checks, output);
            await RunAppearanceAcceptanceAsync(checks, output, () => smokeHandler.RequestCount);
            await RunCompatibilityAcceptanceAsync(checks, () => smokeHandler.RequestCount);
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
            File.WriteAllText(output, JsonSerializer.Serialize(new { success = true, checks, workingSetMb = Environment.WorkingSet / 1024 / 1024,
                appearanceNativeCaptures = _appearanceNativeCaptures,
                compatibilityMonitors = _compatibilityMonitors,
                dpiEvidence = "100/150/200 percent WPF rasterization plus work-area unit tests; compatibilityMonitors records actual native moves when --native-checks is enabled" }, new JsonSerializerOptions { WriteIndented = true }));
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
        if (!NativeMethods.RegisterHotKey(_handle, testHotkeyId, 0x4007, 0x7A))
            throw new Exception("Test shortcut Ctrl+Alt+Shift+F11 could not be registered");
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

    private static void RenderWindowTo(Window window, string path, double scale = 1) => RenderElementTo(window, path, scale);

    // RenderTargetBitmap captures WPF, not the DWM backdrop/title bar. Composite on the theme's solid
    // fallback rather than publishing misleading black/transparent screenshots; native crops are separate.
    private static void RenderElementTo(FrameworkElement element, string path, double scale = 1)
    {
        var bounds = new System.Windows.Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)Application.Current.FindResource("ApplicationBackgroundBrush"), null, bounds);
            // VisualBrush removes the popup's parent offset (RenderTargetBitmap.Render(ContextMenu)
            // otherwise clips its first rows when the native popup is placed against the work-area edge).
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
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
