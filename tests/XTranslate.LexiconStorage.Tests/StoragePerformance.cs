using System.Diagnostics;
using System.Text;
using System.Text.Json;
using XTranslate.App.Lexicons;
using static TestSupport;

internal static class StoragePerformance
{
    internal static async Task<int> Run()
    {
        foreach (var count in new[] { 1_000, 100_000, 1_000_000 })
        {
            using var box = new Sandbox();
            var input = Path.Combine(box.Root, "fixed.csv");
            using (var writer = new StreamWriter(input, false, new UTF8Encoding(false), 64 * 1024))
            {
                await writer.WriteAsync(Header);
                for (var i = 0; i < count; i++)
                    await writer.WriteLineAsync($"word{i:D7},Fixed definition,固定释义,,n,,");
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var managedBefore = GC.GetTotalMemory(true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var privateBefore = process.PrivateMemorySize64;
            var peakPrivate = privateBefore;
            var peakManaged = managedBefore;
            using var stop = new CancellationTokenSource();
            var monitor = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    process.Refresh();
                    peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                    peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                    try { await Task.Delay(10, stop.Token); } catch (OperationCanceledException) { break; }
                }
            });
            var watch = Stopwatch.StartNew();
            var store = box.Store;
            using var preview = await store.PrepareDictionaryAsync(input, DictionaryInfo, Columns, sampleSize: 3);
            var prepareMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var committed = await store.CommitAsync(preview);
            var commitMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var listed = await box.Store.ListAsync();
            var catalogMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            using (var reader = await box.Store.OpenDictionaryAsync(committed.Resource.Revision.ResourceId))
            {
                var openMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                for (var i = 0; i < 100; i++)
                    Check((await reader.LookupAsync($"word{(i * (count / 100)):D7}")).TotalCount == 1, "Fixed workload query failed");
                Console.WriteLine("PERF " + JsonSerializer.Serialize(new
                {
                    entries = count, sourceBytes = new FileInfo(input).Length, indexBytes = committed.Resource.ByteLength,
                    previewEntries = preview.DictionarySample.Count, prepareMs, commitMs, catalogMs, openVerifyMs = openMs,
                    query100Ms = watch.Elapsed.TotalMilliseconds
                }));
            }
            stop.Cancel(); await monitor;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            process.Refresh();
            var retainedManaged = GC.GetTotalMemory(true) - managedBefore;
            Console.WriteLine("MEMORY " + JsonSerializer.Serialize(new
            {
                entries = count, privateBefore, peakPrivate, peakPrivateDelta = peakPrivate - privateBefore,
                peakManagedDelta = peakManaged - managedBefore, retainedManagedDelta = retainedManaged,
                closedPrivateDelta = process.PrivateMemorySize64 - privateBefore
            }));
            Check(retainedManaged < 16 * 1024 * 1024 && listed.Single().EntryCount == count && preview.DictionarySample.Count == 3 && box.NoStages(),
                "Bounded store retained whole-dictionary state or left staging");
            var cold = await Child("--perf-read-child", box.Data, committed.Resource.Revision.ResourceId);
            Check(cold.ExitCode == 0, "Cold reader failed: " + cold.Output);
            Console.WriteLine(cold.Output.Trim());
        }
        return 0;
    }

    internal static async Task<int> ColdRead(string data, string id)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var before = process.PrivateMemorySize64;
        var watch = Stopwatch.StartNew();
        var store = new LexiconStore(data);
        var catalog = await store.ListAsync();
        var catalogMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        using var reader = await store.OpenDictionaryAsync(id);
        var openMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        Check((await reader.LookupAsync("word0000000")).TotalCount == 1, "Cold lookup failed");
        process.Refresh();
        Console.WriteLine("COLD " + JsonSerializer.Serialize(new { entries = catalog.Single().EntryCount, catalogMs, openVerifyMs = openMs,
            firstQueryMs = watch.Elapsed.TotalMilliseconds, privateDelta = process.PrivateMemorySize64 - before }));
        return 0;
    }
}
