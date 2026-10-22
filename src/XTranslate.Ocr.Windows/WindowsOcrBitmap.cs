using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Imaging;
using XTranslate.Core;

namespace XTranslate.Ocr.Windows;

/// <summary>One native bitmap allocation; the memory reference/COM pointer never escapes the synchronous copy.</summary>
internal static class WindowsOcrBitmap
{
    private static readonly Guid ByteAccessId = new("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");

    [SupportedOSPlatform("windows10.0.10240")]
    internal static SoftwareBitmap Create(byte[] pixels, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LocalOcrLimits.ValidateImage(width, height, pixels.Length);
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        try
        {
            using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
            if (buffer.GetPlaneCount() != 1) throw new LocalOcrException(LocalOcrError.InvalidImage);
            var plane = buffer.GetPlaneDescription(0);
            if (plane.Width != width || plane.Height != height) throw new LocalOcrException(LocalOcrError.InvalidImage);
            using var reference = buffer.CreateReference();
            // MemoryBuffer.h: IUnknown + GetBuffer(BYTE**, UINT32*). As() owns only this extra queried ref,
            // never dispose the projection's shared NativeObject or use Marshal.ReleaseComObject.
            using var access = ((WinRT.IWinRTObject)reference).NativeObject.As(ByteAccessId);
            var (pointer, capacity) = GetBuffer(access);
            CopyRows(pixels, width, height, plane.StartIndex, plane.Stride, pointer, capacity, token);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static unsafe (IntPtr Pointer, uint Capacity) GetBuffer(WinRT.IObjectReference access)
    {
        var instance = access.ThisPtr;
        IntPtr pointer = IntPtr.Zero; uint capacity = 0;
        var result = (*(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, int>**)instance)[3](instance, &pointer, &capacity);
        GC.KeepAlive(access);
        Marshal.ThrowExceptionForHR(result);
        return (pointer, capacity);
    }

    internal static void CopyRows(byte[] pixels, int width, int height, int start, int stride, IntPtr destination, uint capacity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LocalOcrLimits.ValidateImage(width, height, pixels.Length);
        var rowBytes = checked(width * 4);
        var last = (long)start + (long)(height - 1) * stride;
        var minimum = Math.Min(start, last); var end = Math.Max(start, last) + rowBytes;
        if (destination == IntPtr.Zero || Math.Abs((long)stride) < rowBytes || minimum < 0 || end > capacity || end > int.MaxValue)
            throw new LocalOcrException(LocalOcrError.InvalidImage);
        // All offsets/capacity checked before the first write. Negative stride is valid for bottom-up planes.
        for (var row = 0; row < height; row++)
        {
            token.ThrowIfCancellationRequested();
            Marshal.Copy(pixels, row * rowBytes, IntPtr.Add(destination, checked((int)((long)start + (long)row * stride))), rowBytes);
        }
    }
}
