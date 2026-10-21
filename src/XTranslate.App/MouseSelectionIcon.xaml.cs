using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MouseSelectionIcon : Window
{
    private readonly Func<Task> _open;
    private HwndSource? _source;
    private IntPtr _handle;
    internal MouseSelectionIcon(Func<Task> open)
    {
        _open=open; InitializeComponent(); Localize(); UiLocalization.Changed+=Changed;
        SourceInitialized+=(_,_) =>
        {
            _handle=new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowLong(_handle,-20,(NativeMethods.GetWindowLong(_handle,-20) | 0x08000080) & ~0x00040000);
            if ((NativeMethods.GetWindowLong(_handle,-20) & 0x08000080) != 0x08000080)
                throw new InvalidOperationException("Mouse icon no-activate styles unavailable.");
            _source=HwndSource.FromHwnd(_handle); _source.AddHook(WindowProc);
        };
        Closed+=(_,_) => { UiLocalization.Changed-=Changed; _source?.RemoveHook(WindowProc); };
    }
    private void Changed(object? sender, EventArgs e) => Localize();
    private void Localize() { var text=UiLocalization.Get("MouseSelectionOpen"); Title=text; OpenButton.Content=text; }
    private async void Open_Click(object sender, RoutedEventArgs e) => await _open();
    private static IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled=true; return new IntPtr(3); } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        return IntPtr.Zero;
    }
    internal bool Contains(PixelPoint point) => IsVisible && NativeMethods.GetWindowRect(_handle,out var r) &&
        point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom;
    internal void ShowNear(PixelPoint point)
    {
        new WindowInteropHelper(this).EnsureHandle();
        Place(point); Show(); Place(point); // Second placement samples any DPI change applied during Show.
    }
    private void Place(PixelPoint point)
    {
        var native=new NativeMethods.Point { X=point.X,Y=point.Y };
        var monitor=new NativeMethods.MonitorInfo { Size=Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromPoint(native,2),ref monitor))
            throw new InvalidOperationException("Mouse icon monitor unavailable.");
        if (!NativeMethods.SetWindowPos(_handle,IntPtr.Zero,monitor.Work.Left,monitor.Work.Top,0,0,0x0015))
            throw new InvalidOperationException("Mouse icon initial placement failed.");
        var dpi=NativeMethods.GetDpiForWindow(_handle); if (dpi == 0) dpi=96;
        var size=Math.Max(1,(int)Math.Ceiling(36*dpi/96d));
        var x=Math.Clamp((long)point.X+12,monitor.Work.Left,Math.Max(monitor.Work.Left,(long)monitor.Work.Right-size));
        var y=Math.Clamp((long)point.Y+12,monitor.Work.Top,Math.Max(monitor.Work.Top,(long)monitor.Work.Bottom-size));
        if (!NativeMethods.SetWindowPos(_handle,new IntPtr(-1),(int)x,(int)y,size,size,0x0010))
            throw new InvalidOperationException("Mouse icon placement failed.");
    }
}
