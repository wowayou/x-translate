using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTranslate.App.Ocr;
using XTranslate.Core;
using XTranslate.Ocr.Windows;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;

namespace XTranslate.App;

public partial class MainWindow
{
    private async Task RunScreenOcrAcceptanceAsync(List<string> checks,string output,Func<int> requests)
    {
        var original=_settings.Copy();var originalService=_service;
        var language=UiLocalization.Preference;var theme=UiAppearance.Preference;
        var oldReview=ReviewRequested;var count=requests();
        using var handler=new OcrTranslationFixture();using var client=new HttpClient(handler);
        _service=new TranslationService(client);
        var monitor=new ScreenRectangle(-2000,-200,0,1200);var region=new ScreenRectangle(-1600,0,-1000,160);
        CaptureAppPolicy.TryParse("secret.exe",out var policy);
        try
        {
            var blocked=new ScreenApiFixture{Before=new[]{new CaptureWindow(7,99,region,"secret")}};
            ExpectScreen(() => new ScreenCaptureService(blocked).Capture(region,monitor,policy),ScreenCaptureError.ExcludedApplication);
            RequireStream(blocked.Reads==0,"Excluded screen owner was captured");
            var unknown=new ScreenApiFixture{Before=new[]{new CaptureWindow(7,99,region,null)}};
            ExpectScreen(() => new ScreenCaptureService(unknown).Capture(region,monitor,policy),ScreenCaptureError.UnidentifiedApplication);
            RequireStream(unknown.Reads==0,"Unknown screen owner was captured under configured exclusions");
            var changed=new ScreenApiFixture{Before=new[]{new CaptureWindow(7,99,region,"safe")},After=new[]{new CaptureWindow(7,100,region,"safe")}};
            ExpectScreen(() => new ScreenCaptureService(changed).Capture(region,monitor,policy),ScreenCaptureError.WindowChanged);
            RequireStream(changed.Reads==1 && changed.Image!.Pixels.All(b => b==0),"Changed-window capture kept unverified pixels");
            var outOfBounds=new ScreenApiFixture();
            ExpectScreen(() => new ScreenCaptureService(outOfBounds).Capture(new(-2100,0,-1000,160),monitor,policy),ScreenCaptureError.InvalidRegion);
            RequireStream(outOfBounds.Reads==0 && outOfBounds.Lists==0,"Invalid region crossed native capture boundary");
            checks.Add("Screen capture refuses excluded/unknown or invalid regions before pixel reads; window identity changes discard and clear captured pixels without HTTP");

            _settings.Translation.Provider=ProviderKind.GoogleWeb;_settings.Translation.TargetLanguage="zh-CN";
            _settings.AutoFallback=true;_settings.AutoTranslateSelection=true;ReviewSourceBox.IsChecked=true;
            var fake=new OcrFixtureEngine{Text="  screen OCR raw\ntext  "};var captures=0;var confirmReread=true;OcrImage? submitted=null;
            SourceBox.Text="existing main draft";var mainSource=SourceBox.Text;
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(fake,_ => confirmReread,_ =>
            {captures++;submitted=NewScreenFixture();return Task.FromResult<OcrImage?>(submitted);}),async window =>
            {
                await window.Loading;
                RequireStream(window.AutoTranslateBox.IsChecked==true && !window.CaptureButton.IsEnabled && handler.Requests.Count==0 && captures==0,
                    "Opening immediate screen OCR read pixels/sent text or guessed language");
                window.LanguageBox.SelectedIndex=0;
                RequireStream(window.CaptureButton.Content?.ToString()==UiLocalization.Get("OcrCaptureTranslate") && handler.Requests.Count==0,
                    "Screen capture action did not disclose translation or selection sent requests");
                RequireStream(await window.CaptureAsync() && captures==1 && fake.Recognitions==1 && handler.Requests.Count==1,
                    "Capture did not run one recognition plus exactly one selected-service request");
                RequireStream(window.TextBox.Text==fake.Text && window.ResultBox.Text==handler.Result && handler.Requests[0].Text==fake.Text &&
                    handler.Requests[0].Host=="translate.googleapis.com" && SourceBox.Text==mainSource && window.CopyTranslationButton.IsEnabled,
                    "Screen source comparison lost exact OCR text, target/provider boundary or main draft");
                RequireStream(window.RequestHint.Text.Contains("translate.googleapis.com",StringComparison.Ordinal) &&
                    window.ResultInfo.Text.Contains(UiLocalization.Get("SourceNotChecked"),StringComparison.Ordinal),"OCR result hides recipient or implies grammar validation");
                confirmReread=false;
                RequireStream(!await window.RecognizeAsync() && fake.Recognitions==1 && window.ResultBox.Text==handler.Result &&
                    window.CopyTranslationButton.IsEnabled && handler.Requests.Count==1,
                    "Declining OCR reread discarded a valid translation or entered the engine");
                confirmReread=true;
                RequireStream(await window.RecognizeAsync() && fake.Recognitions==2 && window.TextBox.Text==fake.Text && window.ResultBox.Text=="" &&
                    !window.CopyTranslationButton.IsEnabled && handler.Requests.Count==1,
                    "Identical OCR reread kept stale translation/copy or automatically sent text");
                RequireStream(await window.TranslateAsync() && handler.Requests.Count==2 && window.ResultBox.Text==handler.Result,
                    "Explicit translation after identical reread did not send once");
                foreach(var ui in new[]{UiLanguage.SimplifiedChinese,UiLanguage.English})
                {
                    UiLocalization.SetPreference(ui);UiAppearance.Apply(ui==UiLanguage.English?UiTheme.Dark:UiTheme.Light);
                    window.Width=1000;window.Height=760;window.EditorViewport.ScrollToTop();window.UpdateLayout();
                    RequireStream(window.ImagePreview.Source is not null && window.ResultBox.Text==handler.Result && window.TextBox.Text==fake.Text && handler.Requests.Count==2,
                        "Source-comparison relocalization altered image/text or resent request");
                    RenderWindowTo(window,Path.ChangeExtension(output,ui==UiLanguage.English?".screen-ocr.en.dark.png":".screen-ocr.zh.light.png"));
                    window.Width=window.MinWidth;window.Height=window.MinHeight;window.UpdateLayout();
                    RequireStream(window.ResultBox.ActualHeight>=60 && window.ResultBox.ActualWidth>=100 && window.UseTextButton.ActualHeight>=30,
                        "Minimum screen comparison layout lost translation/action");
                    RenderWindowTo(window,Path.ChangeExtension(output,ui==UiLanguage.English?".screen-ocr.minimum.en.png":".screen-ocr.minimum.zh.png"));
                }
                window.TextBox.Text="corrected by user";
                RequireStream(window.ResultBox.Text=="" && !window.CopyTranslationButton.IsEnabled,"Edited OCR source kept a stale copyable translation");
                RequireStream(await window.TranslateAsync() && handler.Requests.Count==3 && handler.Requests[2].Text=="corrected by user",
                    "Explicit retranslation did not use user correction exactly once");
                window.TargetBox.SelectedItem=window.TargetBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag=="en");
                RequireStream(window.ResultBox.Text=="" && handler.Requests.Count==3 && _settings.Translation.TargetLanguage=="zh-CN",
                    "Target change kept stale result, sent request or mutated main settings");
                RequireStream(await window.TranslateAsync() && handler.Requests.Count==4 && handler.Requests[3].Target=="en","Local OCR target selection was not honored");
                window.Close();
            });
            RequireStream(submitted is not null && submitted.Pixels.All(x => x==0) && SourceBox.Text==mainSource && !_settingsOpen && requests()==count,
                "Closing comparison retained image pixels, overwrote source or leaked modal state");
            checks.Add("Declined reread preserves the valid translation with zero recognition/request; accepted identical re-recognition revokes old translation/copy without implicit HTTP, and explicit retranslation sends once");
            checks.Add("Capture-and-translate sends one request with raw OCR text only; image/source/result stay side by side, edits and target changes revoke old translation, and close preserves main draft/clears pixels");

