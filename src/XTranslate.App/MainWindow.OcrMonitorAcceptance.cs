using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XTranslate.App.Ocr;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private readonly List<object> _ocrMonitorEvidence = new();

    // Uses real monitor/DPI/window geometry and controlled WPF mouse events, never global input injection.
    // The cursor delegate is fixed; no user screen pixels, clipboard or third-party text is read here.
    private async Task VerifyOcrMonitorsAsync(List<string> checks)
    {
        var monitors = new List<ScreenRectangle>();
        NativeMethods.MonitorEnumProc collect = (IntPtr monitor, IntPtr dc, ref NativeMethods.Rect bounds, IntPtr data) =>
        {
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (monitors.Count >= 16 || !NativeMethods.GetMonitorInfo(monitor, ref info)) return false;
            monitors.Add(new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom));
            return true;
        };
        RequireStream(NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero) && monitors.Count is > 0 and <= 16,
            "Attached OCR monitors could not be enumerated within the bounded fixture");
        foreach (var monitor in monitors.Concat(monitors.Take(1)))
        {
            var first = new PixelPoint(monitor.Left + 140, monitor.Top + 120);
            var last = new PixelPoint(monitor.Left + 600, monitor.Top + 340);
            var region = ScreenRectangle.FromPoints(first, last);
            RequireStream(region.Right < monitor.Right && region.Bottom < monitor.Bottom, "Fixed OCR monitor fixture does not fit");
            var point = first;
            await WithLearningModalAsync<ScreenRegionWindow>(() => new ScreenRegionWindow(monitor, () => point).ShowDialog(), async picker =>
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                picker.UpdateLayout();
                var handle = new WindowInteropHelper(picker).Handle;
                var hasBounds = NativeMethods.GetWindowRect(handle, out var bounds);
                RequireStream(hasBounds && new ScreenRectangle(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom) == monitor,
                    $"OCR picker physical bounds differ: readable={hasBounds}, actual={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}, expected={monitor.Left},{monitor.Top},{monitor.Right},{monitor.Bottom}, dpi={NativeMethods.GetDpiForWindow(handle)}");
                var dpi = NativeMethods.GetDpiForWindow(handle);
                var wpfDpi = VisualTreeHelper.GetDpi(picker);
                RequireStream(dpi > 0 && Math.Abs(wpfDpi.PixelsPerInchX - dpi) < 1, "OCR WPF/native DPI differs on the selected monitor");
                picker.SelectionCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
                point = last;
                picker.SelectionCanvas.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
                picker.UpdateLayout();
                var border = picker.SelectionBorder;
                var topLeft = border.PointToScreen(new Point(0, 0));
                var bottomRight = border.PointToScreen(new Point(border.ActualWidth, border.ActualHeight));
                RequireStream(Math.Abs(topLeft.X - region.Left) < 1 && Math.Abs(topLeft.Y - region.Top) < 1 &&
                    Math.Abs(bottomRight.X - region.Right) < 1 && Math.Abs(bottomRight.Y - region.Bottom) < 1,
                    "OCR selection outline does not map back to the exact selected physical pixels");
                picker.SelectionCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                RequireStream(picker.SelectedRegion == region && !picker.SelectionCanvas.IsMouseCaptured, "OCR monitor selection lost exact bounds or capture cleanup");
                _ocrMonitorEvidence.Add(new { monitor, dpi, wpfDpi = wpfDpi.PixelsPerInchX, region,
                    outlinePhysical = new { left = topLeft.X, top = topLeft.Y, right = bottomRight.X, bottom = bottomRight.Y },
                    input = "controlled WPF routed events; fixed physical cursor delegate", screenPixelsRead = false });
            });
            point = last;
            await WithLearningModalAsync<ScreenRegionWindow>(() => new ScreenRegionWindow(monitor, () => point).ShowDialog(), async picker =>
            {
                picker.SelectionCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
                point = first;
                picker.SelectionCanvas.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
                picker.SelectionCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                RequireStream(picker.SelectedRegion == region && !picker.SelectionCanvas.IsMouseCaptured, "Reverse OCR drag changed physical bounds");
                await Task.CompletedTask;
            });
            point = first;
            await WithLearningModalAsync<ScreenRegionWindow>(() => new ScreenRegionWindow(monitor, () => point).ShowDialog(), async picker =>
            {
                picker.SelectionCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
                point = new(monitor.Left - 10, monitor.Top + 200);
                picker.SelectionCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                RequireStream(picker.SelectedRegion is null && picker.Failure == ScreenCaptureError.InvalidRegion &&
                    !picker.SelectionCanvas.IsMouseCaptured, "Cross-monitor OCR drag was cropped into a successful region");
                await Task.CompletedTask;
            });
        }
        checks.Add($"OCR picker on {monitors.Count} real attached monitor(s) and return trip: physical HWND/WPF DPI and outline agree; forward/reverse drags retain exact pixels and cross-monitor endpoints fail without reads (controlled events, not human gestures)");
    }
}
