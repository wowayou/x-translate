using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTranslate.Core;
using XTranslate.Ocr.Windows;

namespace XTranslate.App;

public partial class MainWindow
{
    private async Task RunOcrAcceptanceAsync(List<string> checks,string output,Func<int> requests)
    {
        var root=Path.Combine(Path.GetDirectoryName(output)!,"ocr-image-fixture");Directory.CreateDirectory(root);
        var png=Path.Combine(root,"fixed.png");
        var pixels=Enumerable.Repeat((byte)255,960*160*4).ToArray();
        var bitmap=BitmapSource.Create(960,160,96,96,PixelFormats.Bgra32,null,pixels,960*4);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=new FileStream(png,FileMode.CreateNew)) encoder.Save(file);
        var originalLanguage=UiLocalization.Preference;var originalTheme=UiAppearance.Preference;var count=requests();
        var original=_settings.Copy();var originalReview=ReviewRequested;var originalService=_service;
        using var noSendHandler=new SmokeHandler();using var noSendClient=new System.Net.Http.HttpClient(noSendHandler);
        _service=new TranslationService(noSendClient);
        try
        {
            var fake=new OcrFixtureEngine();var accept=false;
            SourceBox.Text="unchanged main source";var source=SourceBox.Text;var target=_settings.Translation.TargetLanguage;
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(fake,_ => accept),async window =>
            {
                await window.Loading;
                RequireStream(window.LanguageBox.SelectedIndex==-1 && fake.Recognitions==0 && !window.RecognizeButton.IsEnabled,
                    "Opening OCR guessed language or started recognition");
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                RequireStream(fake.LanguageReads==1,"Reloaded window repeated local language enumeration");
                RequireStream(await window.LoadImageAsync(png) && window.ImagePreview.Source is not null && fake.Recognitions==0,
                    "Local image selection started OCR or failed preview");
                window.LanguageBox.SelectedIndex=0;
                RequireStream(fake.Recognitions==0 && window.RecognizeButton.IsEnabled,"Selecting language performed recognition");
                fake.Text="  fixed OCR text\n原文  ";
                RequireStream(await window.RecognizeAsync() && window.TextBox.Text==fake.Text && window.UseTextButton.IsEnabled && fake.Recognitions==1,
                    "Manual recognition did not preserve raw text");
                window.TextBox.Text="  manually checked text  ";
                window.UseTextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequireStream(window.IsVisible && SourceBox.Text==source,"Import ignored confirmation");
                RequireStream(!await window.RecognizeAsync() && fake.Recognitions==1,"Reread ignored draft replacement cancellation");
                RequireStream(!await window.LoadImageAsync(png) && window.TextBox.Text=="  manually checked text  " && fake.Recognitions==1,
                    "Changing image ignored draft discard confirmation");
                foreach(var language in new[]{UiLanguage.SimplifiedChinese,UiLanguage.English})
                {
                    UiLocalization.SetPreference(language);UiAppearance.Apply(language==UiLanguage.English?UiTheme.Dark:UiTheme.Light);
                    window.Width=window.MinWidth;window.Height=window.MinHeight;window.UpdateLayout();
                    RequireStream(window.TextBox.Text=="  manually checked text  " && fake.Recognitions==1 && window.UseTextButton.ActualHeight>=30,
                        "OCR language/theme/minimum layout changed draft or sent work");
                    window.TextBox.BringIntoView();window.UpdateLayout();
                    RequireStream(window.TextBox.IsVisible && window.TextBox.ActualHeight>=100,"OCR result editor was not reachable at minimum size");
                    RenderWindowTo(window,Path.ChangeExtension(output,language==UiLanguage.English?".ocr.en.dark.png":".ocr.zh.light.png"));
                }
                window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            RequireStream(SourceBox.Text==source && !_settingsOpen && _settings.Translation.TargetLanguage==target && requests()==count,
                "Cancelled image OCR changed main input/target or sent network");
            checks.Add("Image OCR opens with no guessed language or auto-recognition; local PNG/language selection is zero-request; raw/editable text survives language/theme and Cancel leaves main source intact");

            accept=true;fake=new OcrFixtureEngine();SelectProvider(ProviderKind.GoogleWeb);_settings.AutoTranslateSelection=true;ReviewSourceBox.IsChecked=true;
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(fake,_ => accept),async window =>
            {
                await window.Loading;await window.LoadImageAsync(png);window.LanguageBox.SelectedIndex=0;
                await window.RecognizeAsync();window.TextBox.Text="confirmed exact OCR";
                window.UseTextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            RequireStream(SourceBox.Text=="confirmed exact OCR" && _statusKey=="StatusOcrImported" && requests()==count,
                "OCR import altered confirmed text or auto-translated");
            RequireStream(noSendHandler.RequestCount==0,"OCR import attempted an HTTP request against a fresh service");
            ReviewSourceBox.IsChecked=false;
            checks.Add("Confirmed image OCR imports exact user-edited source without HTTP, even when hotkey auto-translate and source-review are enabled");

            fake=new OcrFixtureEngine();
            await WithLearningModalAsync<OcrWindow>(() => new OcrWindow(fake,_ => true){Owner=this}.ShowDialog(),async window =>
            {
                await window.Loading;await window.LoadImageAsync(png);window.LanguageBox.SelectedIndex=0;
                await window.RecognizeAsync();window.TextBox.Text="retained draft";
                fake.Failure=new LocalOcrException(LocalOcrError.Failed);
                RequireStream(!await window.RecognizeAsync() && window.TextBox.Text=="retained draft" && window.UseTextButton.IsEnabled,
                    "Failed reread lost draft or disabled retained result");
                var before=window.ImagePreview.Source;
                var invalid=Path.Combine(root,"bad.png");await File.WriteAllTextAsync(invalid,"private_marker");
                RequireStream(!await window.LoadImageAsync(invalid) && window.ImagePreview.Source==before && window.TextBox.Text=="retained draft" &&
                    !window.Status.Text.Contains("private_marker"),"Invalid image replaced valid preview/draft or exposed data");
                window.TextBox.Text=new string('x',3001);RequireStream(!window.UseTextButton.IsEnabled,"Oversized OCR text could be imported");
                window.TextBox.Text="retained draft";
                var late=new TaskCompletionSource<LocalOcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                fake.Failure=null;fake.Pending=late.Task;
                var recognition=window.RecognizeAsync();window.StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                late.SetResult(new("late private text","en-US",0));
                RequireStream(!await recognition && window.TextBox.Text=="retained draft" && !window.StopButton.IsEnabled,
                    "Cancelled recognition admitted late output or kept operation live");
                window.Close();
            });
            checks.Add("OCR failures retain prior image/draft; invalid PNG and over-3000 import fail; cancellation rejects delayed text without hidden retry or exception-body leakage");

            var large=Path.Combine(root,"large.png");
            using(var file=new FileStream(large,FileMode.CreateNew)) file.SetLength(Ocr.OcrImageLoader.MaxFileBytes+1L);
            await WithLearningModalAsync<OcrWindow>(() => new OcrWindow(new OcrFixtureEngine(),_ => true){Owner=this}.ShowDialog(),async window =>
            {
                await window.Loading;
                RequireStream(!await window.LoadImageAsync(large) && window.Status.Text==UiLocalization.Get("OcrErrorImageTooLarge") && window.ImagePreview.Source is null,
                    "Oversized file was decoded or admitted");
                var crafted=File.ReadAllBytes(png);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crafted.AsSpan(16,4),uint.MaxValue);
                var path=Path.Combine(root,"huge-header.png");await File.WriteAllBytesAsync(path,crafted);
                RequireStream(!await window.LoadImageAsync(path) && window.Status.Text==UiLocalization.Get("OcrErrorImageTooLarge"),
                    "PNG dimension budget was checked only after decoder allocation");
                window.Close();
            });
            checks.Add("PNG byte and declared-dimension ceilings reject oversized fixtures before decoding; no file writes or data collection by the OCR window");
            using(var white=new Ocr.OcrImage(1,1,BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{255,255,255,255},4),new byte[]{255,255,255,255}))
            {
                var load=new TaskCompletionSource<Ocr.OcrImage>(TaskCreationOptions.RunContinuationsAsynchronously);
                await WithLearningModalAsync<OcrWindow>(() => new OcrWindow(new OcrFixtureEngine(),_ => true,(_,_) => load.Task){Owner=this}.ShowDialog(),async window =>
                {
                    await window.Loading;
                    var loading=window.LoadImageAsync("fixed delayed loader");window.Close();
                    load.SetResult(white);
                    RequireStream(!await loading && window.ImagePreview.Source is null && white.Pixels.All(x => x==0),
                        "Closing during load retained late image or failed to clear rejected pixels");
                });
            }
            checks.Add("Closing OCR during a delayed image load rejects the late preview and clears its owned pixels; source remains unchanged");

            var missing=new OcrFixtureEngine { LanguageFailure=true };
            await WithLearningModalAsync<OcrWindow>(() => new OcrWindow(missing,_ => true){Owner=this}.ShowDialog(),async window =>
            {
                await window.Loading;
                RequireStream(window.StopButton.IsEnabled && window.Status.Text==UiLocalization.Get("OcrErrorUnavailable") && !window.RecognizeButton.IsEnabled,
                    "Unavailable local language service had no explicit retry or allowed recognition");
                missing.LanguageFailure=false;
                window.StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForStreamingUiAsync(() => window.LanguageBox.Items.Count==1);
                RequireStream(missing.LanguageReads==2 && missing.Recognitions==0 && window.LanguageBox.SelectedIndex==-1,
                    "Explicit language reload guessed language or started OCR");
                window.Close();
            });
            checks.Add("Unavailable language enumeration offers explicit local reload without download, guessed language or recognition");

            try { await Ocr.OcrImageLoader.LoadAsync(@"\\private_marker.invalid\share\image.png",CancellationToken.None);throw new Exception("Network image was opened"); }
            catch(LocalOcrException e) { RequireStream(e.Code==LocalOcrError.InvalidImage,"Network path failed after unexpected processing"); }
            checks.Add("Local PNG loader refuses UNC/network destinations before opening bytes; link paths are rejected by managed file safety primitives");
            await VerifyOcrPngAcceptanceAsync(checks, root);

            var realPng=Path.Combine(root,"real-local.png");
            using(var generated=new System.Drawing.Bitmap(960,160))
            {
                using(var graphics=System.Drawing.Graphics.FromImage(generated))
                using(var font=new System.Drawing.Font("Segoe UI",42,System.Drawing.FontStyle.Regular,System.Drawing.GraphicsUnit.Pixel))
                {graphics.Clear(System.Drawing.Color.White);graphics.DrawString("HELLO WORLD 123",font,System.Drawing.Brushes.Black,30,45);}
                generated.Save(realPng,System.Drawing.Imaging.ImageFormat.Png);
            }
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(new WindowsOcrEngine(),_ => true),async window =>
            {
                await window.Loading;
                var installed=window.LanguageBox.Items.Cast<LocalOcrLanguage>().FirstOrDefault(l => l.Tag=="en-US");
                if(installed is null)
                {
                    window.Close();checks.Add("SKIP real image OCR UI: en-US is not installed; no pack download or language fallback");return;
                }
                await window.LoadImageAsync(realPng);window.LanguageBox.SelectedItem=installed;
                RequireStream(await window.RecognizeAsync() && window.TextBox.Text=="HELLO WORLD 123", "Real local image-to-OCR UI recognition failed");
                window.TextBox.BringIntoView();window.UpdateLayout();
                RenderWindowTo(window,Path.ChangeExtension(output,".ocr.real-en.png"));
                window.UseTextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                checks.Add("Real image OCR UI loads a generated PNG, explicitly selects installed en-US, recognizes HELLO WORLD 123 locally and confirms manual import");
            });
            RequireStream(requests()==count && noSendHandler.RequestCount==0 && !_settingsOpen,"Local image OCR issued HTTP or left modal capture enabled");
        }
        finally
        {
            ReviewSourceBox.IsChecked=originalReview;_service.Dispose();_service=originalService;RestoreSmokeSettings(original);UiLocalization.SetPreference(originalLanguage);UiAppearance.Apply(originalTheme);
            Localize();_storage.Save(_settings);SourceBox.Clear();
        }
    }
    private sealed class OcrFixtureEngine : ILocalOcrEngine
    {
        internal int LanguageReads,Recognitions;
        internal bool LanguageFailure;
        internal string Text="recognized fixture";
        internal Exception? Failure;
        internal Task<LocalOcrResult>? Pending;
        public Task<IReadOnlyList<LocalOcrLanguage>> LanguagesAsync(CancellationToken token)
        {LanguageReads++;return LanguageFailure ? Task.FromException<IReadOnlyList<LocalOcrLanguage>>(new LocalOcrException(LocalOcrError.Unavailable)) :
            Task.FromResult<IReadOnlyList<LocalOcrLanguage>>(new[]{new LocalOcrLanguage("en-US","English")});}
        public Task<LocalOcrResult> RecognizeAsync(ReadOnlyMemory<byte> bgra,int width,int height,string language,CancellationToken token)
        {
            Recognitions++;
            if(Failure is not null) return Task.FromException<LocalOcrResult>(Failure);
            return Pending ?? Task.FromResult(new LocalOcrResult(Text,language,0));
        }
    }
}