            fake=new OcrFixtureEngine();var noImage=0;var before=handler.Requests.Count;
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(fake,_ => true,_ =>{noImage++;return Task.FromResult<OcrImage?>(null);}),async window =>
            {
                await window.Loading;window.LanguageBox.SelectedIndex=0;
                RequireStream(!await window.CaptureAsync() && fake.Recognitions==0 && noImage==1 && handler.Requests.Count==before,"Cancelled region triggered OCR or translation");
                window.Close();
            });
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(fake,_ => true,_ =>throw new ScreenCaptureException(ScreenCaptureError.ExcludedApplication)),async window =>
            {
                await window.Loading;window.LanguageBox.SelectedIndex=0;
                RequireStream(!await window.CaptureAsync() && fake.Recognitions==0 && handler.Requests.Count==before &&
                    window.Status.Text==UiLocalization.Get("OcrScreenErrorExcludedApplication"),"Excluded capture was translated or not reported");
                window.Close();
            });
            var delayedImage=new TaskCompletionSource<OcrImage?>(TaskCreationOptions.RunContinuationsAsynchronously);OcrImage? lateImage=null;
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(fake,_ => true,_ =>delayedImage.Task),async window =>
            {
                await window.Loading;window.LanguageBox.SelectedIndex=0;var pending=window.CaptureAsync();window.StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                lateImage=NewScreenFixture();delayedImage.SetResult(lateImage);
                RequireStream(!await pending && fake.Recognitions==0 && handler.Requests.Count==before && lateImage.Pixels.All(b => b==0),
                    "Late cancelled screen image reached OCR or retained pixels");window.Close();
            });
            checks.Add("Cancelled/excluded selection is zero recognition/request; stopping a delayed capture rejects and clears late pixels before OCR");

            fake=new OcrFixtureEngine();
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(fake,_ => true,_ =>Task.FromResult<OcrImage?>(NewScreenFixture())),async window =>
            {
                await window.Loading;window.LanguageBox.SelectedIndex=0;window.AutoTranslateBox.IsChecked=false;
                RequireStream(await window.CaptureAsync() && handler.Requests.Count==before && window.ResultBox.Text=="" && fake.Recognitions==1,
                    "Explicit local-only screenshot still sent text");
                handler.Status=HttpStatusCode.ServiceUnavailable;
                RequireStream(!await window.TranslateAsync() && handler.Requests.Count==before+1 && window.TextBox.Text==fake.Text &&
                    window.ResultBox.Text=="" && window.ImagePreview.Source is not null,"Failed OCR translation retried/fell back or discarded source");
                handler.Status=HttpStatusCode.OK;
                var late=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);handler.Delay=late;
                var pending=window.TranslateAsync();await WaitForStreamingUiAsync(() => handler.Requests.Count==before+2);
                window.ServiceBox.SelectedIndex=1;late.SetResult();await pending;
                RequireStream(window.ResultBox.Text=="" && handler.Requests.Count==before+2 && !window.CopyTranslationButton.IsEnabled,
                    "Service switch admitted late translation or added fallback");
                handler.Delay=null;window.Close();
            });
            checks.Add("Local-only capture does not send; translation failure retains screenshot/raw text with one attempt and service switching rejects a delayed response without fallback");

            var delayedText=new TaskCompletionSource<LocalOcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake=new OcrFixtureEngine{Pending=delayedText.Task};before=handler.Requests.Count;
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(fake,_ =>true,_ =>Task.FromResult<OcrImage?>(NewScreenFixture())),async window =>
            {
                await window.Loading;window.LanguageBox.SelectedIndex=0;
                var pending=window.CaptureAsync();await WaitForStreamingUiAsync(() =>fake.Recognitions==1);
                window.TextBox.Text="edited during delayed OCR";
                delayedText.SetResult(new("late captured text","en-US",0));
                RequireStream(!await pending && handler.Requests.Count==before && window.TextBox.Text=="edited during delayed OCR" && window.ResultBox.Text=="",
                    "Editing source during OCR admitted late text or automatic sending");window.Close();
            });
            checks.Add("Explicit source changes while OCR is outstanding invalidate its generation; late recognized text cannot replace edits or trigger automatic translation");

            await VerifyScreenPickerAsync(checks);
            if (Environment.GetCommandLineArgs().Contains("--native-checks")) await VerifyOcrMonitorsAsync(checks);
            await VerifyScreenOwnerVisibilityAsync(checks);
            await VerifyNativeScreenOcrAsync(checks,output,handler);
            RequireStream(requests()==count && !_settingsOpen,"OCR path polluted outer HTTP handler/modal state");
        }
        finally
        {
            handler.Delay?.TrySetResult();handler.Delay=null;_service.Dispose();_service=originalService;RestoreSmokeSettings(original);
            ReviewSourceBox.IsChecked=oldReview;UiLocalization.SetPreference(language);UiAppearance.Apply(theme);Localize();_storage.Save(_settings);ShowForUser(false);SourceBox.Clear();
        }
    }
    private async Task VerifyScreenPickerAsync(List<string> checks)
    {
        var screen=ScreenRegionWindow.CurrentMonitor();var point=new PixelPoint(screen.Left+180,screen.Top+150);
        await WithLearningModalAsync<ScreenRegionWindow>(() => new ScreenRegionWindow(screen,() =>point).ShowDialog(),async picker =>
        {
            var handle=new WindowInteropHelper(picker).Handle;NativeMethods.GetWindowRect(handle,out var rect);
            RequireStream(new ScreenRectangle(rect.Left,rect.Top,rect.Right,rect.Bottom)==screen,"Screen picker mixed physical monitor pixels and WPF DIPs");
            picker.SelectionCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.MouseDownEvent});
            point=new(screen.Left+520,screen.Top+300);
            picker.SelectionCanvas.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseMoveEvent});
            picker.SelectionCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.MouseUpEvent});
            RequireStream(picker.SelectedRegion==new ScreenRectangle(screen.Left+180,screen.Top+150,screen.Left+520,screen.Top+300) &&
                !picker.SelectionCanvas.IsMouseCaptured,"Picker did not preserve physical selected bounds or release mouse capture");await Task.CompletedTask;
        });
        await WithLearningModalAsync<ScreenRegionWindow>(() => new ScreenRegionWindow(screen).ShowDialog(),async picker =>
        {
            picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(picker)!,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
            RequireStream(picker.SelectedRegion is null && !picker.IsVisible,"Escape retained capture selection");await Task.CompletedTask;
        });
        await WithLearningModalAsync<ScreenRegionWindow>(() => new ScreenRegionWindow(screen).ShowDialog(),async picker =>
        {
            picker.Closing+=(_,_) => picker.CancelSelection(); // Model the native Deactivated event inside closing.
            picker.CancelSelection();picker.CancelSelection();
            RequireStream(picker.SelectedRegion is null && !picker.IsVisible,"Repeated/deactivation cancellation left a selection");await Task.CompletedTask;
        });
        checks.Add("Real picker HWND matches current physical monitor; controlled mouse routed events select exact pixels/release capture; Escape and reentrant/deactivation cancellation close without pixels");
    }
    private async Task VerifyScreenOwnerVisibilityAsync(List<string> checks)
    {
        var fake=new OcrFixtureEngine();var monitor=new ScreenRectangle(0,0,1000,700);var region=new ScreenRectangle(50,50,200,150);
        var api=new ScreenApiFixture();var reads=0;
        await WithLearningModalAsync<OcrWindow>(() => new OcrWindow(fake,_ =>true){Owner=this}.ShowDialog(),async view =>
        {
            var handle=new WindowInteropHelper(view).Handle;var loaded=view.Loading;
            await view.Loading;
            var result=await CaptureScreenForOcrAsync(view,CancellationToken.None,m =>
            {
                RequireStream(!NativeMethods.IsWindowVisible(handle) && !NativeMethods.IsWindowVisible(_handle),"Capture would include an OCR/main HWND");reads++;return region;
            },new ScreenCaptureService(api),monitor);
            using var captured=result ?? throw new Exception("Screen visibility fixture did not return an image");
            RequireStream(NativeMethods.IsWindowVisible(handle) && NativeMethods.IsWindowVisible(_handle) &&
                view.IsVisible && ReferenceEquals(view.Loading,loaded),"Temporarily hiding HWND ended modal/reinitialized OCR or failed restore");
            var cancelled=await CaptureScreenForOcrAsync(view,CancellationToken.None,_ =>null,new ScreenCaptureService(api),monitor);
            RequireStream(cancelled is null && api.Reads==1 && reads==1,"Cancelled picker still read screen");view.Close();
        });
        checks.Add("Production capture orchestration hides native OCR/main windows without ending ShowDialog, restores them on return/cancel, and does not reinitialize language state");
    }
    private async Task VerifyNativeScreenOcrAsync(List<string> checks,string output,OcrTranslationFixture handler)
    {
        // WinForms/GDI paints an explicit opaque, unscaled bitmap. WPF HWND ownership alone did not
        // establish that a Fluent-backed test window had finished painting or excluded background pixels.
        var screen=ScreenRegionWindow.CurrentMonitor();
        using var expectedBitmap=new Drawing.Bitmap(760,180,Drawing.Imaging.PixelFormat.Format32bppArgb);
        using(var graphics=Drawing.Graphics.FromImage(expectedBitmap))
        using(var font=new Drawing.Font("Segoe UI",42,Drawing.FontStyle.Regular,Drawing.GraphicsUnit.Pixel))
        {
            graphics.Clear(Drawing.Color.White);
            graphics.DrawString("HELLO WORLD 123",font,Drawing.Brushes.Black,30,45);
        }
        var locked=expectedBitmap.LockBits(new Drawing.Rectangle(0,0,expectedBitmap.Width,expectedBitmap.Height),
            Drawing.Imaging.ImageLockMode.ReadOnly,Drawing.Imaging.PixelFormat.Format32bppArgb);
        var expectedPixels=new byte[expectedBitmap.Width*expectedBitmap.Height*4];
        try
        {
            RequireStream(locked.Stride==expectedBitmap.Width*4,"Owned screen bitmap layout mismatch");
            Marshal.Copy(locked.Scan0,expectedPixels,0,expectedPixels.Length);
            for(var i=0;i<expectedPixels.Length;i+=4)
            {
                // A binary synthetic glyph avoids display color conversion/dither on fractional antialias
                // colors. Pixel equality remains exact; no tolerance or background-content allowance.
                var binary=(byte)(expectedPixels[i]<128?0:255);
                expectedPixels[i]=expectedPixels[i+1]=expectedPixels[i+2]=binary;expectedPixels[i+3]=255;
            }
        }
        finally{expectedBitmap.UnlockBits(locked);}
        using var fixture=new OpaqueScreenFixture(expectedPixels,expectedBitmap.Size)
        {Location=new Drawing.Point(screen.Left+80,screen.Top+80)};
        fixture.Show();fixture.Refresh();
        byte[]? croppedExpected=null;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            RequireStream((NativeMethods.GetWindowLong(fixture.Handle,-20)&0x00080000)==0 && fixture.Opacity==1 && fixture.TransparencyKey.IsEmpty,
                "Owned screen fixture must not be layered or transparent");
            var point=fixture.PointToScreen(Drawing.Point.Empty);
            const int inset=16; // Windows 11 clips native round corners; do not capture any corner/background pixels.
            var width=expectedBitmap.Width-2*inset;var height=expectedBitmap.Height-2*inset;
            var region=new ScreenRectangle(point.X+inset,point.Y+inset,point.X+inset+width,point.Y+inset+height);
            croppedExpected=new byte[width*height*4];
            for(var y=0;y<height;y++)Array.Copy(expectedPixels,((y+inset)*expectedBitmap.Width+inset)*4,croppedExpected,y*width*4,width*4);
            ScreenCaptureRules.Validate(region,screen);
            var owned=new OwnedScreenApi(fixture.Handle,croppedExpected);
            using(var process=System.Diagnostics.Process.GetCurrentProcess())
            {
                // Repeated native crops remain within the verified opaque interior; mismatch rejects before OCR.
                var gdiBefore=GetGuiResources(process.Handle,0);
                var watch=System.Diagnostics.Stopwatch.StartNew();
                for(var i=0;i<20;i++)using(var crop=owned.Pixels(region))RequireStream(owned.ExactPixels,"Repeated owned screen crop differs from generated pixels");
                var gdiAfter=GetGuiResources(process.Handle,0);
                RequireStream(gdiAfter<=gdiBefore,$"Native screenshot loop grew GDI handles: before={gdiBefore}, after={gdiAfter}");
                checks.Add($"Twenty exact owned-screen crops show no GDI handle growth (delta {gdiAfter-gdiBefore}); all native release calls checked; elapsed {watch.Elapsed.TotalMilliseconds:F1}ms excludes OCR/network");
            }
            var before=handler.Requests.Count;
            OcrWindow? current=null;
            await WithLearningModalAsync<OcrWindow>(() => OpenOcr(new WindowsOcrEngine(),_ =>true,async token =>
            {
                var view=current ?? throw new Exception("Owned OCR view not ready");
                return await CaptureScreenForOcrAsync(view,token,m =>
                {
                    var picker=new ScreenRegionWindow(m);
                    _=Dispatcher.BeginInvoke(new Action(() => picker.CompleteSelection(region)),DispatcherPriority.ContextIdle);
                    return picker.ShowDialog()==true?picker.SelectedRegion:null;
                },new ScreenCaptureService(owned),screen);
            }),async view =>
            {
                current=view;await view.Loading;
                var english=view.LanguageBox.Items.Cast<LocalOcrLanguage>().FirstOrDefault(l =>l.Tag=="en-US");
                RequireStream(english is not null,"Real screen lane requires installed en-US; no pack download attempted");
                view.LanguageBox.SelectedItem=english;
                var succeeded=await view.CaptureAsync();
                // Never include OCR text/image/exception bodies in a failing report. The backend below
                // checks pixel equality with the generated bitmap before recognition or any evidence image.
                RequireStream(succeeded && view.TextBox.Text=="HELLO WORLD 123" && handler.Requests.Count==before+1 && view.ResultBox.Text==handler.Result,
                    $"Owned screen OCR chain failed: completed={succeeded}, exactText={view.TextBox.Text=="HELLO WORLD 123"}, requests={handler.Requests.Count-before}, exactTranslation={view.ResultBox.Text==handler.Result}, statusKey={view.StatusKey}, pixelsRead={owned.PixelReads}, exactPixels={owned.ExactPixels}");
                view.Width=1000;view.Height=760;view.EditorViewport.ScrollToTop();view.ComparisonViewport.ScrollToTop();view.UpdateLayout();
                RenderWindowTo(view,Path.ChangeExtension(output,".screen-ocr.real.png"));view.Close();
            });
            RequireStream(owned.PixelReads==21 && owned.ExactPixels && expectedPixels.Any(b =>b!=0),"Owned native screen fixture did not prove exact pixel input");
            checks.Add("Real opaque GDI fixture passes exact generated-pixel comparison before local OCR; production hiding/picker/capture yields HELLO WORLD 123 and exactly one simulated translation without file input or background-content reporting");
        }
        finally{fixture.Hide();Array.Clear(expectedPixels);if(croppedExpected is not null)Array.Clear(croppedExpected);}
    }
    private sealed class OpaqueScreenFixture : Forms.Form
    {
        private readonly byte[] _pixels;
        private readonly Drawing.Size _size;
        internal OpaqueScreenFixture(byte[] pixels,Drawing.Size size)
        {
            _pixels=pixels;_size=size;AutoScaleMode=Forms.AutoScaleMode.None;FormBorderStyle=Forms.FormBorderStyle.FixedToolWindow;
            ClientSize=size;StartPosition=Forms.FormStartPosition.Manual;ShowInTaskbar=false;TopMost=true;
            BackColor=Drawing.Color.White;Opacity=1;TransparencyKey=Drawing.Color.Empty;Text="XTranslate owned OCR fixture";
            SetStyle(Forms.ControlStyles.Opaque|Forms.ControlStyles.UserPaint|Forms.ControlStyles.AllPaintingInWmPaint,true);
        }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            // Copy exact opaque BGRA bytes with GDI, avoiding GDI+ alpha-compositing roundoff at glyph edges.
            var dc=e.Graphics.GetHdc();var memory=IntPtr.Zero;var bitmap=IntPtr.Zero;var previous=IntPtr.Zero;
            try
            {
                memory=NativeMethods.CreateCompatibleDC(dc);
                var info=new NativeMethods.BitmapInfo{Size=(uint)Marshal.SizeOf<NativeMethods.BitmapInfo>(),Width=_size.Width,Height=-_size.Height,Planes=1,BitCount=32};
                bitmap=NativeMethods.CreateDIBSection(dc,ref info,0,out var bits,IntPtr.Zero,0);
                RequireStream(memory!=IntPtr.Zero && bitmap!=IntPtr.Zero && bits!=IntPtr.Zero,"Owned fixture bitmap creation failed");
                Marshal.Copy(_pixels,0,bits,_pixels.Length);previous=NativeMethods.SelectObject(memory,bitmap);
                RequireStream(previous!=IntPtr.Zero && previous!=new IntPtr(-1) &&
                    NativeMethods.BitBlt(dc,0,0,_size.Width,_size.Height,memory,0,0,0x00cc0020) && NativeMethods.GdiFlush(),"Owned fixture drawing failed");
            }
            finally
            {
                if(previous!=IntPtr.Zero && previous!=new IntPtr(-1))NativeMethods.SelectObject(memory,previous);
                if(bitmap!=IntPtr.Zero)NativeMethods.DeleteObject(bitmap);
                if(memory!=IntPtr.Zero)NativeMethods.DeleteDC(memory);
                e.Graphics.ReleaseHdc(dc);
            }
        }
        protected override void OnPaintBackground(Forms.PaintEventArgs e) => e.Graphics.Clear(Drawing.Color.White);
    }
    private sealed class OwnedScreenApi(IntPtr owner,byte[] expected) : IScreenCaptureApi
    {
        private readonly WindowsScreenCaptureApi _native=new();
        internal int PixelReads;internal bool ExactPixels;
        public IReadOnlyList<CaptureWindow> Windows(ScreenRectangle region) => _native.Windows(region);
        public OcrImage Pixels(ScreenRectangle region)
        {
            RequireStream(NativeMethods.IsWindowVisible(owner) && (NativeMethods.GetWindowLong(owner,-20)&0x00080000)==0,
                "Owned screen window is not visibly opaque");
            foreach(var point in new[]{new PixelPoint(region.Left+1,region.Top+1),new(region.Right-2,region.Top+1),
                new(region.Left+1,region.Bottom-2),new(region.Right-2,region.Bottom-2),new(region.Left+(int)region.Width/2,region.Top+(int)region.Height/2)})
                RequireStream(NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new(){X=point.X,Y=point.Y}),2)==owner,
                    "Owned screen fixture was covered; screen read refused");
            PixelReads++;var image=_native.Pixels(region);
            ExactPixels=image.Pixels.AsSpan().SequenceEqual(expected);
            if(!ExactPixels){image.Dispose();throw new Exception("Owned screenshot pixel comparison failed; contents are not retained, reported or recognized");}
            return image;
        }
    }
    private static void ExpectScreen(Action run,ScreenCaptureError code)
    {
        try{run();}catch(ScreenCaptureException error){RequireStream(error.Code==code,"Unexpected screen error");return;}
        throw new Exception("Expected screen refusal "+code);
    }
    private static OcrImage NewScreenFixture(int width=600,int height=160)
    {
        var pixels=Enumerable.Repeat((byte)255,width*height*4).ToArray();
        var preview=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);preview.Freeze();return new(width,height,preview,pixels);
    }
    private sealed class ScreenApiFixture : IScreenCaptureApi
    {
        internal IReadOnlyList<CaptureWindow> Before=Array.Empty<CaptureWindow>();
        internal IReadOnlyList<CaptureWindow>? After;
        internal int Reads,Lists;internal OcrImage? Image;
        public IReadOnlyList<CaptureWindow> Windows(ScreenRectangle region)=>Lists++==0?Before:After??Before;
        public OcrImage Pixels(ScreenRectangle region){Reads++;return Image=NewScreenFixture((int)region.Width,(int)region.Height);}
    }
    private sealed class OcrTranslationFixture : HttpMessageHandler
    {
        internal readonly List<(string Host,string Text,string Target)> Requests=new();
        internal readonly string Result="截图译文 · screen translation";
        internal HttpStatusCode Status=HttpStatusCode.OK;internal TaskCompletionSource? Delay;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var uri=request.RequestUri!;string text,target;
            if(uri.Host=="translate.googleapis.com")
            {
                var query=uri.Query.TrimStart('?').Split('&').Select(p =>p.Split('=',2)).ToDictionary(p=>p[0],p=>Uri.UnescapeDataString(p[1]));
                text=query["q"];target=query["tl"];
            }
            else
            {
                using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                text=json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;target="zh-CN";
            }
            Requests.Add((uri.Host,text,target));if(Delay is { } waiting)await waiting.Task; // Deliberately ignores token to test stale result rejection.
            return new HttpResponseMessage(Status){Content=new StringContent(uri.Host=="translate.googleapis.com"?
                JsonSerializer.Serialize(new object?[]{new object[]{new object[]{Result,text}},null,"en"}):
                JsonSerializer.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=Result}}}}),Encoding.UTF8,"application/json")};
        }
    }
}
