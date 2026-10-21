using System.Diagnostics;
using XTranslate.Core;

namespace XTranslate.Ocr.Windows;

public interface ILocalOcrEngine
{
    Task<IReadOnlyList<LocalOcrLanguage>> LanguagesAsync(CancellationToken token);
    Task<LocalOcrResult> RecognizeAsync(ReadOnlyMemory<byte> bgra, int width, int height, string language, CancellationToken token);
}
internal interface IWindowsOcrBackend
{
    IReadOnlyList<LocalOcrLanguage> Languages();
    Task<string> RecognizeAsync(byte[] pixels, int width, int height, string language, CancellationToken token);
}

/// <summary>
/// Lazy, one local operation across all instances. A timed-out/cancelled native worker keeps the gate until it
/// actually settles: another request cannot overlap it or spawn unbounded workers. No HTTP/files/PowerShell.
/// </summary>
public sealed class WindowsOcrEngine : ILocalOcrEngine
{
    private static readonly SemaphoreSlim Gate = new(1,1);
    private readonly IWindowsOcrBackend _backend;
    private readonly TimeSpan _timeout;
    public WindowsOcrEngine() : this(new WindowsOcrBackend(),TimeSpan.FromSeconds(10)) { }
    internal WindowsOcrEngine(IWindowsOcrBackend backend, TimeSpan timeout) { _backend=backend; _timeout=timeout; }
    public Task<IReadOnlyList<LocalOcrLanguage>> LanguagesAsync(CancellationToken token) => RunAsync<IReadOnlyList<LocalOcrLanguage>>(() => workToken =>
    {
        workToken.ThrowIfCancellationRequested(); return Task.FromResult(_backend.Languages());
    },token);
    public Task<LocalOcrResult> RecognizeAsync(ReadOnlyMemory<byte> bgra, int width, int height, string language, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LocalOcrLimits.ValidateImage(width,height,bgra.Length); LocalOcrLimits.ValidateLanguage(language);
        return RunAsync<LocalOcrResult>(() =>
        {
            var captured=bgra.ToArray(); // Capture synchronously on admission, before the caller can mutate its buffer.
            return async workToken =>
            {
                try
                {
                    workToken.ThrowIfCancellationRequested();
                    if (!_backend.Languages().Any(x => string.Equals(x.Tag,language,StringComparison.OrdinalIgnoreCase)))
                        throw new LocalOcrException(LocalOcrError.UnsupportedLanguage);
                    var watch=Stopwatch.StartNew();
                    var text=await _backend.RecognizeAsync(captured,width,height,language,workToken).ConfigureAwait(false);
                    workToken.ThrowIfCancellationRequested(); LocalOcrLimits.ValidateText(text);
                    return new LocalOcrResult(text,language,watch.ElapsedMilliseconds);
                }
                finally { Array.Clear(captured); }
            };
        },token);
    }
    private async Task<T> RunAsync<T>(Func<Func<CancellationToken,Task<T>>> prepare, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Gate.Wait(0)) throw new LocalOcrException(LocalOcrError.Busy);
        Func<CancellationToken,Task<T>> run;
        try { run=prepare(); }
        catch { Gate.Release(); throw; }
        var cancel=CancellationTokenSource.CreateLinkedTokenSource(token); cancel.CancelAfter(_timeout);
        var workToken=cancel.Token;
        var worker=Task.Run(async () =>
        {
            try { return await run(workToken).ConfigureAwait(false); }
            catch (LocalOcrException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { throw new LocalOcrException(LocalOcrError.Failed); }
            finally { Gate.Release(); cancel.Dispose(); }
        },CancellationToken.None);
        try { return await worker.WaitAsync(workToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Observe a late backend fault but do not retry it, release its gate early, or expose its text.
            _ = worker.ContinueWith(t => { _ = t.Exception; },CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new LocalOcrException(LocalOcrError.Timeout);
        }
    }
}
