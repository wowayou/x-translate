using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using XTranslate.App.Lexicons;
using static TestSupport;

internal static class StoragePerformance
{
    // Local fixed-fixture acceptance budgets, not a promise about arbitrary third-party dictionaries/devices.
    // Import runs in a fresh child (30 s process deadline); reader validation intentionally scans the index.
    internal static async Task<int> Run()
    {
        Console.WriteLine("ENV " + JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(),
            os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            samples = 3, monitorIntervalMs = 10, processDeadlineSeconds = 30 }));
        for (var iteration = 1; iteration <= 3; iteration++)
            foreach (var count in new[] { 1_000, 100_000, 1_000_000 })
            {
                Console.WriteLine($"CASE iteration={iteration} entries={count}");
                var result = await Child("--perf-case-child", count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Console.WriteLine(result.Output.Trim());
                Check(result.ExitCode == 0, "Isolated performance case failed");
            }
        Console.WriteLine("9/9 isolated storage performance cases passed");
        return 0;
    }

    internal static async Task<int> RunCase(int count)
    {
        Check(count is 1_000 or 100_000 or 1_000_000, "Unknown fixed fixture size");
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
        using var process = Process.GetCurrentProcess(); process.Refresh();
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
        LexiconResource resource;
        try
        {
            var watch = Stopwatch.StartNew();
            var store = box.Store;
            using var preview = await store.PrepareDictionaryAsync(input, DictionaryInfo, Columns, sampleSize: 3);
            var prepareMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var committed = await store.CommitAsync(preview);
            resource = committed.Resource;
            var commitMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var listed = await box.Store.ListAsync();
            var catalogMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            using (var reader = await box.Store.OpenDictionaryAsync(resource.Revision.ResourceId))
            {
                var openMs = watch.Elapsed.TotalMilliseconds;
                var times = new List<double>();
                for (var i = 0; i < 100; i++)
                {
                    // Half exact hits, half misses. Every lookup is checked, not just timed.
                    var hit = i % 2 == 0;
                    watch.Restart();
                    var page = await reader.LookupAsync(hit ? $"word{(i * (count / 100)):D7}" : $"missing{i:D7}");
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    Check(page.TotalCount == (hit ? 1 : 0) && page.Entries.Count == page.TotalCount, "Fixed workload query failed");
                }
                times.Sort();
                var p95 = times[94];
                Console.WriteLine("PERF " + JsonSerializer.Serialize(new
                {
                    entries = count, sourceBytes = new FileInfo(input).Length, indexBytes = resource.ByteLength,
                    previewEntries = preview.DictionarySample.Count, prepareMs, commitMs, catalogMs, openVerifyMs = openMs,
                    queryCount = times.Count, queryP50Ms = (times[49] + times[50]) / 2, queryP95Ms = p95, queryMaxMs = times[^1]
                }));
                Check(prepareMs + commitMs < 15_000 && catalogMs < 500 && openMs < 2_000 && p95 < 50,
                    "Fixed-fixture latency budget exceeded");
            }
            Check(listed.Single().EntryCount == count && preview.DictionarySample.Count == 3 && !committed.CleanupPending && box.NoStages(),
                "Committed fixture count/preview/cleanup drifted");
        }
        finally { stop.Cancel(); await monitor; }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
        var retainedManaged = GC.GetTotalMemory(true) - managedBefore;
        Console.WriteLine("MEMORY " + JsonSerializer.Serialize(new
        {
            entries = count, privateBefore, peakPrivate, peakPrivateDelta = peakPrivate - privateBefore,
            peakManagedDelta = peakManaged - managedBefore, retainedManagedDelta = retainedManaged,
            closedPrivateDelta = process.PrivateMemorySize64 - privateBefore
        }));
        Check(retainedManaged < 16 * 1024 * 1024 && peakManaged - managedBefore < 64 * 1024 * 1024 && peakPrivate - privateBefore < 96 * 1024 * 1024,
            "Fixed-fixture memory budget exceeded");
        // Fresh process/runtime, but OS file cache is deliberately not evicted. Not a cold physical-disk claim.
        var cold = await Child("--perf-read-child", box.Data, resource.Revision.ResourceId);
        Console.WriteLine(cold.Output.Trim());
        Check(cold.ExitCode == 0, "Fresh-process reader failed");
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
        Check((await reader.LookupAsync("word0000000")).TotalCount == 1, "Fresh-process lookup failed");
        var firstQueryMs = watch.Elapsed.TotalMilliseconds;
        process.Refresh();
        var privateDelta = process.PrivateMemorySize64 - before;
        Console.WriteLine("FRESH_PROCESS " + JsonSerializer.Serialize(new { entries = catalog.Single().EntryCount, catalogMs,
            openVerifyMs = openMs, firstQueryMs, privateDelta }));
        Check(catalogMs < 500 && openMs < 2_000 && firstQueryMs < 50 && privateDelta < 32 * 1024 * 1024,
            "Fresh-process reader budget exceeded");
        return 0;
    }
}
