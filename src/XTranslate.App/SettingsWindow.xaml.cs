using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTranslate.Core;

namespace XTranslate.App;

public partial class SettingsWindow : Window
{
    private readonly AppStorage _storage;
    private readonly UiLanguage _originalLanguage;
    private bool _suppressLanguagePreview;
    private string _errorKey = "";
    private bool _initialized;
    private bool _loadingProfile;
    private string _credentialEndpoint = "";
    private AiServiceProfile? _editing;
    private UpdateRelease? _release;
    private CancellationTokenSource? _updateCancel;
    public UserSettings Settings { get; }
    /// <summary>Program files were replaced; the owner restarts (now or later). Independent of DialogResult.</summary>
    public bool UpdateApplied { get; private set; }
    public bool RestartRequested { get; private set; }

    public enum Section { General, Services, Fallback, Privacy, About }

    public SettingsWindow(UserSettings settings, AppStorage storage, Section section = Section.General, string? aiServiceId = null)
    {
        InitializeComponent();
        _storage = storage;
        Settings = settings.Copy();
        Settings.EnsureAiService();
        _originalLanguage = settings.UiLanguage;
        HotkeyBox.Text = settings.EffectiveCaptureHotkey;
        AutoTranslateBox.IsChecked = settings.AutoTranslateSelection;
        AutoHideBox.IsChecked = settings.AutoHideSelectionPopup;
        CopyCaptureBox.IsChecked = settings.AllowCopyCapture;
        DeepLKeyBox.Password = settings.Translation.DeepLApiKey;
        DeepLProBox.IsChecked = settings.Translation.DeepLEndpoint == "https://api.deepl.com/v2/translate";
        _suppressLanguagePreview = true;
        UiLanguageBox.SelectedIndex = (int)settings.UiLanguage;
        _suppressLanguagePreview = false;
        foreach (var service in Settings.AiServices)
            AiServiceBox.Items.Add(new ComboBoxItem { Tag = service, Content = service.DisplayName() });
        AutoFallbackBox.IsChecked = Settings.AutoFallback;
        AutoUpdateBox.IsChecked = Settings.AutoCheckUpdates;
        AutoUpdateBox.IsEnabled = CheckUpdateButton.IsEnabled = UpdateFromFileButton.IsEnabled = AppUpdates.CanSelfUpdate;
        _initialized = true;
        AiServiceBox.SelectedItem = AiServiceBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => ((AiServiceProfile)item.Tag).Id == aiServiceId)
            ?? AiServiceBox.Items.Cast<ComboBoxItem>().First(item => item.Tag == Settings.ActiveAiService);
        SectionTabs.SelectedIndex = (int)section;
        Localize();
        UiLocalization.Changed += OnLanguageChanged;
        Closed += (_, _) =>
        {
            UiLocalization.Changed -= OnLanguageChanged;
            _updateCancel?.Cancel();
            if (DialogResult != true) UiLocalization.SetPreference(_originalLanguage);
        };
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Localize();

