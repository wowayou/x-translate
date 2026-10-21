using System.Windows;
using System.Windows.Interop;
using XTranslate.Core;
using XTranslate.Ocr.Windows;
using XTranslate.App.Ocr;

namespace XTranslate.App;

public partial class MainWindow
{
    private void Ocr_Click(object sender,RoutedEventArgs e) => OpenOcr();
    internal void OpenOcr(ILocalOcrEngine? engine=null,Func<string,bool>? confirm=null,
        Func<CancellationToken,Task<OcrImage?>>? capture=null)
    {
        if(_settingsOpen || _exiting)return;
        ResetMouseSelection();CancelRequest();_capturePending?.Cancel();_settingsOpen=true;ShowForUser(false);
        try
        {
            OcrWindow? ocr=null;
            async Task<OcrImage?> CaptureScreenAsync(CancellationToken token)
            {
                var result=await CaptureScreenForOcrAsync(ocr!,token);return result;
            }
            Task<TranslationResult> TranslateOcrAsync(string text,TranslationSettings settings,CancellationToken token) =>
                _service.TranslateAsync(text,settings,token); // One service/request, never the ordinary fallback pipeline.
            ocr=new OcrWindow(engine,confirm,capture:capture ?? CaptureScreenAsync,translate:TranslateOcrAsync,settings:_settings.Copy()) {Owner=this};
            if(ocr.ShowDialog()==true && ocr.AcceptedText is { } text)
            {SourceBox.Text=text;SourceBox.Focus();SetStatus("StatusOcrImported");}
        }
        finally{_settingsOpen=false;UpdateInputState();}
    }
    internal async Task<OcrImage?> CaptureScreenForOcrAsync(OcrWindow view,CancellationToken token,
        Func<ScreenRectangle,ScreenRectangle?>? select=null,ScreenCaptureService? service=null,ScreenRectangle? monitorOverride=null)
    {
        token.ThrowIfCancellationRequested();
        if(!CaptureAppPolicy.TryParse(_settings.CaptureExcludedApps,out var policy))throw new ScreenCaptureException(ScreenCaptureError.UnidentifiedApplication);
        var handle=new WindowInteropHelper(view).Handle;var wasVisible=IsVisible;
        // Direct HWND hiding does not call WPF Hide/DoDialogHide and therefore preserves ShowDialog.
        // It removes the native frame/material, not merely the client area's opacity.
        // Keep the owner alive: hiding a Win32 owner can hide its modal child through WM_SHOWWINDOW.
        try
        {
            NativeMethods.ShowWindow(handle,0);
            if(NativeMethods.IsWindowVisible(handle))throw new ScreenCaptureException(ScreenCaptureError.Unavailable);
            NativeMethods.ShowWindow(_handle,0);
            if(NativeMethods.IsWindowVisible(_handle))throw new ScreenCaptureException(ScreenCaptureError.Unavailable);
            var monitor=monitorOverride ?? ScreenRegionWindow.CurrentMonitor();
            ScreenRectangle? region;
            if(select is not null)region=select(monitor);
            else
            {
                var picker=new ScreenRegionWindow(monitor);
                using var cancelled=token.Register(() => Dispatcher.BeginInvoke(new Action(() =>
                {if(NativeMethods.IsWindow(new WindowInteropHelper(picker).Handle))picker.CancelSelection();})));
                region=picker.ShowDialog()==true?picker.SelectedRegion:null;
                if(picker.Failure is { } failure)throw new ScreenCaptureException(failure);
            }
            if(region is null)return null;
            await Task.Delay(60,token);
            if(NativeMethods.DwmFlush()!=0)throw new ScreenCaptureException(ScreenCaptureError.Unavailable);
            token.ThrowIfCancellationRequested();
            return (service ?? new ScreenCaptureService()).Capture(region.Value,monitor,policy);
        }
        finally
        {
            if(NativeMethods.IsWindow(_handle) && !_exiting && wasVisible)NativeMethods.ShowWindow(_handle,5);
            if(NativeMethods.IsWindow(handle) && !_exiting){NativeMethods.ShowWindow(handle,5);view.Activate();}
        }
    }
}
