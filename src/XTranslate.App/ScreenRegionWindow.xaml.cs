using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using XTranslate.Core;

namespace XTranslate.App;

public partial class ScreenRegionWindow : Window
{
    internal ScreenRectangle Monitor { get; }
    internal ScreenRectangle? SelectedRegion { get; private set; }
    internal ScreenCaptureError? Failure { get; private set; }
    private PixelPoint? _start;
    private bool _closing;
    private readonly Func<PixelPoint> _cursor;
    internal ScreenRegionWindow(ScreenRectangle monitor,Func<PixelPoint>? cursor=null)
    {
        Monitor=monitor;_cursor=cursor ?? (() => NativeMethods.GetCursorPos(out var p) ? new PixelPoint(p.X,p.Y) : throw new ScreenCaptureException(ScreenCaptureError.Unavailable));
        InitializeComponent();Title=HelpText.Text=UiLocalization.Get("OcrScreenHelp");
        System.Windows.Automation.AutomationProperties.SetName(SelectionCanvas,UiLocalization.Get("OcrScreenHelp"));
        SourceInitialized+=(_,_) => PlaceOnMonitor();
        Loaded+=(_,_) => PlaceOnMonitor();
        Closing+=(_,_) => _closing=true;
        Closed+=(_,_) => SelectionCanvas.ReleaseMouseCapture();
        Deactivated+=(_,_) => { if(IsVisible) CancelSelection(); };
    }
    private void PlaceOnMonitor()
    {
        var handle=new WindowInteropHelper(this).Handle;
        if(!Monitor.IsValid || Monitor.Width>int.MaxValue || Monitor.Height>int.MaxValue ||
            !NativeMethods.SetWindowPos(handle,new IntPtr(-1),Monitor.Left,Monitor.Top,(int)Monitor.Width,(int)Monitor.Height,0x0010))
            throw new ScreenCaptureException(ScreenCaptureError.Unavailable);
    }
    private void BeginSelection(object sender,MouseButtonEventArgs e)
    {
        try
        {
            _start=_cursor();SelectionBorder.Visibility=Visibility.Visible;
            if(!SelectionCanvas.CaptureMouse())throw new ScreenCaptureException(ScreenCaptureError.Unavailable);
        }
        catch(ScreenCaptureException failure){Failure=failure.Code;Close();}
        e.Handled=true;
    }
    private void MoveSelection(object sender,MouseEventArgs e)
    {
        if(_start is not { } start || !SelectionCanvas.IsMouseCaptured) return;
        ScreenRectangle rectangle;
        try{rectangle=ScreenRectangle.FromPoints(start,_cursor());}
        catch(ScreenCaptureException failure){Failure=failure.Code;Close();return;}
        var dpi=NativeMethods.GetDpiForWindow(new WindowInteropHelper(this).Handle);if(dpi==0) dpi=96;
        Canvas.SetLeft(SelectionBorder,(rectangle.Left-Monitor.Left)*96d/dpi);Canvas.SetTop(SelectionBorder,(rectangle.Top-Monitor.Top)*96d/dpi);
        SelectionBorder.Width=Math.Max(0,rectangle.Width*96d/dpi);SelectionBorder.Height=Math.Max(0,rectangle.Height*96d/dpi);
    }
    private void EndSelection(object sender,MouseButtonEventArgs e)
    {
        if(_start is not { } start) return;
        SelectionCanvas.ReleaseMouseCapture();
        try{CompleteSelection(ScreenRectangle.FromPoints(start,_cursor()));}
        catch(ScreenCaptureException failure){Failure=failure.Code;Close();}
        e.Handled=true;
    }
    internal void CompleteSelection(ScreenRectangle region)
    {
        ScreenCaptureRules.Validate(region,Monitor);SelectedRegion=region;DialogResult=true;
    }
    internal void CancelSelection()
    {
        if(_closing)return;
        _start=null;SelectedRegion=null;Close();
    }
    private void Window_KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape){CancelSelection();e.Handled=true;}
    }
    internal static ScreenRectangle CurrentMonitor()
    {
        if(!NativeMethods.GetCursorPos(out var cursor)) throw new ScreenCaptureException(ScreenCaptureError.Unavailable);
        var info=new NativeMethods.MonitorInfo {Size=Marshal.SizeOf<NativeMethods.MonitorInfo>()};
        if(!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromPoint(cursor,2),ref info)) throw new ScreenCaptureException(ScreenCaptureError.Unavailable);
        return new(info.Monitor.Left,info.Monitor.Top,info.Monitor.Right,info.Monitor.Bottom);
    }
}
