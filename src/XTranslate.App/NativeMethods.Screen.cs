using System.Runtime.InteropServices;

namespace XTranslate.App;

internal static partial class NativeMethods
{
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
    internal delegate bool WindowEnumProc(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool EnumWindows(WindowEnumProc callback,IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window,uint attribute,out int value,int size);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc,IntPtr item);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
    [DllImport("gdi32.dll",SetLastError=true)] internal static extern bool BitBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sx,int sy,uint operation);
    [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
    {
        public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,ImageSize;
        public int XPixelsPerMeter,YPixelsPerMeter;public uint ColorsUsed,ColorsImportant;
    }
}
