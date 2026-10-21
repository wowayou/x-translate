using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XTranslate.App.Ocr;
using XTranslate.Core;
using XTranslate.Ocr.Windows;

namespace XTranslate.App;

public partial class OcrWindow : Window
{
    private readonly ILocalOcrEngine _engine;
    private readonly Func<string,bool> _confirm;
    private readonly Func<string,CancellationToken,Task<OcrImage>> _load;
    private readonly Func<CancellationToken,Task<OcrImage?>>? _capture;
    private readonly Func<string,TranslationSettings,CancellationToken,Task<TranslationResult>>? _translate;
    private readonly UserSettings _settings;
    private CancellationTokenSource? _operation;
    private long _generation;
    private OcrImage? _image;
    private bool _closed;
    private bool _recognized;
    private bool _updatingSource;
    private string _statusKey="";
    private object[] _statusArgs=Array.Empty<object>();
    internal string? AcceptedText { get; private set; }
    internal string StatusKey => _statusKey;
    internal Task Loading { get; private set; }=Task.CompletedTask;
    internal OcrWindow(ILocalOcrEngine? engine=null,Func<string,bool>? confirm=null,
        Func<string,CancellationToken,Task<OcrImage>>? load=null,Func<CancellationToken,Task<OcrImage?>>? capture=null,
        Func<string,TranslationSettings,CancellationToken,Task<TranslationResult>>? translate=null,UserSettings? settings=null)
    {
        _engine=engine ?? new WindowsOcrEngine();_load=load ?? OcrImageLoader.LoadAsync;
        _capture=capture;_translate=translate;_settings=settings?.Copy() ?? new UserSettings();_settings.EnsureAiService();
        _confirm=confirm ?? (message => MessageBox.Show(this,message,UiLocalization.Get("OcrTitle"),MessageBoxButton.YesNo,
            MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes);
        InitializeComponent();WindowPlacement.Attach(this);BuildServices();
        // User's Capture click, not opening/checking this control, authorizes one selected-service request.
        AutoTranslateBox.IsChecked=_translate is not null;Localize();
        RoutedEventHandler? first=null;
        first=(_,_) => {Loaded-=first;Loading=LoadLanguagesAsync();};
        Loaded+=first;UiLocalization.Changed+=Changed;Closing+=OnClosing;
        LanguageBox.SelectionChanged+=(_,_) => Refresh();
        ServiceBox.SelectionChanged+=(_,_) => {Stop();ClearTranslation();RefreshTargets();RefreshRequestHint();Refresh();};
        TargetBox.SelectionChanged+=(_,_) => {Stop();ClearTranslation();RefreshRequestHint();Refresh();};
        AutoTranslateBox.Checked+=(_,_) => Refresh();AutoTranslateBox.Unchecked+=(_,_) => Refresh();
        Closed+=(_,_) =>
        {
            _closed=true;Stop();_image?.Dispose();_image=null;ImagePreview.Source=null;TextBox.Clear();ResultBox.Clear();
            UiLocalization.Changed-=Changed;
        };
        Refresh();
    }
    private void Changed(object? sender,EventArgs e) => Localize();
    private void Localize()
    {
        Title=Heading.Text=UiLocalization.Get("OcrTitle");PrivacyHelp.Text=UiLocalization.Get("OcrPrivacy");
        OpenImageButton.Content=UiLocalization.Get("OcrOpenImage");RecognizeButton.Content=UiLocalization.Get("OcrRecognize");
        TranslateButton.Content=UiLocalization.Get("OcrTranslate");TargetLabel.Text=UiLocalization.Get("TargetLabel");
        CopyTranslationButton.Content=UiLocalization.Get("Copy");ResultLabel.Text=UiLocalization.Get("OcrResult");ServiceLabel.Text=UiLocalization.Get("ProviderLabel");
        foreach(ComboBoxItem item in ServiceBox.Items)
            if(item.DataContext is not AiServiceProfile) item.Content=UiLocalization.Get((ProviderKind)item.Tag==ProviderKind.GoogleWeb?"GoogleProvider":"DeepLProvider");
        AutoTranslateBox.Content=new TextBlock{Text=UiLocalization.Get("OcrAutoTranslate"),TextWrapping=TextWrapping.Wrap};
        LanguageLabel.Text=UiLocalization.Get("OcrLanguage");ImagePlaceholder.Text=UiLocalization.Get("OcrImagePlaceholder");
        TextLabel.Text=UiLocalization.Get("OcrText");ReviewHelp.Text=UiLocalization.Get("OcrReviewHelp");
        UseTextButton.Content=UiLocalization.Get("OcrUseText");CancelButton.Content=UiLocalization.Get("Cancel");
        if(_statusKey.Length>0) Status.Text=UiLocalization.Format(_statusKey,_statusArgs);
        ImageInfo.Text=_image is null?"":UiLocalization.Format("OcrImageInfo",_image.Width,_image.Height);
        RefreshRequestHint();Refresh();
    }
    private void OnClosing(object? sender,CancelEventArgs e) => Stop();
    private void Cancel_Click(object sender,RoutedEventArgs e) => Close();
    private void Stop_Click(object sender,RoutedEventArgs e)
    {
        Stop();
        if(_operation is null && _image is null && LanguageBox.Items.Count==0) _=LoadLanguagesAsync();
    }
    private void Stop()
    {
        _generation++;_operation?.Cancel();
        if(!_closed){SetStatus("OcrCancelled");Refresh();}
    }
    private void SetStatus(string key,params object[] args)
    {
        _statusKey=key;_statusArgs=args;Status.Text=UiLocalization.Format(key,args);
        Status.SetResourceReference(TextBlock.ForegroundProperty,
            key.StartsWith("OcrError",StringComparison.Ordinal)||key.StartsWith("OcrScreenError",StringComparison.Ordinal)||key=="OcrTranslationFailed"
                ?"SystemFillColorCriticalBrush":"TextFillColorSecondaryBrush");
    }
    private void Refresh()
    {
        var ready=!_closed && _operation is null;
        var settings=SelectedSettings();var targetSupported=settings is not null && TranslationLanguages.Supports(settings.Provider,settings.TargetLanguage);
        CaptureButton.Content=UiLocalization.Get(AutoTranslateBox.IsChecked==true?"OcrCaptureTranslate":"OcrCapture");
        OpenImageButton.IsEnabled=ready;
        CaptureButton.IsEnabled=ready && _capture is not null && LanguageBox.SelectedItem is LocalOcrLanguage &&
            (AutoTranslateBox.IsChecked!=true || targetSupported);
        ServiceBox.IsEnabled=TargetBox.IsEnabled=AutoTranslateBox.IsEnabled=ready;
        TranslateButton.IsEnabled=ready && _translate is not null && targetSupported && CanUseSource;
        CopyTranslationButton.IsEnabled=ready && ResultBox.Text.Length>0;
        RecognizeButton.IsEnabled=ready && _image is not null && LanguageBox.SelectedItem is LocalOcrLanguage;
        StopButton.Content=UiLocalization.Get(_operation is null && _image is null && LanguageBox.Items.Count==0?"OcrReloadLanguages":"OcrStop");
        StopButton.IsEnabled=_operation is not null || ready && _image is null && LanguageBox.Items.Count==0;
        LanguageBox.IsEnabled=TextBox.IsEnabled=ready;
        AutoTranslateBox.Visibility=_translate is not null?Visibility.Visible:Visibility.Collapsed;
        UseTextButton.IsEnabled=ready && CanUseSource;
    }
    private bool CanUseSource => _recognized && !string.IsNullOrWhiteSpace(TextBox.Text) && TextBox.Text.Length<=3000;
    private async Task<bool> RunAsync(Func<long,CancellationToken,Task<bool>> action)
    {
        if(_closed || _operation is not null)return false;
        using var cancel=new CancellationTokenSource();_operation=cancel;var generation=++_generation;
        SetStatus("OcrWorking");Refresh();
        try{return await action(generation,cancel.Token) && Current(generation,cancel.Token);}
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            if(Current(generation,cancel.Token))SetStatus(error switch{
                LocalOcrException ocr=>"OcrError"+ocr.Code,ScreenCaptureException screen=>"OcrScreenError"+screen.Code,
                TranslationException=>"OcrTranslationFailed",_=>"OcrErrorFailed"},
                error is TranslationException translation?new object[]{translation.Code.ToString()}:Array.Empty<object>());
            return false;
        }
        finally{if(_operation==cancel)_operation=null;if(!_closed)Refresh();}
    }
    private bool Current(long generation,CancellationToken token) => !_closed && !token.IsCancellationRequested && generation==_generation;
    private Task<bool> LoadLanguagesAsync() => RunAsync(async(generation,token) =>
    {
        var languages=await _engine.LanguagesAsync(token);if(!Current(generation,token))return false;
        LanguageBox.ItemsSource=languages;LanguageBox.DisplayMemberPath=nameof(LocalOcrLanguage.Name);LanguageBox.SelectedIndex=-1;
        SetStatus(languages.Count==0?"OcrErrorUnsupportedLanguage":"OcrChooseLanguage");return true;
    });
    private async void OpenImage_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Filter=UiLocalization.Get("OcrFileFilter"),CheckFileExists=true};
        if(dialog.ShowDialog(this)==true)await LoadImageAsync(dialog.FileName);
    }
    internal Task<bool> LoadImageAsync(string path) => RunAsync(async(generation,token) =>
    {
        var image=await _load(path,token);
        if(!Current(generation,token)){image.Dispose();return false;}
        if(_recognized && TextBox.Text.Length>0 && !_confirm(UiLocalization.Get("OcrConfirmReplaceImage")))
        {image.Dispose();SetStatus("OcrCancelled");return false;}
        ReplaceImage(image);SetStatus("OcrImageLoaded");return true;
    });
    private async void Recognize_Click(object sender,RoutedEventArgs e){if(await RecognizeAsync())TextBox.Focus();}
    internal Task<bool> RecognizeAsync()
    {
        if(_closed || _operation is not null || _image is null || LanguageBox.SelectedItem is not LocalOcrLanguage language)return Task.FromResult(false);
        var image=_image;
        if(_recognized && TextBox.Text.Length>0 && !_confirm(UiLocalization.Get("OcrConfirmReread")))return Task.FromResult(false);
        return RunAsync(async(generation,token) =>
        {
            var result=await _engine.RecognizeAsync(image.Pixels,image.Width,image.Height,language.Tag,token);
            if(!Current(generation,token))return false;
            ApplyRecognition(result);return true; // Re-recognition stays local; only Capture/Translate clicks can send text.
        });
    }
    private void ApplyRecognition(LocalOcrResult result)
    {
        LocalOcrLimits.ValidateText(result.Text);
        _recognized=true;_updatingSource=true;
        try{TextBox.Text=result.Text;}finally{_updatingSource=false;}
        TextBox.BringIntoView();SetStatus("OcrRecognized",result.Language,result.DurationMs);
    }
    private void Text_Changed(object sender,TextChangedEventArgs e)
    {
        if(!IsInitialized || _closed)return;
        if(_operation is not null && !_updatingSource)Stop();
        ClearTranslation();Refresh();
    }
    private void ReplaceImage(OcrImage image)
    {
        _image?.Dispose();_image=image;ImagePreview.Source=image.Preview;ImagePlaceholder.Visibility=Visibility.Collapsed;
        _recognized=false;_updatingSource=true;
        try{TextBox.Clear();}finally{_updatingSource=false;}
        ClearTranslation();Localize();
    }
    private void BuildServices()
    {
        ServiceBox.Items.Add(new ComboBoxItem{Content=UiLocalization.Get("GoogleProvider"),Tag=ProviderKind.GoogleWeb});
        ServiceBox.Items.Add(new ComboBoxItem{Content=UiLocalization.Get("DeepLProvider"),Tag=ProviderKind.DeepL});
        foreach(var profile in _settings.AiServices)ServiceBox.Items.Add(new ComboBoxItem{Content=profile.DisplayName(),DataContext=profile,Tag=ProviderKind.OpenAI});
        ServiceBox.SelectedItem=ServiceBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (ProviderKind)i.Tag==_settings.Translation.Provider &&
            (_settings.Translation.Provider!=ProviderKind.OpenAI || i.DataContext is AiServiceProfile p && p.Id==_settings.ActiveAiServiceId)) ?? ServiceBox.Items[0];
        foreach(var target in TranslationLanguages.All)TargetBox.Items.Add(new ComboBoxItem{Content=target.NativeName,Tag=target.Code});
        TargetBox.SelectedItem=TargetBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag==_settings.Translation.TargetLanguage) ?? TargetBox.Items[0];
        RefreshTargets();
    }
    private void RefreshTargets()
    {
        if(ServiceBox.SelectedItem is not ComboBoxItem service)return;
        foreach(ComboBoxItem item in TargetBox.Items)item.IsEnabled=TranslationLanguages.Supports((ProviderKind)service.Tag,(string)item.Tag);
    }
    private TranslationSettings? SelectedSettings()
    {
        if(ServiceBox.SelectedItem is not ComboBoxItem item)return null;
        var settings=_settings.Translation.Copy();settings.Provider=(ProviderKind)item.Tag;settings.ReviewSource=false;
        if(TargetBox.SelectedItem is ComboBoxItem target)settings.TargetLanguage=(string)target.Tag;
        if(item.DataContext is AiServiceProfile profile)profile.ApplyTo(settings);
        settings.OpenAiStreaming=false;return settings;
    }
    private void RefreshRequestHint()
    {
        var settings=SelectedSettings();if(settings is null)return;
        string host;
        try{host=settings.Provider switch{ProviderKind.GoogleWeb=>"translate.googleapis.com",ProviderKind.DeepL=>new Uri(settings.DeepLEndpoint).Host,
            _=>AiEndpoint.Resolve(settings.AiProtocol,settings.OpenAiEndpoint,settings.OpenAiModel,false).Host};}
        catch(Exception e) when(e is UriFormatException or TranslationException){host=UiLocalization.Get("ErrorInvalidEndpoint");}
        RequestHint.Text=UiLocalization.Format("OcrTranslateHint",host,settings.TargetLanguage);
    }
    private void ClearTranslation(){ResultBox.Clear();ResultInfo.Text="";}
    private async void Capture_Click(object sender,RoutedEventArgs e) => await CaptureAsync();
    internal Task<bool> CaptureAsync()
    {
        if(_closed || _operation is not null)return Task.FromResult(false);
        if(_capture is null || LanguageBox.SelectedItem is not LocalOcrLanguage language){SetStatus("OcrChooseLanguage");return Task.FromResult(false);}
        if(_recognized && TextBox.Text.Length>0 && !_confirm(UiLocalization.Get("OcrConfirmReplaceImage")))return Task.FromResult(false);
        var translateImmediately=AutoTranslateBox.IsChecked==true;var settings=SelectedSettings();
        return RunAsync(async(generation,token) =>
        {
            var image=await _capture(token);
            if(image is null){if(Current(generation,token))SetStatus("OcrCancelled");return false;}
            if(!Current(generation,token)){image.Dispose();return false;}
            ReplaceImage(image);
            var result=await _engine.RecognizeAsync(image.Pixels,image.Width,image.Height,language.Tag,token);
            if(!Current(generation,token))return false;
            ApplyRecognition(result);
            if(translateImmediately && Current(generation,token))await TranslateCoreAsync(generation,token,settings);
            return true;
        });
    }
    private async void Translate_Click(object sender,RoutedEventArgs e) => await TranslateAsync();
    internal Task<bool> TranslateAsync()
    {
        var settings=SelectedSettings();
        return RunAsync(async(generation,token) =>{await TranslateCoreAsync(generation,token,settings);return true;});
    }
    private async Task TranslateCoreAsync(long generation,CancellationToken token,TranslationSettings? settings)
    {
        token.ThrowIfCancellationRequested();
        if(!Current(generation,token))return;
        if(_translate is null || settings is null || !_recognized || string.IsNullOrWhiteSpace(TextBox.Text))throw new TranslationException("Empty OCR text",TranslationErrorCode.InputEmpty);
        if(TextBox.Text.Length>3000)throw new TranslationException("OCR input too long",TranslationErrorCode.InputTooLong);
        ClearTranslation();SetStatus("OcrTranslating");
        var result=await _translate(TextBox.Text,settings,token);if(!Current(generation,token))return;
        ResultBox.Text=result.Text;ResultInfo.Text=result.ProviderName+(result.Model is {Length:>0} model?" · "+model:"")+" · "+
            result.SourceLanguage+" → "+result.TargetLanguage+" · "+result.DurationMs+" ms · "+UiLocalization.Get("SourceNotChecked");
        ResultBox.FlowDirection=TranslationLanguages.Find(result.TargetLanguage)?.RightToLeft==true?FlowDirection.RightToLeft:FlowDirection.LeftToRight;
        SetStatus("OcrTranslated");
    }
    private void CopyTranslation_Click(object sender,RoutedEventArgs e)
    {
        if(!CopyTranslationButton.IsEnabled)return;
        try{Clipboard.SetText(ResultBox.Text);}catch{SetStatus("StatusClipboardBusy");}
    }
    private void UseText_Click(object sender,RoutedEventArgs e)
    {
        if(!UseTextButton.IsEnabled || _image is null || !_confirm(UiLocalization.Get("OcrConfirmUse")))return;
        AcceptedText=TextBox.Text;DialogResult=true;
    }
}
