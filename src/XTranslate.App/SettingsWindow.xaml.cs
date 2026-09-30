using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace XTranslate.App;

public partial class SettingsWindow : Window
{
    private readonly AppStorage _storage;
    private readonly UiLanguage _originalLanguage;
    private bool _suppressLanguagePreview;
    private string _errorKey = "";
    public UserSettings Settings { get; }

    public SettingsWindow(UserSettings settings, AppStorage storage)
    {
        InitializeComponent();
        _storage = storage;
        Settings = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
        _originalLanguage = settings.UiLanguage;
        AltHotkeyBox.IsChecked = settings.UseCtrlAltQ;
        AutoTranslateBox.IsChecked = settings.AutoTranslateSelection;
        AutoHideBox.IsChecked = settings.AutoHideSelectionPopup;
        CopyCaptureBox.IsChecked = settings.AllowCopyCapture;
        DeepLKeyBox.Password = settings.Translation.DeepLApiKey;
        DeepLProBox.IsChecked = settings.Translation.DeepLEndpoint == "https://api.deepl.com/v2/translate";
        EndpointBox.Text = settings.Translation.OpenAiEndpoint;
        OpenAiKeyBox.Password = settings.Translation.OpenAiApiKey;
        ModelBox.Text = settings.Translation.OpenAiModel;
        _suppressLanguagePreview = true;
        UiLanguageBox.SelectedIndex = (int)settings.UiLanguage;
        _suppressLanguagePreview = false;
        Localize();
        UiLocalization.Changed += OnLanguageChanged;
        Closed += (_, _) =>
        {
            UiLocalization.Changed -= OnLanguageChanged;
            if (DialogResult != true) UiLocalization.SetPreference(_originalLanguage);
        };
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Localize();

    private void Localize()
    {
        Title = UiLocalization.Get("SettingsTitle");
        IntroText.Text = UiLocalization.Get("SettingsIntro");
        UiLanguageLabel.Text = UiLocalization.Get("UiLanguageLabel");
        UiAutoItem.Content = UiLocalization.Get("UiAuto");
        UiChineseItem.Content = UiLocalization.Get("UiChinese");
        UiEnglishItem.Content = UiLocalization.Get("UiEnglish");
        ShortcutsHeader.Text = UiLocalization.Get("SettingsShortcuts");
        SetWrappedContent(AltHotkeyBox, "SettingsAltHotkey");
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
        EndpointLabel.Text = UiLocalization.Get("SettingsEndpoint");
        EndpointBox.ToolTip = UiLocalization.Get("SettingsEndpointTip");
        OpenAiKeyLabel.Text = UiLocalization.Get("SettingsApiKey");
        ModelLabel.Text = UiLocalization.Get("SettingsModel");
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
        var endpoint = EndpointBox.Text.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            SetError("SettingsEndpointError");
            return;
        }
        if (string.IsNullOrWhiteSpace(ModelBox.Text)) { SetError("SettingsModelError"); return; }
        Settings.UseCtrlAltQ = AltHotkeyBox.IsChecked == true;
        Settings.AutoTranslateSelection = AutoTranslateBox.IsChecked == true;
        Settings.AutoHideSelectionPopup = AutoHideBox.IsChecked == true;
        Settings.AllowCopyCapture = CopyCaptureBox.IsChecked == true;
        Settings.UiLanguage = (UiLanguage)Math.Max(0, UiLanguageBox.SelectedIndex);
        Settings.Translation.DeepLApiKey = DeepLKeyBox.Password.Trim();
        Settings.Translation.DeepLEndpoint = DeepLProBox.IsChecked == true ? "https://api.deepl.com/v2/translate" : "https://api-free.deepl.com/v2/translate";
        Settings.Translation.OpenAiEndpoint = endpoint;
        Settings.Translation.OpenAiApiKey = OpenAiKeyBox.Password.Trim();
        Settings.Translation.OpenAiModel = ModelBox.Text.Trim();
        try { _storage.Save(Settings); _errorKey = ""; DialogResult = true; }
        catch { SetError("SettingsSaveError"); }
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