    private void Localize()
    {
        Title = UiLocalization.Get("SettingsTitle");
        GeneralTab.Header = UiLocalization.Get("SettingsTabGeneral");
        ServicesTab.Header = UiLocalization.Get("SettingsTabServices");
        FallbackTab.Header = UiLocalization.Get("SettingsTabFallback");
        PrivacyTab.Header = UiLocalization.Get("SettingsTabPrivacy");
        AboutTab.Header = UiLocalization.Get("SettingsTabAbout");
        PrivacyTitle.Text = UiLocalization.Get("PrivacyTitle");
        PrivacyHelp.Text = UiLocalization.Get("PrivacyHelp");
        AboutTitle.Text = UiLocalization.Get("AboutTitle");
        VersionLabel.Text = UiLocalization.Format("AboutVersion", AppUpdates.Current);
        EditionLabel.Text = UiLocalization.Get(AppUpdates.Flavor switch
        {
            UpdateFlavor.Portable => "EditionPortable", UpdateFlavor.Lite => "EditionLite", _ => "EditionDev"
        });
        CheckUpdateButton.Content = UiLocalization.Get("UpdateCheck");
        InstallUpdateButton.Content = UiLocalization.Get(UpdateApplied ? "InfoRestartNow" : "UpdateInstall");
        UpdateFromFileButton.Content = UiLocalization.Get("UpdateFromFile");
        ReleasePageButton.Content = UiLocalization.Get("UpdateReleasePage");
        SetWrappedContent(AutoUpdateBox, "UpdateAuto");
        UpdateHelp.Text = UiLocalization.Get("UpdateHelp");
        DataFolderLabel.Text = UiLocalization.Format("DataFolder", _storage.DirectoryPath);
        OpenDataFolderButton.Content = UiLocalization.Get("OpenDataFolder");
        if (_updateStatus is { } status) UpdateStatusLabel.Text = status();
        UiLanguageLabel.Text = UiLocalization.Get("UiLanguageLabel");
        UiAutoItem.Content = UiLocalization.Get("UiAuto");
        UiChineseItem.Content = UiLocalization.Get("UiChinese");
        UiEnglishItem.Content = UiLocalization.Get("UiEnglish");
        ShortcutsHeader.Text = UiLocalization.Get("SettingsShortcuts");
        HotkeySettingLabel.Text = UiLocalization.Get("SettingsHotkeyLabel");
        HotkeyHelp.Text = UiLocalization.Get("SettingsHotkeyHelp");
        ResetHotkeyButton.Content = UiLocalization.Get("SettingsHotkeyReset");
        SetWrappedContent(AutoTranslateBox, "SettingsAutoTranslate");
        SetWrappedContent(AutoHideBox, "SettingsAutoHide");
        SetWrappedContent(CopyCaptureBox, "SettingsCopyCapture");
        CaptureHelp.Text = UiLocalization.Get("SettingsCaptureHelp");
        GoogleTitle.Text = UiLocalization.Get("SettingsGoogleTitle");
        GoogleHelp.Text = UiLocalization.Get("SettingsGoogleHelp");
        DeepLTitle.Text = UiLocalization.Get("SettingsDeepLTitle");
        DeepLKeyLabel.Text = UiLocalization.Get("SettingsApiKey");
        DeepLProBox.Content = UiLocalization.Get("SettingsDeepLPro");
        OpenAiTitle.Text = UiLocalization.Get("SettingsOpenAiTitle");
        AiServiceLabel.Text = UiLocalization.Get("SettingsAiService");
        AddAiServiceButton.Content = UiLocalization.Get("SettingsAiAdd");
        RemoveAiServiceButton.Content = UiLocalization.Get("SettingsAiRemove");
        AiNameLabel.Text = UiLocalization.Get("SettingsAiName");
        AiProtocolLabel.Text = UiLocalization.Get("SettingsAiProtocol");
        ProtocolOpenAiItem.Content = UiLocalization.Get("ProtocolOpenAiChat");
        ProtocolAnthropicItem.Content = UiLocalization.Get("ProtocolAnthropic");
        ProtocolGeminiItem.Content = UiLocalization.Get("ProtocolGemini");
        AiKeyUnavailableLabel.Text = UiLocalization.Get("SettingsKeyUnavailable");
        foreach (ComboBoxItem item in AiServiceBox.Items) item.Content = ((AiServiceProfile)item.Tag).DisplayName();
        FallbackTitle.Text = UiLocalization.Get("SettingsFallbackTitle");
        SetWrappedContent(AutoFallbackBox, "SettingsAutoFallback");
        FallbackHelp.Text = UiLocalization.Get("SettingsFallbackHelp");
        FallbackUpButton.Content = UiLocalization.Get("SettingsFallbackUp");
        FallbackDownButton.Content = UiLocalization.Get("SettingsFallbackDown");
        RefreshFallbackList();
        EndpointLabel.Text = UiLocalization.Get("SettingsEndpoint");
        EndpointBox.ToolTip = UiLocalization.Get("SettingsEndpointTip");
        EndpointHelp.Text = UiLocalization.Get("SettingsEndpointHelp");
        UpdateEndpointPreview();
        OpenAiKeyLabel.Text = UiLocalization.Get("SettingsApiKey");
        ForgetAiKeyButton.Content = ForgetDeepLKeyButton.Content = UiLocalization.Get("SettingsForgetKey");
        ForgetAiKeyButton.ToolTip = ForgetDeepLKeyButton.ToolTip = UiLocalization.Get("SettingsForgetKeyHelp");
        ModelLabel.Text = UiLocalization.Get("SettingsModel");
        SetWrappedContent(OpenAiStreamingBox, "SettingsAiStreaming");
        AiStreamingHelp.Text = UiLocalization.Get("SettingsAiStreamingHelp");
        PlannedProvidersHelp.Text = UiLocalization.Get("SettingsPlannedProviders");
        SecurityHelp.Text = UiLocalization.Get("SettingsSecurityHelp");
        SaveButton.Content = UiLocalization.Get("Save");
        CancelButton.Content = UiLocalization.Get("Close");
        ErrorLabel.Text = _errorKey.Length == 0 ? "" : UiLocalization.Get(_errorKey);
        _suppressLanguagePreview = true;
        UiLanguageBox.SelectedIndex = (int)UiLocalization.Preference;
        _suppressLanguagePreview = false;
    }

