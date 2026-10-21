using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTranslate.Core;

namespace XTranslate.App.Ocr;

internal interface IScreenCaptureApi
{
    IReadOnlyList<CaptureWindow> Windows(ScreenRectangle region);
    OcrImage Pixels(ScreenRectangle region);
}
internal sealed class ScreenCaptureService(IScreenCaptureApi? api=null)
{
    private readonly IScreenCaptureApi _api=api ?? new WindowsScreenCaptureApi();
    internal OcrImage Capture(ScreenRectangle region,ScreenRectangle monitor,CaptureAppPolicy policy)
    {
        ScreenCaptureRules.Validate(region,monitor);
        var before=_api.Windows(region);ScreenCaptureRules.CheckApplications(policy,before);
        OcrImage? image=null;
        try
        {
            image=_api.Pixels(region);
            var after=_api.Windows(region);ScreenCaptureRules.CheckApplications(policy,after);
            if(!ScreenCaptureRules.SameWindows(before,after)) throw new ScreenCaptureException(ScreenCaptureError.WindowChanged);
            return image;
        }
        catch {image?.Dispose();throw;}
    }
}
internal sealed class WindowsScreenCaptureApi : IScreenCaptureApi
{
    public IReadOnlyList<CaptureWindow> Windows(ScreenRectangle region)
    {
        var windows=new List<CaptureWindow>();var examined=0;ScreenCaptureException? failure=null;
        var completed=NativeMethods.EnumWindows((window,_) =>
        {
            if(++examined>ScreenCaptureRules.MaxExaminedWindows) {failure=new(ScreenCaptureError.UnidentifiedApplication);return false;}
            if(!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window)) return true;
            if(NativeMethods.DwmGetWindowAttribute(window,14,out var cloaked,sizeof(int))==0 && cloaked!=0) return true;
            if(!NativeMethods.GetWindowRect(window,out var r)) {failure=new(ScreenCaptureError.UnidentifiedApplication);return false;}
            var bounds=new ScreenRectangle(r.Left,r.Top,r.Right,r.Bottom);if(!region.Intersects(bounds)) return true;
            if(windows.Count>=ScreenCaptureRules.MaxWindows){failure=new(ScreenCaptureError.UnidentifiedApplication);return false;}
            NativeMethods.GetWindowThreadProcessId(window,out var pid);
            var name=MainWindow.CaptureProcessName(window);
            windows.Add(new(window.ToInt64(),pid,bounds,name));return true;
        },IntPtr.Zero);
        if(!completed) throw failure ?? new ScreenCaptureException(ScreenCaptureError.Unavailable);
        return windows.AsReadOnly();
    }
    public OcrImage Pixels(ScreenRectangle region)
    {
        var dc=NativeMethods.GetDC(IntPtr.Zero);if(dc==IntPtr.Zero) throw new ScreenCaptureException(ScreenCaptureError.Unavailable);
        var memory=IntPtr.Zero;var bitmap=IntPtr.Zero;var previous=IntPtr.Zero;OcrImage? image=null;
        try
        {
            memory=NativeMethods.CreateCompatibleDC(dc);if(memory==IntPtr.Zero) throw new ScreenCaptureException(ScreenCaptureError.Failed);
            var width=(int)region.Width;var height=(int)region.Height;
            var info=new NativeMethods.BitmapInfo {Size=(uint)Marshal.SizeOf<NativeMethods.BitmapInfo>(),Width=width,Height=-height,Planes=1,BitCount=32};
            bitmap=NativeMethods.CreateDIBSection(dc,ref info,0,out var bits,IntPtr.Zero,0);
            if(bitmap==IntPtr.Zero || bits==IntPtr.Zero) throw new ScreenCaptureException(ScreenCaptureError.Failed);
            previous=NativeMethods.SelectObject(memory,bitmap);if(previous==IntPtr.Zero || previous==new IntPtr(-1)) throw new ScreenCaptureException(ScreenCaptureError.Failed);
            if(!NativeMethods.BitBlt(memory,0,0,width,height,dc,region.Left,region.Top,0x00cc0020|0x40000000)) throw new ScreenCaptureException(ScreenCaptureError.Failed);
            // BitBlt can be batched. Complete drawing before dereferencing the DIBSection bits pointer.
            // DwmFlush synchronizes composition, not this GDI writer's memory access.
            if(!NativeMethods.GdiFlush())throw new ScreenCaptureException(ScreenCaptureError.Failed);
            var pixels=new byte[width*height*4];
            try
            {
                Marshal.Copy(bits,pixels,0,pixels.Length);
                for(var i=3;i<pixels.Length;i+=4) pixels[i]=255;
                var preview=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);preview.Freeze();
                return image=new(width,height,preview,pixels);
            }
            catch {Array.Clear(pixels);throw;}
        }
        finally
        {
            var released=true;
            if(previous!=IntPtr.Zero && previous!=new IntPtr(-1))
            {
                var restored=NativeMethods.SelectObject(memory,previous);
                released=restored!=IntPtr.Zero && restored!=new IntPtr(-1);
            }
            // Delete the DC first so a failed restore cannot leave the bitmap selected during deletion.
            if(memory!=IntPtr.Zero)released=NativeMethods.DeleteDC(memory) && released;
            if(bitmap!=IntPtr.Zero)released=NativeMethods.DeleteObject(bitmap) && released;
            released=NativeMethods.ReleaseDC(IntPtr.Zero,dc)!=0 && released;
            if(!released){image?.Dispose();throw new ScreenCaptureException(ScreenCaptureError.CleanupPending);}
        }
    }
}
