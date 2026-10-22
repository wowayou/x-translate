using System.Diagnostics;
using System.Text.Json;
using XTranslate.Core;
using XTranslate.Ocr.Windows;

var tests = new (string Name,Func<Task> Run)[]
{
    ("Constructing OCR does not call native engine; pre-cancelled work never enters backend", NoWork),
    ("Explicit installed language and immutable pixel snapshot preserve caller data and exact output", Snapshot),
    ("Unsupported/invalid image and language send no recognition; no language fallback", Rejections),
    ("Backend/empty/oversized output failures are safe and never retried", Failures),
    ("Timeout keeps single-worker gate while ignored native cancellation is outstanding", TimeoutGate),
    ("Explicit cancellation rejects late results and concurrent requests without releasing ownership early", CancelGate),
    ("Same-size bounded snapshots reuse one zeroed buffer across instances without retaining larger images", SnapshotReuse),
    ("Cancelled ignored backend keeps snapshot ownership until actual completion, then reuses only cleared bytes", CancelledSnapshotReuse),
    ("Resized and rejected snapshots retain exact admission isolation without stale bytes", SnapshotBoundaries),
    ("Native row copy preserves packed, padded, bottom-up planes and untouched guards", BitmapTests.Layouts),
    ("Invalid row layouts, capacity arithmetic and pre-cancellation reject before any native write", BitmapTests.Rejections),
};
var failed=0;
foreach (var (name,run) in tests)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(5)); Console.WriteLine("PASS "+name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL "+name+": "+e); }
}
if (args.Contains("--native"))
{
    try { await BitmapTests.Native().WaitAsync(TimeSpan.FromSeconds(5)); Console.WriteLine("PASS Real native bitmap readback uses an independent buffer interface"); }
    catch(Exception e) { failed++; Console.WriteLine("FAIL Real native bitmap: "+e); }
    try { await Native().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS Real local Windows OCR with generated fixed images"); }
    catch(Exception e) { failed++; Console.WriteLine("FAIL Real local OCR: "+e); }
}
Console.WriteLine($"{tests.Length+(args.Contains("--native")?2:0)-failed}/{tests.Length+(args.Contains("--native")?2:0)} OCR tests passed");
return failed==0?0:1;

static void Check(bool value,string message="OCR contract failed") { if (!value) throw new Exception(message); }
static async Task Error(Func<Task> call,LocalOcrError code)
{
    try { await call(); }
    catch(LocalOcrException e) { Check(e.Code==code && e.InnerException is null && !e.ToString().Contains("private_marker")); return; }
    throw new Exception("Expected OCR error "+code);
}
static async Task Cancelled(Func<Task> call)
{
    try { await call(); } catch(OperationCanceledException) { return; }
    throw new Exception("Expected cancellation");
}
static async Task NoWork()
{
    var backend=new FakeBackend(); var engine=new WindowsOcrEngine(backend,TimeSpan.FromSeconds(1));
    Check(backend.LanguageCalls==0 && backend.ReadCalls==0);
    using var cancelled=new CancellationTokenSource();cancelled.Cancel();
    await Cancelled(() => engine.LanguagesAsync(cancelled.Token));
    await Cancelled(() => engine.RecognizeAsync(new byte[4],1,1,"en-US",cancelled.Token));
    Check(backend.LanguageCalls==0 && backend.ReadCalls==0);
}
static async Task Snapshot()
{
    var backend=new FakeBackend(); byte[]? owned=null;
    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    backend.Read=(pixels,token) => { owned=pixels;entered.SetResult();return release.Task; };
    var engine=new WindowsOcrEngine(backend,TimeSpan.FromSeconds(2)); var original=new byte[]{1,2,3,4};
    var call=engine.RecognizeAsync(original,1,1,"en-US",CancellationToken.None);
    original[0]=9; // The caller mutates immediately after admission, not just after backend capture.
    await entered.Task;
    Check(owned![0]==1,"Caller changed captured native pixels");
    release.SetResult("  fixed\n原文  "); var result=await call;
    Check(result.Text=="  fixed\n原文  " && result.Language=="en-US" && original[0]==9 && owned.All(x => x==0),"Snapshot text/pixels drifted");
    Check(backend.ReadCalls==1 && backend.LanguageCalls==1);
}
static async Task Rejections()
{
    var b=new FakeBackend();var e=new WindowsOcrEngine(b,TimeSpan.FromSeconds(1));
    await Error(() => e.RecognizeAsync(new byte[4],1,1,"zxx",CancellationToken.None),LocalOcrError.UnsupportedLanguage);
    await Error(() => e.RecognizeAsync(new byte[3],1,1,"en-US",CancellationToken.None),LocalOcrError.InvalidImage);
    await Error(() => e.RecognizeAsync(new byte[4],1,1,"../en",CancellationToken.None),LocalOcrError.UnsupportedLanguage);
    Check(b.ReadCalls==0 && b.LanguageCalls==1);
}
static async Task Failures()
{
    foreach (var (text,code) in new[] {(" ",LocalOcrError.EmptyText),(new string('x',20001),LocalOcrError.OutputTooLarge),("bad\ud800",LocalOcrError.Failed)})
    {
        var b=new FakeBackend { Read=(_,_) => Task.FromResult(text) }; var e=new WindowsOcrEngine(b,TimeSpan.FromSeconds(1));
        await Error(() => e.RecognizeAsync(new byte[4],1,1,"en-US",CancellationToken.None),code);Check(b.ReadCalls==1);
    }
    var fault=new FakeBackend { Read=(_,_) => throw new IOException("private_marker") };
    await Error(() => new WindowsOcrEngine(fault,TimeSpan.FromSeconds(1)).RecognizeAsync(new byte[4],1,1,"en-US",CancellationToken.None),LocalOcrError.Failed);
    Check(fault.ReadCalls==1);
}
static async Task TimeoutGate()
{
    var release=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    byte[]? owned = null;
    var b=new FakeBackend { Read=(pixels,_) => {owned=pixels;entered.TrySetResult();return release.Task;} };
    var e=new WindowsOcrEngine(b,TimeSpan.FromMilliseconds(70));
    var timed=e.RecognizeAsync(new byte[]{1,2,3,4},1,1,"en-US",CancellationToken.None);
    await entered.Task;
    try
    {
        await Error(() => timed,LocalOcrError.Timeout);
        await Error(() => new WindowsOcrEngine(new FakeBackend(),TimeSpan.FromSeconds(1)).LanguagesAsync(CancellationToken.None),LocalOcrError.Busy);
        Check(b.ReadCalls==1 && owned!.AsSpan().SequenceEqual(new byte[]{1,2,3,4}), "Deadline returned a live backend snapshot to the cache");
    }
    finally { release.TrySetResult("late private_marker"); }
    await WaitForGate(e);
    Check(owned!.All(value => value == 0), "Settled deadline snapshot retained pixels");
    var next = new FakeBackend { Read=(pixels,_) => { Check(ReferenceEquals(owned,pixels) && pixels.All(value => value == 5), "Deadline snapshot was not cleared before reuse"); return Task.FromResult("next"); } };
    await new WindowsOcrEngine(next,TimeSpan.FromSeconds(1)).RecognizeAsync(new byte[]{5,5,5,5},1,1,"en-US",CancellationToken.None);
    Check(owned!.All(value => value == 0));
}
static async Task CancelGate()
{
    var release=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var b=new FakeBackend { Read=(_,_) => {entered.TrySetResult();return release.Task;} };
    var e=new WindowsOcrEngine(b,TimeSpan.FromSeconds(1));using var cancel=new CancellationTokenSource();
    var call=e.RecognizeAsync(new byte[4],1,1,"en-US",cancel.Token);await entered.Task;cancel.Cancel();
    try { await Cancelled(() => call);await Error(() => e.LanguagesAsync(CancellationToken.None),LocalOcrError.Busy); }
    finally {release.TrySetResult("late");}
    await WaitForGate(e);
}
static async Task SnapshotReuse()
{
    var snapshots = new List<byte[]>();
    var backend = new FakeBackend
    {
        Read = (pixels, _) =>
        {
            Check(pixels.Length == 16 && pixels.All(value => value == 7), "Reusable snapshot does not preserve exact caller bytes");
            snapshots.Add(pixels); return Task.FromResult("fixed");
        }
    };
    var input = Enumerable.Repeat((byte)7, 16).ToArray();
    for (var index = 0; index < 3; index++)
    {
        var result = await new WindowsOcrEngine(backend, TimeSpan.FromSeconds(2)).RecognizeAsync(input, 4, 1, "en-US", CancellationToken.None);
        Check(result.Text == "fixed" && snapshots[^1].All(value => value == 0) && input.All(value => value == 7));
    }
    Check(snapshots.Count == 3 && snapshots.All(snapshot => ReferenceEquals(snapshot, snapshots[0])), "Same-size snapshot was not reused across instances");
    byte[]? bound1 = null, bound2 = null;
    var boundary = new FakeBackend { Read = (pixels, _) => { if (bound1 is null) bound1 = pixels; else bound2 = pixels; return Task.FromResult("boundary"); } };
    var boundaryInput = new byte[WindowsOcrEngine.MaxReusableSnapshotBytes];
    for (var index = 0; index < 2; index++)
        await new WindowsOcrEngine(boundary, TimeSpan.FromSeconds(2)).RecognizeAsync(boundaryInput, 512, 512, "en-US", CancellationToken.None);
    Check(ReferenceEquals(bound1, bound2) && bound1!.All(value => value == 0), "Inclusive snapshot ceiling did not reuse cleared bytes");
    byte[]? large1 = null, large2 = null;
    var large = new FakeBackend { Read = (pixels, _) => { if (large1 is null) large1 = pixels; else large2 = pixels; return Task.FromResult("large"); } };
    var largeInput = new byte[513 * 512 * 4];
    for (var index = 0; index < 2; index++)
        await new WindowsOcrEngine(large, TimeSpan.FromSeconds(2)).RecognizeAsync(largeInput, 513, 512, "en-US", CancellationToken.None);
    Check(!ReferenceEquals(large1, large2) && large1!.All(value => value == 0) && large2!.All(value => value == 0), "Larger image retained/reused a snapshot beyond the bound");
    var faultSnapshots = new List<byte[]>();
    var fault = new FakeBackend { Read = (pixels, _) => { faultSnapshots.Add(pixels); throw new IOException("private_marker"); } };
    for (var index = 0; index < 2; index++)
    {
        await Error(() => new WindowsOcrEngine(fault, TimeSpan.FromSeconds(2)).RecognizeAsync(new byte[20], 5, 1, "en-US", CancellationToken.None), LocalOcrError.Failed);
        Check(faultSnapshots[^1].All(value => value == 0));
    }
    Check(ReferenceEquals(faultSnapshots[0], faultSnapshots[1]), "Failed backend did not return a safe cleared bounded snapshot");
}
static async Task CancelledSnapshotReuse()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    byte[]? owned = null;
    var backend = new FakeBackend { Read = (pixels, _) => { owned = pixels; entered.SetResult(); return release.Task; } };
    var engine = new WindowsOcrEngine(backend, TimeSpan.FromSeconds(2));
    using var cancel = new CancellationTokenSource();
    var input = Enumerable.Repeat((byte)9, 24).ToArray();
    var pending = engine.RecognizeAsync(input, 6, 1, "en-US", cancel.Token);
    await entered.Task; cancel.Cancel();
    try
    {
        await Cancelled(() => pending);
        Check(owned!.All(value => value == 9), "Caller cancellation cleared/recycled a live backend snapshot");
        await Error(() => new WindowsOcrEngine(new FakeBackend(), TimeSpan.FromSeconds(1)).RecognizeAsync(new byte[24], 6, 1, "en-US", CancellationToken.None), LocalOcrError.Busy);
        Check(owned!.All(value => value == 9));
    }
    finally { release.TrySetResult("late"); }
    await WaitForGate(engine);
    Check(owned!.All(value => value == 0), "Settled cancelled snapshot was not cleared");
    var next = new FakeBackend { Read = (pixels, _) => { Check(ReferenceEquals(owned, pixels) && pixels.All(value => value == 5), "Next request reused a live snapshot or stale bytes"); return Task.FromResult("next"); } };
    await new WindowsOcrEngine(next, TimeSpan.FromSeconds(2)).RecognizeAsync(Enumerable.Repeat((byte)5, 24).ToArray(), 6, 1, "en-US", CancellationToken.None);
    Check(owned!.All(value => value == 0) && input.All(value => value == 9), "Reuse changed caller bytes or retained last request pixels");
}
static async Task SnapshotBoundaries()
{
    byte[]? first = null, second = null;
    var pixels = Enumerable.Repeat((byte)3, 28).ToArray();
    var backend = new FakeBackend { Read = (snapshot, _) => { Check(snapshot.All(value => value == 3)); first ??= snapshot; return Task.FromResult("fixed"); } };
    await new WindowsOcrEngine(backend, TimeSpan.FromSeconds(1)).RecognizeAsync(pixels, 7, 1, "en-US", CancellationToken.None);
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Cancelled(() => new WindowsOcrEngine(backend, TimeSpan.FromSeconds(1)).RecognizeAsync(pixels, 7, 1, "en-US", cancel.Token));
    await Error(() => new WindowsOcrEngine(backend, TimeSpan.FromSeconds(1)).RecognizeAsync(pixels, 7, 1, "zxx", CancellationToken.None), LocalOcrError.UnsupportedLanguage);
    Check(first!.All(value => value == 0) && backend.ReadCalls == 1, "Rejected recognition retained bytes or reached the backend");
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var reused = new FakeBackend { Read = (snapshot, _) => { Check(ReferenceEquals(first, snapshot) && snapshot.All(value => value == 3), "Reuse lost synchronous admission isolation"); entered.SetResult(); return release.Task; } };
    var pending = new WindowsOcrEngine(reused, TimeSpan.FromSeconds(2)).RecognizeAsync(pixels, 7, 1, "en-US", CancellationToken.None);
    Array.Fill(pixels, (byte)9);
    try { await entered.Task; Check(first!.All(value => value == 3)); }
    finally { release.TrySetResult("fixed"); }
    await pending;
    Check(first!.All(value => value == 0) && pixels.All(value => value == 9));
    var resized = new FakeBackend { Read = (snapshot, _) => { second = snapshot; Check(snapshot.Length == 32 && snapshot.All(value => value == 6)); return Task.FromResult("resized"); } };
    await new WindowsOcrEngine(resized, TimeSpan.FromSeconds(1)).RecognizeAsync(Enumerable.Repeat((byte)6, 32).ToArray(), 8, 1, "en-US", CancellationToken.None);
    var restored = new FakeBackend { Read = (snapshot, _) => { Check(!ReferenceEquals(first, snapshot) && !ReferenceEquals(second, snapshot) && snapshot.Length == 28 && snapshot.All(value => value == 9), "Size change reused an oversized buffer or retained multiple images"); return Task.FromResult("restored"); } };
    await new WindowsOcrEngine(restored, TimeSpan.FromSeconds(1)).RecognizeAsync(pixels, 7, 1, "en-US", CancellationToken.None);
    Check(first!.All(value => value == 0) && second!.All(value => value == 0));
}
static async Task WaitForGate(WindowsOcrEngine engine)
{
    // Synchronization polling is bounded; it does not re-run recognition or contact an API.
    for(var i=0;i<50;i++)
    {
        try { await engine.LanguagesAsync(CancellationToken.None);return; }
        catch(LocalOcrException e) when(e.Code==LocalOcrError.Busy) { await Task.Delay(5); }
    }
    throw new Exception("Settled OCR worker did not release its gate");
}
static async Task Native()
{
    var engine=new WindowsOcrEngine();var languages=await engine.LanguagesAsync(CancellationToken.None);
    Console.WriteLine("LANGUAGES "+JsonSerializer.Serialize(languages));
    Check(languages.Any(x => x.Tag=="en-US"),"Explicit en-US native lane unavailable; no pack was installed");
    foreach(var (text,language) in new[] {("HELLO WORLD 123","en-US"),("本地文字识别","zh-Hans-CN")})
    {
        if(!languages.Any(x => x.Tag==language)) { Console.WriteLine("SKIP installed language unavailable: "+language);continue; }
        using var image=new System.Drawing.Bitmap(960,160);
        using(var g=System.Drawing.Graphics.FromImage(image))
        using(var f=new System.Drawing.Font(language=="en-US"?"Segoe UI":"Microsoft YaHei UI",42,System.Drawing.FontStyle.Regular,System.Drawing.GraphicsUnit.Pixel))
        {g.Clear(System.Drawing.Color.White);g.DrawString(text,f,System.Drawing.Brushes.Black,30,45);}
        var pixels=Pixels(image);var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));var results=new List<object>();
        for(var i=0;i<2;i++)
        {
            var result=await engine.RecognizeAsync(pixels,960,160,language,CancellationToken.None);
            results.Add(result);Check(result.Text.Replace(" ","",StringComparison.Ordinal)==text.Replace(" ","",StringComparison.Ordinal),"Synthetic native OCR output differs");
        }
        Check(hash==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)),"Native recognition modified caller image");
        Console.WriteLine("NATIVE "+JsonSerializer.Serialize(new{language,pixelSha256=hash,results}));
    }
    await Error(() => engine.RecognizeAsync(new byte[4],1,1,"zxx",CancellationToken.None),LocalOcrError.UnsupportedLanguage);
    var blank=Enumerable.Repeat((byte)255,960*160*4).ToArray();
    await Error(() => engine.RecognizeAsync(blank,960,160,"en-US",CancellationToken.None),LocalOcrError.EmptyText);
}
static byte[] Pixels(System.Drawing.Bitmap image)
{
    var data=image.LockBits(new(0,0,image.Width,image.Height),System.Drawing.Imaging.ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    try {var bytes=new byte[image.Width*image.Height*4];System.Runtime.InteropServices.Marshal.Copy(data.Scan0,bytes,0,bytes.Length);return bytes;}
    finally {image.UnlockBits(data);}
}
sealed class FakeBackend : IWindowsOcrBackend
{
    internal int LanguageCalls,ReadCalls;
    internal Func<byte[],CancellationToken,Task<string>> Read=(_,_) => Task.FromResult("text");
    public IReadOnlyList<LocalOcrLanguage> Languages(){LanguageCalls++;return new[]{new LocalOcrLanguage("en-US","English")};}
    public Task<string> RecognizeAsync(byte[] pixels,int width,int height,string language,CancellationToken token){ReadCalls++;return Read(pixels,token);}
}
