using System.Diagnostics;
using System.Text;
using XTranslate.App.Learning;
using XTranslate.Core.Learning;
using static TestSupport;

internal static class LearningStorageTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    internal static LearningContent Entry(string prompt = "account for") => new(LearningKind.Phrase, prompt, "把……考虑在内",
        "We need to account for latency.", "personal note", "en", "zh-CN", LearningOrigin.Personal);
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Learning construction/read/pre-cancellation create no data files", NoWrites),
        ("Learning create/edit/delete survives process restart and portable-folder move", Lifecycle),
        ("Learning stale writers and pre-restore generations cannot overwrite newer data", Conflict),
        ("Learning cancellation and injected write failure preserve the old bytes", Failure),
        ("Learning read-only or locked output fails without deleting the saved book", ReadOnly),
        ("Learning corruption/future JSON is explicit, never treated as an empty notebook", Corruption),
        ("Learning backup preview commits the captured immutable data, not changed source bytes", Backup),
        ("Learning export cancellation/failure preserves previous backup and input ownership", ExportFailure),
        ("Learning exports cannot overwrite managed application data", ManagedPaths),
        ("Learning busy writers fail explicitly while read-only loading remains available", Busy),
        ("Learning failed temporary cleanup is reported rather than silently swallowed", Cleanup),
        ("Learning interrupted writer before publication preserves the old notebook", Interrupted),
        ("Learning fixed-size sample retains bounded text and records offline load time", Sample)
    };
    private static string PathOf(Sandbox box) => Path.Combine(box.Data,"learning","book.json");
    private static async Task<LearningBook> Seed(Sandbox box)
    {
        var store = new LearningStore(box.Data);
        return await store.ChangeAsync(await store.LoadAsync(), b => b.Add(Entry(), Now));
    }
    private static async Task<LearningStorageException> Error(Func<Task> action, params LearningStorageError[] codes)
    {
        var error = await Throws<LearningStorageException>(action);
        Check(codes.Contains(error.Code) && error.InnerException is null && !error.ToString().Contains("private_marker"), "Unsafe learning storage error");
        return error;
    }
    private static async Task NoWrites()
    {
        using var box = new Sandbox(); var store = new LearningStore(box.Data);
        Check((await store.LoadAsync()).Cards.Count == 0 && !Directory.Exists(box.Data), "Reading an unused learning book wrote files");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => store.ChangeAsync(LearningBook.Empty, b => b.Add(Entry(),Now), cancelled.Token));
        Check(!Directory.Exists(box.Data), "Pre-cancellation created learning data");
    }
    private static async Task Lifecycle()
    {
        using var box = new Sandbox(); var book = await Seed(box); var store = new LearningStore(box.Data);
        var child = await Child("--learning-read-child", box.Data);
        Check(child.ExitCode == 0 && child.Output.Contains("LEARNING_READ_OK"), "Learning restart failed: "+child.Output);
        var moved = Path.Combine(box.Root,"moved"); Directory.Move(box.Data,moved); store = new LearningStore(moved);
        var loaded = await store.LoadAsync(); Check(loaded.Cards.SequenceEqual(book.Cards), "Portable move changed learning data");
        loaded = await store.ChangeAsync(loaded,b => b.Edit(b.Cards[0], Entry("new prompt"),Now.AddMinutes(1)));
        Check(loaded.Cards[0].Content.Prompt == "new prompt", "Learning edit lost");
        loaded = await store.ChangeAsync(loaded,b => b.Delete(b.Cards[0]));
        Check((await store.LoadAsync()).Cards.Count == 0, "Delete was not persisted");
    }
    private static async Task Conflict()
    {
        using var box = new Sandbox(); var old = await Seed(box); var a = new LearningStore(box.Data); var b = new LearningStore(box.Data);
        var newer = await a.ChangeAsync(old,x => x.Add(Entry("two"),Now)); var hash=Hash(PathOf(box));
        await Error(() => b.ChangeAsync(old,x => x.Delete(x.Cards[0])),LearningStorageError.Conflict);
        Check(Hash(PathOf(box)) == hash,"Stale writer changed bytes");
        var restored = await a.RestoreAsync(newer, old);
        Check(restored.Generation != old.Generation && restored.Cards.Count == 1,"Restore reused a generation");
        await Error(() => b.ChangeAsync(old,x => x.SetDailyLimit(20)),LearningStorageError.Conflict);
    }
    private static async Task Failure()
    {
        foreach (var cancel in new[] { false,true })
        {
            using var box = new Sandbox(); var old=await Seed(box); var hash=Hash(PathOf(box));
            using var cts = new CancellationTokenSource();
            var store = new LearningStore(box.Data,_ => { if(cancel) cts.Cancel(); else throw new IOException("private_marker"); });
            if(cancel) await Throws<OperationCanceledException>(() => store.ChangeAsync(old,b => b.Add(Entry("new"),Now),cts.Token));
            else await Error(() => store.ChangeAsync(old,b => b.Add(Entry("new"),Now)),LearningStorageError.IoFailure);
            Check(Hash(PathOf(box)) == hash && !Directory.GetFiles(Path.GetDirectoryName(PathOf(box))!,"*.tmp").Any(),"Failure changed old bytes or leaked temp");
        }
    }
    private static async Task ReadOnly()
    {
        using var box = new Sandbox(); var old=await Seed(box); var hash=Hash(PathOf(box)); var store=new LearningStore(box.Data);
        File.SetAttributes(PathOf(box),FileAttributes.ReadOnly);
        Check((await store.LoadAsync()).Cards.Count == 1,"Read-only book could not be read");
        await Error(() => store.ChangeAsync(old,b=>b.Add(Entry("new"),Now)),LearningStorageError.AccessDenied,LearningStorageError.IoFailure);
        Check(Hash(PathOf(box)) == hash,"Read-only failure changed data"); File.SetAttributes(PathOf(box),FileAttributes.Normal);
        using var pin=new FileStream(PathOf(box),FileMode.Open,FileAccess.Read,FileShare.Read);
        await Error(() => store.ChangeAsync(old,b=>b.Delete(b.Cards[0])),LearningStorageError.IoFailure);
        Check(Hash(PathOf(box)) == hash,"Pinned output was replaced");
    }
    private static async Task Corruption()
    {
        foreach(var data in new[] { "private_marker", "{\"version\":99}", "null" })
        {
            using var box=new Sandbox(); var old=await Seed(box); var store=new LearningStore(box.Data);
            await File.WriteAllTextAsync(PathOf(box),data); var hash=Hash(PathOf(box));
            var e=await Throws<LearningException>(()=>store.LoadAsync());
            Check(e.Code==LearningError.InvalidData && !e.ToString().Contains("private_marker"),"Corruption leaked or became empty");
            await Throws<LearningException>(()=>store.ChangeAsync(old,b=>b.Add(Entry("new"),Now)));
            Check(Hash(PathOf(box))==hash,"Corruption was silently reset");
        }
    }
    private static async Task Backup()
    {
        using var box=new Sandbox(); var book=await Seed(box); var store=new LearningStore(box.Data);
        book=await store.ChangeAsync(book,b=>b.Rate(b.Cards[0],RecallRating.Easy,Now));
        var destination=Path.Combine(box.Root,"backup.json"); await store.ExportAsync(book,destination);
        var captured=await store.ReadBackupAsync(destination);
        await File.WriteAllTextAsync(destination,"changed private_marker");
        var changed=await store.ChangeAsync(book,b=>b.Delete(b.Cards[0]));
        var restored=await store.RestoreAsync(changed,captured);
        Check(restored.Cards.SequenceEqual(book.Cards) && restored.ReviewsOnDay==1 && restored.Generation!=book.Generation,
            "Restore lost recall history or reread changed file");
        await Throws<LearningException>(()=>store.ReadBackupAsync(destination));
    }
    private static async Task ExportFailure()
    {
        foreach(var cancel in new[]{false,true})
        {
            using var box=new Sandbox(); var book=await Seed(box); var path=Path.Combine(box.Root,"backup.json");
            await File.WriteAllTextAsync(path,"old backup"); var hash=Hash(path); using var cts=new CancellationTokenSource();
            var store=new LearningStore(box.Data,_=>{if(cancel)cts.Cancel();else throw new IOException("private_marker");});
            if(cancel) await Throws<OperationCanceledException>(()=>store.ExportAsync(book,path,true,cts.Token));
            else await Error(()=>store.ExportAsync(book,path,true),LearningStorageError.IoFailure);
            Check(Hash(path)==hash && !Directory.GetFiles(box.Root,"*.tmp").Any(),"Export damaged existing backup");
            await Error(()=>new LearningStore(box.Data).ExportAsync(book,path),LearningStorageError.AlreadyExists);
        }
    }
    private static async Task ManagedPaths()
    {
        using var box=new Sandbox(); var book=await Seed(box); var store=new LearningStore(box.Data); var hash=Hash(PathOf(box));
        foreach(var path in new[]{ PathOf(box),Path.Combine(box.Data,"settings.json"),Path.Combine(box.Data,"lexicons","catalog.json") })
            await Error(()=>store.ExportAsync(book,path,true),LearningStorageError.UnsafePath);
        Check(Hash(PathOf(box))==hash,"Export traversed managed data");
        using var junction=new Sandbox(); Directory.CreateDirectory(junction.Data);
        var outside=Path.Combine(junction.Root,"outside"); Directory.CreateDirectory(outside);
        var link=Path.Combine(junction.Data,"learning");
        using var process=Process.Start(new ProcessStartInfo("cmd.exe",$"/d /c mklink /J \"{link}\" \"{outside}\"")
            {UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true})!;
        var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { if(!process.HasExited) process.Kill(); throw; }
        Check(process.ExitCode==0,"Learning junction fixture failed: "+await stdout+await stderr);
        await Error(()=>new LearningStore(junction.Data).LoadAsync(),LearningStorageError.UnsafePath);
        await Error(()=>new LearningStore(junction.Data).ChangeAsync(LearningBook.Empty,b=>b.Add(Entry(),Now)),LearningStorageError.UnsafePath);
        Check(Directory.GetFileSystemEntries(outside).Length==0,"Learning storage followed a junction");
    }
    private static async Task Busy()
    {
        using var box=new Sandbox(); var book=await Seed(box); var store=new LearningStore(box.Data);
        using var held=new FileStream(Path.Combine(box.Data,"learning","book.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        await Error(()=>store.ChangeAsync(book,b=>b.SetDailyLimit(20)),LearningStorageError.Busy);
        Check((await store.LoadAsync()).Generation==book.Generation,"A writer lock blocked read-only loading");
    }
    private static async Task Cleanup()
    {
        using var box=new Sandbox(); var book=await Seed(box); var hash=Hash(PathOf(box)); FileStream? held=null;
        var store=new LearningStore(box.Data,_=>
        {
            held=new FileStream(Directory.GetFiles(Path.Combine(box.Data,"learning"),"*.tmp").Single(),FileMode.Open,FileAccess.Read,FileShare.Read);
            throw new IOException("private_marker");
        });
        try
        {
            var e=await Error(()=>store.ChangeAsync(book,b=>b.Add(Entry("new"),Now)),LearningStorageError.IoFailure);
            Check(e.CleanupPending && Hash(PathOf(box))==hash,"Failed cleanup was hidden or old book lost");
        }
        finally { held?.Dispose(); }
    }
    private static async Task Interrupted()
    {
        using var box=new Sandbox(); await Seed(box); var hash=Hash(PathOf(box));
        var child=await Child("--learning-crash-child",box.Data);
        Check(child.ExitCode==19 && Hash(PathOf(box))==hash && (await new LearningStore(box.Data).LoadAsync()).Cards.Count==1,
            "Interrupted writer damaged old book: "+child.Output);
    }
    private static async Task Sample()
    {
        using var box=new Sandbox(); var store=new LearningStore(box.Data); var book=LearningBook.Empty;
        for(var i=0;i<1000;i++) book=book.Add(Entry("fixed-"+i),Now);
        book=await store.ChangeAsync(LearningBook.Empty,_=>book);
        var watch=Stopwatch.StartNew(); var loaded=await store.LoadAsync(); watch.Stop();
        Check(loaded.Cards.Count==1000,"Fixed sample lost cards");
        Console.WriteLine($"INFO learning sample: cards=1000, bytes={new FileInfo(PathOf(box)).Length}, loadMs={watch.Elapsed.TotalMilliseconds:0.0}");
    }
}