    private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguagePreview || UiLanguageBox.SelectedIndex < 0) return;
        UiLocalization.SetPreference((UiLanguage)UiLanguageBox.SelectedIndex);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        if (!HotkeyGesture.TryParse(HotkeyBox.Text, out var gesture)) { SetError("SettingsHotkeyError"); Reveal(HotkeyBox); return; }
        if (DeepLKeyBox.Password.Trim().Any(char.IsControl)) { SetError("ErrorInvalidApiKey"); Reveal(DeepLKeyBox); return; }
        // Every profile must be usable; the failing one is selected so the message has context.
        foreach (var service in Settings.AiServices)
        {
            string? error = null;
            if (string.IsNullOrWhiteSpace(service.Model)) error = "SettingsModelError";
            else if (service.ApiKey.Any(char.IsControl)) error = "ErrorInvalidApiKey";
            else
            {
                try { AiEndpoint.Resolve(service.Protocol, service.Endpoint, service.Model, service.Streaming); }
                catch (TranslationException failure) { error = EndpointErrorKey(failure); }
            }
            if (error is null) continue;
            AiServiceBox.SelectedItem = AiServiceBox.Items.Cast<ComboBoxItem>().First(item => item.Tag == service);
            SetError(error);
            Reveal(error == "SettingsModelError" ? ModelBox : EndpointBox);
            return;
        }
        Settings.CaptureHotkey = gesture.Display;
        Settings.AutoTranslateSelection = AutoTranslateBox.IsChecked == true;
        Settings.AutoHideSelectionPopup = AutoHideBox.IsChecked == true;
        Settings.AllowCopyCapture = CopyCaptureBox.IsChecked == true;
        Settings.UiLanguage = (UiLanguage)Math.Max(0, UiLanguageBox.SelectedIndex);
        Settings.AutoFallback = AutoFallbackBox.IsChecked == true;
        Settings.AutoCheckUpdates = AutoUpdateBox.IsChecked == true;
        Settings.Translation.DeepLApiKey = DeepLKeyBox.Password.Trim();
        Settings.Translation.DeepLEndpoint = DeepLProBox.IsChecked == true ? "https://api.deepl.com/v2/translate" : "https://api-free.deepl.com/v2/translate";
        try { _storage.Save(Settings); _errorKey = ""; DialogResult = true; }
        catch { SetError("SettingsSaveError"); }
    }

    /// <summary>Selects the section that contains <paramref name="element"/>, scrolls to it and focuses it.</summary>
    internal void Reveal(FrameworkElement element)
    {
        DependencyObject? node = element;
        while (node is not null and not TabItem) node = LogicalTreeHelper.GetParent(node);
        if (node is TabItem tab) SectionTabs.SelectedItem = tab;
        UpdateLayout();
        element.BringIntoView();
        if (element.Focusable) element.Focus();
    }

    private Func<string>? _updateStatus;

    private void ShowUpdateStatus(Func<string> text, bool busy = false)
    {
        _updateStatus = text;
        UpdateStatusLabel.Text = text();
        UpdateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = UpdateFromFileButton.IsEnabled = !busy && AppUpdates.CanSelfUpdate && !UpdateApplied;
        InstallUpdateButton.IsEnabled = !busy;
    }

    private static string UpdateErrorText(UpdateException error) => error.Code switch
    {
        UpdateErrorCode.SourceUnavailable => UiLocalization.Get("UpdateSourceUnavailable"),
        UpdateErrorCode.Network => UiLocalization.Get("UpdateNetworkError"),
        _ => UiLocalization.Format("UpdateFailed", UiLocalization.Get(error.Code switch
        {
            UpdateErrorCode.Integrity => "UpdateIntegrity",
            UpdateErrorCode.NotNewer => "UpdateNotNewer",
            UpdateErrorCode.Apply => "UpdateApply",
            _ => "UpdatePackage"
        }))
    };

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!AppUpdates.CanSelfUpdate) return;
        _updateCancel = new CancellationTokenSource();
        ShowUpdateStatus(() => UiLocalization.Get("UpdateChecking"), busy: true);
        try
        {
            using var client = new UpdateClient();
            _release = await client.GetLatestAsync(AppUpdates.Flavor!.Value, AppUpdates.Current, _updateCancel.Token);
            Settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            var release = _release;
            var newer = release.Version > AppUpdates.Current;
            InstallUpdateButton.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
            ReleasePageButton.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
            ShowUpdateStatus(() => newer ? UiLocalization.Format("UpdateAvailable", release.Version, AppUpdates.Current)
                                         : UiLocalization.Format("UpdateLatest", AppUpdates.Current));
        }
        catch (UpdateException error) { ShowUpdateStatus(() => UpdateErrorText(error)); }
        catch (OperationCanceledException) { ShowUpdateStatus(() => UiLocalization.Get("UpdateNetworkError")); }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateApplied)
        {
            // Restart: save first so pending edits are not lost; stay open if they are invalid.
            Save_Click(sender, e);
            if (DialogResult != true) return;
            RestartRequested = true;
            return;
        }
        if (_release is not { } release) return;
        _updateCancel = new CancellationTokenSource();
        var zip = Path.Combine(Path.GetTempPath(), "xtranslate-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            ShowUpdateStatus(() => UiLocalization.Format("UpdateDownloading", release.Version), busy: true);
            using var client = new UpdateClient();
            await client.DownloadAsync(release, AppUpdates.Current, zip, new Progress<double>(value => UpdateProgress.Value = value), _updateCancel.Token);
            ShowUpdateStatus(() => UiLocalization.Get("UpdateVerifying"), busy: true);
            await AppUpdates.ApplyPackageAsync(zip, release.Sha256);
            MarkApplied();
        }
        catch (UpdateException error) { ShowUpdateStatus(() => UpdateErrorText(error)); }
        catch (OperationCanceledException) { ShowUpdateStatus(() => UiLocalization.Get("UpdateNetworkError")); }
        finally { try { File.Delete(zip); } catch (IOException) { } }
    }

    private async void UpdateFromFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "XTranslate (*.zip)|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var sha = AppUpdates.ReadSidecarSha256(picker.FileName);
            if (sha is null && MessageBox.Show(this, UiLocalization.Get("UpdateNoChecksum"), UiLocalization.Get("UpdateConfirmTitle"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            ShowUpdateStatus(() => UiLocalization.Get("UpdateVerifying"), busy: true);
            await AppUpdates.ApplyPackageAsync(picker.FileName, sha);
            MarkApplied();
        }
        catch (UpdateException error) { ShowUpdateStatus(() => UpdateErrorText(error)); }
    }

    private void MarkApplied()
    {
        UpdateApplied = true;
        InstallUpdateButton.Visibility = Visibility.Visible;
        InstallUpdateButton.Content = UiLocalization.Get("InfoRestartNow");
        ShowUpdateStatus(() => UiLocalization.Get("UpdateReady"));
    }

    private void ReleasePage_Click(object sender, RoutedEventArgs e)
    {
        if (_release is { } release && UpdateClient.IsAllowed(release.PageUrl))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.PageUrl.AbsoluteUri) { UseShellExecute = true });
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_storage.DirectoryPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { _storage.DirectoryPath } });
    }

    private static string EndpointErrorKey(TranslationException error) => error.Code switch
    {
        TranslationErrorCode.UnsupportedProtocol => "ErrorUnsupportedProtocol",
        TranslationErrorCode.InvalidModel => "ErrorInvalidModel",
        _ => "SettingsEndpointError"
    };

    /// <summary>Writes the editor fields back into the profile being edited (this dialog's copy only).</summary>
    private void CommitEditor()
    {
        if (_editing is null) return;
        _editing.Name = AiNameBox.Text.Trim();
        _editing.Protocol = (AiProtocol)Math.Max(0, AiProtocolBox.SelectedIndex);
        _editing.Endpoint = EndpointBox.Text.Trim();
        _editing.ApiKey = OpenAiKeyBox.Password.Trim();
        _editing.Model = ModelBox.Text.Trim();
        _editing.Streaming = OpenAiStreamingBox.IsChecked == true;
    }

    private void LoadEditor(AiServiceProfile service)
    {
        _loadingProfile = true;
        _editing = service;
        AiNameBox.Text = service.Name;
        AiProtocolBox.SelectedIndex = (int)service.Protocol;
        EndpointBox.Text = service.Endpoint;
        OpenAiKeyBox.Password = service.ApiKey;
        ModelBox.Text = service.Model;
        OpenAiStreamingBox.IsChecked = service.Streaming;
        _credentialEndpoint = service.Endpoint;
        _loadingProfile = false;
        AiKeyUnavailableLabel.Visibility = service.SecretUnavailable ? Visibility.Visible : Visibility.Collapsed;
        RemoveAiServiceButton.IsEnabled = Settings.AiServices.Count > 1;
        UpdateEndpointPreview();
    }

    private void AiService_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || AiServiceBox.SelectedItem is not ComboBoxItem { Tag: AiServiceProfile service } || service == _editing) return;
        CommitEditor();
        _errorKey = "";
        ErrorLabel.Text = ""; // Errors belong to the previous service.
        LoadEditor(service);
    }

    private void AddAiService_Click(object sender, RoutedEventArgs e)
    {
        AiTemplateMenu.Items.Clear();
        foreach (var template in AiServiceTemplates.All)
        {
            var item = new MenuItem { Header = template.Name.Length > 0 ? template.Name : UiLocalization.Get("TemplateCustom") };
            item.Click += (_, _) => AddService(template);
            AiTemplateMenu.Items.Add(item);
        }
        AiTemplateMenu.PlacementTarget = AddAiServiceButton;
        AiTemplateMenu.IsOpen = true;
    }

    internal void AddService(AiServiceTemplate template)
    {
        CommitEditor();
        var baseName = template.Name.Length > 0 ? template.Name : UiLocalization.Get("SettingsAiNameDefault");
        var name = baseName;
        for (var index = 2; Settings.AiServices.Any(service => service.Name == name); index++) name = $"{baseName} {index}";
        var service = AiServiceTemplates.Create(template, name);
        Settings.AiServices.Add(service);
        var item = new ComboBoxItem { Tag = service, Content = service.DisplayName() };
        AiServiceBox.Items.Add(item);
        AiServiceBox.SelectedItem = item;
        RefreshFallbackList();
        (template.Endpoint.Length == 0 ? EndpointBox : (Control)OpenAiKeyBox).Focus();
    }

    private void RemoveAiService_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || Settings.AiServices.Count <= 1) return;
        var index = AiServiceBox.SelectedIndex;
        var removed = _editing;
        _editing = null; // Do not commit the removed profile's fields into its neighbour.
        Settings.AiServices.Remove(removed);
        AiServiceBox.Items.RemoveAt(index);
        AiServiceBox.SelectedIndex = Math.Min(index, AiServiceBox.Items.Count - 1);
        RefreshFallbackList(); // The removed service also leaves the backup order.
    }

    private void AiName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingProfile || _editing is null || AiServiceBox.SelectedItem is not ComboBoxItem item) return;
        _editing.Name = AiNameBox.Text.Trim();
        item.Content = _editing.DisplayName();
        RefreshFallbackList();
    }

    /// <summary>Rebuilds the ordered backup list from this dialog's copy; keeps the selected row.</summary>
    private void RefreshFallbackList()
    {
        if (!_initialized) return;
        Settings.NormalizeFallbackOrder();
        var selectedKey = (FallbackList.SelectedItem as ListBoxItem)?.Tag as string;
        FallbackList.Items.Clear();
        foreach (var entry in Settings.FallbackOrder)
        {
            var name = ServiceName(entry.Key);
            var box = new CheckBox { Content = name, IsChecked = entry.Enabled, Focusable = false };
            box.Click += (_, _) => entry.Enabled = box.IsChecked == true;
            var row = new ListBoxItem { Content = box, Tag = entry.Key };
            System.Windows.Automation.AutomationProperties.SetName(row, name);
            row.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Space) return;
                box.IsChecked = !(box.IsChecked == true);
                entry.Enabled = box.IsChecked == true;
                args.Handled = true;
            };
            FallbackList.Items.Add(row);
        }
        FallbackList.SelectedItem = FallbackList.Items.Cast<ListBoxItem>().FirstOrDefault(row => (string)row.Tag == selectedKey);
        UpdateFallbackControls();
    }

    private string ServiceName(string key) => key switch
    {
        UserSettings.GoogleKey => UiLocalization.Get("GoogleProvider"),
        UserSettings.DeepLKey => UiLocalization.Get("DeepLProvider"),
        _ => UiLocalization.Format("OpenAiProvider",
            Settings.AiServices.FirstOrDefault(service => UserSettings.AiKey(service) == key)?.DisplayName() ?? key)
    };

    private void UpdateFallbackControls()
    {
        var enabled = AutoFallbackBox.IsChecked == true;
        FallbackList.IsEnabled = enabled;
        FallbackUpButton.IsEnabled = enabled && FallbackList.SelectedIndex > 0;
        FallbackDownButton.IsEnabled = enabled && FallbackList.SelectedIndex >= 0 && FallbackList.SelectedIndex < FallbackList.Items.Count - 1;
    }

    private void AutoFallback_Click(object sender, RoutedEventArgs e) => UpdateFallbackControls();
    private void FallbackList_Changed(object sender, SelectionChangedEventArgs e) => UpdateFallbackControls();
    private void FallbackUp_Click(object sender, RoutedEventArgs e) => MoveFallback(-1);
    private void FallbackDown_Click(object sender, RoutedEventArgs e) => MoveFallback(1);

    internal void MoveFallback(int offset)
    {
        var index = FallbackList.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Settings.FallbackOrder.Count) return;
        (Settings.FallbackOrder[index], Settings.FallbackOrder[target]) = (Settings.FallbackOrder[target], Settings.FallbackOrder[index]);
        RefreshFallbackList();
        FallbackList.SelectedIndex = target;
        ((ListBoxItem)FallbackList.SelectedItem).Focus();
    }

    private void AiProtocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingProfile) UpdateEndpointPreview();
    }

    private void AiStreaming_Click(object sender, RoutedEventArgs e) => UpdateEndpointPreview();

    private void Model_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loadingProfile) UpdateEndpointPreview();
    }

    private void ForgetDeepLKey_Click(object sender, RoutedEventArgs e)
    {
        DeepLKeyBox.Clear();
        Settings.DeepLSecretUnavailable = false;
    }

    private void ForgetAiKey_Click(object sender, RoutedEventArgs e)
    {
        OpenAiKeyBox.Clear();
        if (_editing is not null) _editing.SecretUnavailable = false;
        AiKeyUnavailableLabel.Visibility = Visibility.Collapsed;
    }

    private void Endpoint_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _loadingProfile) return;
        // A key (readable or preserved ciphertext) is bound to its origin and never follows a new host.
        if ((OpenAiKeyBox.Password.Length > 0 || _editing?.SecretUnavailable == true) &&
            !AiEndpoint.SameOrigin(_credentialEndpoint, EndpointBox.Text))
        {
            OpenAiKeyBox.Clear();
            if (_editing is not null) _editing.SecretUnavailable = false;
            AiKeyUnavailableLabel.Visibility = Visibility.Collapsed;
            SetError("SettingsKeyOriginChanged");
        }
        UpdateEndpointPreview();
    }

    private void AiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loadingProfile || OpenAiKeyBox.Password.Length == 0) return;
        _credentialEndpoint = EndpointBox.Text;
        if (_editing is not null) _editing.SecretUnavailable = false; // A new key replaces the unreadable one.
        AiKeyUnavailableLabel.Visibility = Visibility.Collapsed;
    }

    private void UpdateEndpointPreview()
    {
        var protocol = (AiProtocol)Math.Max(0, AiProtocolBox.SelectedIndex);
        var model = ModelBox.Text.Trim();
        try
        {
            // An empty Gemini model is shown as a placeholder instead of an error while typing.
            var placeholder = protocol == AiProtocol.GeminiGenerateContent && model.Length == 0;
            var uri = AiEndpoint.Resolve(protocol, EndpointBox.Text, placeholder ? "MODEL" : model, OpenAiStreamingBox.IsChecked == true).AbsoluteUri;
            EndpointPreviewLabel.Text = UiLocalization.Format("SettingsEndpointPreview", placeholder ? uri.Replace("/MODEL:", "/{model}:") : uri);
        }
        catch (TranslationException error) { EndpointPreviewLabel.Text = UiLocalization.Get(EndpointErrorKey(error)); }
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e) => HotkeyBox.Text = "Ctrl+Q";

    private void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.Escape) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        var modifiers = Keyboard.Modifiers;
        var text = (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
                   (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
                   (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") +
                   (key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        if (!modifiers.HasFlag(ModifierKeys.Windows) && HotkeyGesture.TryParse(text, out var gesture))
        {
            HotkeyBox.Text = gesture.Display;
            _errorKey = "";
            ErrorLabel.Text = "";
        }
        else SetError("SettingsHotkeyError");
    }

    private void SetError(string key)
    {
        _errorKey = key;
        ErrorLabel.Text = UiLocalization.Get(key);
    }

    private static void SetWrappedContent(CheckBox box, string key)
    {
        box.Content = new TextBlock
        {
            Text = UiLocalization.Get(key),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 470
        };
    }
}
