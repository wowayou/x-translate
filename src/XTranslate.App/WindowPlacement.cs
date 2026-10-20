using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace XTranslate.App;

/// <summary>
/// Fits normal windows to their current monitor in WPF's device-independent units. Keeping native
/// chrome preserves Windows resizing/Snap; scrollable settings stay usable at high DPI or over RDP.
/// No global display settings are changed. Benchmarks deliberately keep their off-screen geometry.
/// </summary>
internal static class WindowPlacement
{
    public static void Attach(Window window)
    {
        if (App.IsBenchmark) return;
        var minimumWidth = window.MinWidth;
        var minimumHeight = window.MinHeight;
        var updating = false;
        void Fit()
        {
            if (updating) return;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var bounds)) return;
            var point = new NativeMethods.Point { X = bounds.Left + (bounds.Right - bounds.Left) / 2, Y = bounds.Top + (bounds.Bottom - bounds.Top) / 2 };
            var monitor = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromPoint(point, 2), ref monitor)) return;
            var dpi = NativeMethods.GetDpiForWindow(handle);
            var limits = WorkAreaInDips(monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top, dpi == 0 ? 96 : dpi);
            updating = true;
            try
            {
                window.MinWidth = Math.Min(minimumWidth, limits.Width);
                window.MinHeight = Math.Min(minimumHeight, limits.Height);
                window.MaxWidth = limits.Width;
                window.MaxHeight = limits.Height;
                if (!double.IsNaN(window.Width) && window.Width > limits.Width) window.Width = limits.Width;
                if (!double.IsNaN(window.Height) && window.Height > limits.Height) window.Height = limits.Height;
            }
            finally { updating = false; }
        }
        // WM_DPICHANGED raises WPF's DpiChanged before the native HWND has its final DPI/size.
        // Coalesce movement events and sample after WPF has applied that transition; otherwise
        // crossing 150% -> 125% leaves MaxWidth/MaxHeight calculated with the old 150% DPI.
        var scheduled = false;
        void ScheduleFit()
        {
            if (updating || scheduled) return;
            scheduled = true;
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                scheduled = false;
                Fit();
            }), DispatcherPriority.Loaded);
        }
        window.SourceInitialized += (_, _) => Fit();
        window.LocationChanged += (_, _) => ScheduleFit();
        window.DpiChanged += (_, _) => ScheduleFit();
    }

    internal static Size WorkAreaInDips(int widthPixels, int heightPixels, double dpi) =>
        new(Math.Max(1, widthPixels * 96 / dpi), Math.Max(1, heightPixels * 96 / dpi));
}
