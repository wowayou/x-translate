using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Imaging;
using XTranslate.Core;
using XTranslate.Ocr.Windows;

internal static class BitmapTests
{
    private const byte Guard = 0xcc;
    private static readonly Guid BufferAccessId = new("905a0fef-bc53-11df-8c49-001e4fc686da");

    internal static Task Layouts()
    {
        var pixels = Enumerable.Range(1, 36).Select(value => (byte)value).ToArray();
        foreach (var (start, stride, capacity) in new[] { (0, 12, 36u), (7, 16, 51u), (39, -16, 51u) })
        {
            var destination = Marshal.AllocHGlobal(80);
            try
            {
                var expected = Enumerable.Repeat(Guard, 80).ToArray();
                Marshal.Copy(expected, 0, destination, expected.Length);
                for (var row = 0; row < 3; row++) pixels.AsSpan(row * 12, 12).CopyTo(expected.AsSpan(start + row * stride, 12));
                WindowsOcrBitmap.CopyRows(pixels, 3, 3, start, stride, destination, capacity, CancellationToken.None);
                var actual = new byte[80]; Marshal.Copy(destination, actual, 0, actual.Length);
                Require(actual.AsSpan().SequenceEqual(expected), "BGRA row order, padding or allocation guard changed");
                Require(pixels.AsSpan().SequenceEqual(Enumerable.Range(1, 36).Select(value => (byte)value).ToArray()), "Row copy changed caller bytes");
            }
            finally { Marshal.FreeHGlobal(destination); }
        }
        var single = Marshal.AllocHGlobal(12);
        try
        {
            var expected = Enumerable.Repeat(Guard, 12).ToArray(); Marshal.Copy(expected, 0, single, expected.Length);
            var pixel = new byte[] { 1, 2, 3, 4 }; pixel.CopyTo(expected, 5);
            WindowsOcrBitmap.CopyRows(pixel, 1, 1, 5, -4, single, 9, CancellationToken.None);
            var actual = new byte[12]; Marshal.Copy(single, actual, 0, actual.Length);
            Require(actual.AsSpan().SequenceEqual(expected), "Single bottom-up row or exact capacity boundary differs");
        }
        finally { Marshal.FreeHGlobal(single); }
        return Task.CompletedTask;
    }

    internal static Task Rejections()
    {
        var pixels = Enumerable.Range(1, 36).Select(value => (byte)value).ToArray();
        var destination = Marshal.AllocHGlobal(80);
        try
        {
            var untouched = Enumerable.Repeat(Guard, 80).ToArray(); Marshal.Copy(untouched, 0, destination, untouched.Length);
            foreach (var (start, stride, capacity) in new[]
            {
                (0, 12, 35u), (24, -12, 35u), (0, 12, 0u), (-1, 12, 80u), (0, -12, 80u),
                (0, 0, 80u), (0, 11, 80u), (24, -11, 80u),
                (int.MaxValue, 12, uint.MaxValue), (0, int.MaxValue, uint.MaxValue), (0, int.MinValue, uint.MaxValue)
            })
            {
                Expect(LocalOcrError.InvalidImage, () => WindowsOcrBitmap.CopyRows(pixels, 3, 3, start, stride, destination, capacity, CancellationToken.None));
                Unchanged();
            }
            Expect(LocalOcrError.InvalidImage, () => WindowsOcrBitmap.CopyRows(pixels, 3, 3, 0, 12, IntPtr.Zero, 80, CancellationToken.None));
            Expect(LocalOcrError.InvalidImage, () => WindowsOcrBitmap.CopyRows(pixels, 0, 3, 0, 12, destination, 80, CancellationToken.None));
            Expect(LocalOcrError.InvalidImage, () => WindowsOcrBitmap.CopyRows(pixels[..^1], 3, 3, 0, 12, destination, 80, CancellationToken.None));
            Expect(LocalOcrError.ImageTooLarge, () => WindowsOcrBitmap.CopyRows(pixels, 8193, 1, 0, 12, destination, 80, CancellationToken.None));
            Expect(LocalOcrError.ImageTooLarge, () => WindowsOcrBitmap.CopyRows(pixels, 8000, 1001, 0, 12, destination, 80, CancellationToken.None));
            Expect(LocalOcrError.ImageTooLarge, () => WindowsOcrBitmap.CopyRows(pixels, int.MaxValue, int.MaxValue, 0, 12, destination, 80, CancellationToken.None));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try
            {
                WindowsOcrBitmap.CopyRows(pixels, 3, 3, 0, 12, destination, 80, cancel.Token);
                throw new Exception("Pre-cancelled row copy wrote pixels");
            }
            catch (OperationCanceledException error) { Require(error.CancellationToken == cancel.Token, "Cancellation lost caller identity"); }
            Unchanged();

            void Unchanged()
            {
                var actual = new byte[80]; Marshal.Copy(destination, actual, 0, actual.Length);
                Require(actual.AsSpan().SequenceEqual(untouched), "Rejected layout partially wrote native pixels");
            }
        }
        finally { Marshal.FreeHGlobal(destination); }
        return Task.CompletedTask;
    }

    internal static Task Native()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) throw new Exception("Native bitmap lane requires Windows 10; not skipped");
        RoundTrip();
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static void RoundTrip()
    {
        foreach (var (width, height) in new[] { (1, 1), (3, 2), (17, 5), (960, 160) })
        {
            var pixels = new byte[width * height * 4];
            for (var offset = 0; offset < pixels.Length; offset++) pixels[offset] = (byte)(offset * 37 + offset / (width * 4));
            var expected = pixels.ToArray();
            using var bitmap = WindowsOcrBitmap.Create(pixels, width, height, CancellationToken.None);
            Require(bitmap.PixelWidth == width && bitmap.PixelHeight == height && bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                bitmap.BitmapAlphaMode == BitmapAlphaMode.Ignore && pixels.AsSpan().SequenceEqual(expected), "Native bitmap changed dimensions, format or caller pixels");
            Array.Fill(pixels, (byte)0xef); // A completed copy must not borrow the mutable managed source.
            // Independent readback through SoftwareBitmap.CopyToBuffer and IBufferByteAccess, not the production
            // IMemoryBufferByteAccess/layout path. Dispose only the extra queried COM reference, never NativeObject.
            var buffer = new Windows.Storage.Streams.Buffer((uint)expected.Length);
            bitmap.CopyToBuffer(buffer);
            Require(buffer.Length == expected.Length && buffer.Capacity >= expected.Length, "Native readback buffer length differs");
            using var access = ((WinRT.IWinRTObject)buffer).NativeObject.As(BufferAccessId);
            var instance = access.ThisPtr;
            var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBuffer>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(getBuffer(instance, out var pointer));
            Require(pointer != IntPtr.Zero, "Native readback has no buffer");
            var actual = new byte[expected.Length]; Marshal.Copy(pointer, actual, 0, actual.Length);
            GC.KeepAlive(access);
            Require(actual.AsSpan().SequenceEqual(expected), "Native bitmap byte-for-byte readback differs or retained caller source");
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try
        {
            using var unexpected = WindowsOcrBitmap.Create(new byte[4], 1, 1, cancel.Token);
            throw new Exception("Pre-cancelled native bitmap was created");
        }
        catch (OperationCanceledException error) { Require(error.CancellationToken == cancel.Token, "Native copy lost cancellation identity"); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBuffer(IntPtr instance, out IntPtr pointer);

    private static void Expect(LocalOcrError expected, Action call)
    {
        try { call(); }
        catch (LocalOcrException error) when (error.Code == expected) { return; }
        throw new Exception("Expected bitmap error " + expected);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
