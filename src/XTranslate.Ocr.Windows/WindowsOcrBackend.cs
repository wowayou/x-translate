using System.Runtime.Versioning;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using XTranslate.Core;

namespace XTranslate.Ocr.Windows;

internal sealed class WindowsOcrBackend : IWindowsOcrBackend
{
    public IReadOnlyList<LocalOcrLanguage> Languages()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,10240)) throw new LocalOcrException(LocalOcrError.Unavailable);
        return LanguagesCore();
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private static IReadOnlyList<LocalOcrLanguage> LanguagesCore() => Array.AsReadOnly(OcrEngine.AvailableRecognizerLanguages
        .Select(l => new LocalOcrLanguage(l.LanguageTag,l.NativeName)).ToArray());
    public Task<string> RecognizeAsync(byte[] pixels, int width, int height, string language, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,10240)) throw new LocalOcrException(LocalOcrError.Unavailable);
        return RecognizeCoreAsync(pixels,width,height,language,token);
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private static async Task<string> RecognizeCoreAsync(byte[] pixels, int width, int height, string language, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension) throw new LocalOcrException(LocalOcrError.ImageTooLarge);
        var requested=new global::Windows.Globalization.Language(language);
        var engine=OcrEngine.TryCreateFromLanguage(requested);
        if (engine is null || !string.Equals(engine.RecognizerLanguage.LanguageTag,language,StringComparison.OrdinalIgnoreCase))
            throw new LocalOcrException(LocalOcrError.UnsupportedLanguage);
        using var writer=new DataWriter(); writer.WriteBytes(pixels);
        using var bitmap=SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(),BitmapPixelFormat.Bgra8,width,height,BitmapAlphaMode.Ignore);
        token.ThrowIfCancellationRequested();
        var operation=engine.RecognizeAsync(bitmap);
        var done=new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.Completed=Completed;
        void Completed(IAsyncOperation<OcrResult> current, AsyncStatus status)
        {
            try { done.TrySetResult(current.GetResults()); }
            catch (OperationCanceledException) { done.TrySetCanceled(); }
            catch { done.TrySetException(new LocalOcrException(LocalOcrError.Failed)); }
        }
        var cancellationFailed=0;
        using var cancellation=token.Register(() =>
        {
            // Cancellation requests native termination but the owned worker/bitmap remains until Completed.
            // Never free its gate or native input while the OS might still use them.
            try { operation.Cancel(); }
            catch { Interlocked.Exchange(ref cancellationFailed,1); }
        });
        try
        {
            var result=await done.Task.ConfigureAwait(false);
            if (Volatile.Read(ref cancellationFailed) != 0) throw new LocalOcrException(LocalOcrError.Failed);
            token.ThrowIfCancellationRequested(); return result.Text;
        }
        finally { operation.Close(); }
    }
}
