using System.Diagnostics;
using System.Text.Json.Nodes;
using XTranslate.App.Lexicons;
using XTranslate.Core.Lexicons;
using static TestSupport;

internal static class StorageFailureTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("I/O failures at every staging/publication checkpoint leave old catalog and data unchanged", IoFailures),
        ("Cancellation during capture/indexing/flush/publication preserves the old generation", Cancellation),
        ("Changed preview data is refused before publication", ChangedStage),
        ("Bad rows beyond the preview and oversized source files never become committed resources", InvalidSource),
        ("Read-only catalog replacement fails without losing old data", ReadOnlyWrite),
        ("Failed/cancelled delete preserves the existing resource", DeleteFailures),
        ("Failed/cancelled/read-only file export preserves the previous output", ExportFailures),
        ("Hash, length, missing-file and native index corruption fail explicitly without resetting storage", CorruptResources),
        ("Corrupt, duplicate, future-version and path-bearing catalog metadata are refused", CorruptCatalog),
        ("Missing catalog with existing resources is not treated as an empty store", MissingCatalog),
        ("Busy gates serialize independent store instances and separate processes", Busy),
        ("Unsafe IDs, managed export destinations and directory junctions cannot escape storage", UnsafePaths),
        ("Failed staging cleanup is reported and can be collected explicitly later", CleanupFailure),
        ("Process interruption before catalog publication leaves the old library readable and orphans collectable", CrashBeforePublication),
        ("Process interruption after publication preserves the new library and defers old-file cleanup", CrashAfterPublication),
        ("Reopen/cancelled queries and disposed readers do not retain hidden file handles", ReaderLifetime)
    };

    private static async Task AssertOld(Sandbox box, LexiconResource old, string catalogHash, string blobHash)
    {
        Check(Hash(box.CatalogPath) == catalogHash && Hash(box.Blob(old)) == blobHash && box.NoStages(), "Failure changed old bytes or leaked staging");
        using var reader = await box.Store.OpenDictionaryAsync(old.Revision.ResourceId);
        Check((await reader.LookupAsync("thread")).Entries.Single().Entry.Definition == "old", "Old dictionary became unusable");
    }

    private static async Task IoFailures()
    {
        foreach (var checkpoint in new[] { StorageCheckpoint.SourceCaptured, StorageCheckpoint.DictionaryRowWritten, StorageCheckpoint.ContentFlushed,
            StorageCheckpoint.BlobMoved, StorageCheckpoint.CatalogFlushed })
        {
            using var box = new Sandbox();
            var old = await box.Dictionary();
            var catalog = Hash(box.CatalogPath); var blob = Hash(box.Blob(old));
            var source = await box.Csv("thread,new,,,,,\n");
            var hit = false;
            var store = new LexiconStore(box.Data, point => { if (point == checkpoint) { hit = true; throw new IOException("private_marker injected disk failure"); } });
            await Error(async () =>
            {
                using var preview = await store.PrepareDictionaryAsync(source, TestSupport.DictionaryInfo, Columns, replace: old);
                await store.CommitAsync(preview);
            }, LexiconStorageError.IoFailure);
            Check(hit, "Failure checkpoint was not reached");
            await AssertOld(box, old, catalog, blob);
            Check(Directory.GetFiles(Path.GetDirectoryName(box.Blob(old))!).Length == 1, "Uncommitted generation leaked after I/O failure");
        }
    }

    private static async Task Cancellation()
    {
        foreach (var checkpoint in new[] { StorageCheckpoint.SourceCaptured, StorageCheckpoint.DictionaryRowWritten, StorageCheckpoint.ContentFlushed,
            StorageCheckpoint.BlobMoved, StorageCheckpoint.CatalogFlushed })
        {
            using var box = new Sandbox();
            var old = await box.Dictionary();
            var catalog = Hash(box.CatalogPath); var blob = Hash(box.Blob(old));
            using var cancel = new CancellationTokenSource();
            var hit = false;
            var store = new LexiconStore(box.Data, point => { if (point == checkpoint) { hit = true; cancel.Cancel(); } });
            var source = await box.Csv("thread,new,,,,,\n");
            await Throws<OperationCanceledException>(async () =>
            {
                using var preview = await store.PrepareDictionaryAsync(source, TestSupport.DictionaryInfo, Columns, replace: old, cancellationToken: cancel.Token);
                await store.CommitAsync(preview, cancel.Token);
            });
            Check(hit, "Cancellation checkpoint was not reached");
            await AssertOld(box, old, catalog, blob);
        }
        using var during = new Sandbox();
        var current = await during.Dictionary();
        var hash = Hash(during.CatalogPath);
        var large = await during.Csv(string.Concat(Enumerable.Repeat("thread,same,,,,,\n", 10000)));
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.CapturedBytes > 0) cts.Cancel(); });
        await Throws<OperationCanceledException>(() => during.Store.PrepareDictionaryAsync(large, TestSupport.DictionaryInfo, Columns,
            replace: current, progress: progress, cancellationToken: cts.Token));
        Check(Hash(during.CatalogPath) == hash && during.NoStages(), "In-flight copy cancellation changed storage");
    }

    private static async Task ChangedStage()
    {
        using var box = new Sandbox();
        var old = await box.Dictionary();
        var hash = Hash(box.CatalogPath); var blob = Hash(box.Blob(old));
        var store = box.Store;
        using var preview = await store.PrepareDictionaryAsync(await box.Csv("thread,preview,,,,,\n"), TestSupport.DictionaryInfo, Columns, replace: old);
        using (var file = new FileStream(preview.ContentPath, FileMode.Open, FileAccess.Write)) file.WriteByte(0);
        await Error(() => store.CommitAsync(preview), LexiconStorageError.CorruptResource);
        await AssertOld(box, old, hash, blob);
    }

    private static async Task InvalidSource()
    {
        using var box = new Sandbox();
        var old = await box.Dictionary();
        var catalog = Hash(box.CatalogPath); var blob = Hash(box.Blob(old));
        var source = await box.Csv("thread,good,,,,,\nmissing,\n");
        await Throws<LexiconImportException>(() => box.Store.PrepareDictionaryAsync(source, TestSupport.DictionaryInfo, Columns, replace: old, sampleSize: 1));
        await AssertOld(box, old, catalog, blob);
        var oversized = Path.Combine(box.Root, "oversized.csv");
        using (var file = File.Create(oversized)) file.SetLength(256L * 1024 * 1024 + 1);
        var error = await Throws<LexiconImportException>(() => box.Store.PrepareDictionaryAsync(oversized, TestSupport.DictionaryInfo, Columns, replace: old));
        Check(error.Code == LexiconErrorCode.InputTooLarge, "Source size cap was not enforced");
        await AssertOld(box, old, catalog, blob);
    }

    private static async Task ReadOnlyWrite()
    {
        using var box = new Sandbox();
        var old = await box.Dictionary();
        var catalog = Hash(box.CatalogPath); var blob = Hash(box.Blob(old));
        var store = box.Store;
        using var preview = await store.PrepareDictionaryAsync(await box.Csv("thread,new,,,,,\n"), TestSupport.DictionaryInfo, Columns, replace: old);
        File.SetAttributes(box.CatalogPath, FileAttributes.ReadOnly);
        await Error(() => store.CommitAsync(preview), LexiconStorageError.AccessDenied, LexiconStorageError.IoFailure);
        await AssertOld(box, old, catalog, blob);
        File.SetAttributes(box.CatalogPath, FileAttributes.Normal);
    }

    private static async Task DeleteFailures()
    {
        using var box = new Sandbox();
        var old = await box.Dictionary();
        var catalog = Hash(box.CatalogPath); var blob = Hash(box.Blob(old));
        var failed = new LexiconStore(box.Data, point => { if (point == StorageCheckpoint.CatalogFlushed) throw new IOException("private_marker"); });
        await Error(() => failed.DeleteAsync(old), LexiconStorageError.IoFailure);
        await AssertOld(box, old, catalog, blob);
        using var cancel = new CancellationTokenSource();
        var cancelled = new LexiconStore(box.Data, point => { if (point == StorageCheckpoint.CatalogFlushed) cancel.Cancel(); });
        await Throws<OperationCanceledException>(() => cancelled.DeleteAsync(old, cancel.Token));
        await AssertOld(box, old, catalog, blob);
    }

    private static async Task ExportFailures()
    {
        using var box = new Sandbox();
        var resource = await box.Glossary();
        var path = Path.Combine(box.Root, "existing.json");
        await File.WriteAllTextAsync(path, "old bytes");
        var before = Hash(path);
        var failed = new LexiconStore(box.Data, point => { if (point == StorageCheckpoint.ExportFlushed) throw new IOException("private_marker"); });
        await Error(() => failed.ExportGlossaryAsync(resource.Revision.ResourceId, path, overwrite: true), LexiconStorageError.IoFailure);
        Check(Hash(path) == before, "Failed export changed destination");
        using var cancel = new CancellationTokenSource();
        var cancelled = new LexiconStore(box.Data, point => { if (point == StorageCheckpoint.ExportFlushed) cancel.Cancel(); });
        await Throws<OperationCanceledException>(() => cancelled.ExportGlossaryAsync(resource.Revision.ResourceId, path, overwrite: true, cancellationToken: cancel.Token));
        Check(Hash(path) == before, "Cancelled export changed destination");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        await Error(() => box.Store.ExportGlossaryAsync(resource.Revision.ResourceId, path, overwrite: true), LexiconStorageError.AccessDenied, LexiconStorageError.IoFailure);
        Check(Hash(path) == before && !Directory.EnumerateFiles(box.Root, ".lexicon-export-*").Any(), "Read-only export lost data or left staging");
    }

    private static async Task CorruptResources()
    {
        using var box = new Sandbox();
        var resource = await box.Dictionary();
        var path = box.Blob(resource);
        var original = await File.ReadAllBytesAsync(path);
        var catalog = await File.ReadAllTextAsync(box.CatalogPath);
        var corrupt = original.ToArray(); corrupt[0] ^= 0xff;
        await File.WriteAllBytesAsync(path, corrupt);
        await Error(() => box.Store.OpenDictionaryAsync(resource.Revision.ResourceId), LexiconStorageError.CorruptResource);
        Check(await File.ReadAllTextAsync(box.CatalogPath) == catalog, "Corruption reset the catalog");
        await File.WriteAllBytesAsync(path, original[..^1]);
        await Error(() => box.Store.OpenDictionaryAsync(resource.Revision.ResourceId), LexiconStorageError.CorruptResource);
        File.Delete(path);
        await Error(() => box.Store.OpenDictionaryAsync(resource.Revision.ResourceId), LexiconStorageError.CorruptResource);
        await File.WriteAllBytesAsync(path, original);
        // Recompute the catalog checksum to reach the independent native/schema validation rather than just SHA rejection.
        using (var connection = DictionaryDatabase.Connect(path, readOnly: false))
        {
            using var command = connection.CreateCommand(); command.CommandText = "DROP INDEX exact_headword"; command.ExecuteNonQuery();
        }
        var node = Catalog(box);
        node["resources"]![0]!["contentSha256"] = Hash(path);
        node["resources"]![0]!["byteLength"] = new FileInfo(path).Length;
        await WriteCatalog(box, node);
        await Error(() => box.Store.OpenDictionaryAsync(resource.Revision.ResourceId), LexiconStorageError.CorruptResource);
        await File.WriteAllBytesAsync(path, original); await File.WriteAllTextAsync(box.CatalogPath, catalog);
        using var reopened = await box.Store.OpenDictionaryAsync(resource.Revision.ResourceId);
        Check((await reopened.LookupAsync("thread")).TotalCount == 1, "Rejected dictionary leaked an open native handle");
    }

    private static async Task CorruptCatalog()
    {
        using var box = new Sandbox();
        await box.Dictionary();
        var original = await File.ReadAllTextAsync(box.CatalogPath);
        var corrupt = new List<string> { "{broken private_marker", "null", original.Replace("\"version\": 1", "\"version\": 1, \"version\": 1") };
        foreach (var change in new Action<JsonNode>[]
        {
            node => node["version"] = 99,
            node => node["resources"]![0]!["storageKey"] = "../../private_marker",
            node => node["resources"]![0]!["revision"]!["resourceId"] = "../private_marker",
            node => node["resources"]![0]!["kind"] = 999,
            node => node["resources"]![0]!["entryCount"] = int.MaxValue,
            node => node["resources"]![0]!["revision"]!.AsObject().Remove("sourceSha256"),
            node => node["resources"]![0]!["provenance"]!.AsObject().Remove("license"),
            node => node["resources"]!.AsArray().Add(node["resources"]![0]!.DeepClone())
        })
        { var node = JsonNode.Parse(original)!; change(node); corrupt.Add(node.ToJsonString()); }
        foreach (var text in corrupt)
        {
            await File.WriteAllTextAsync(box.CatalogPath, text);
            await Error(() => box.Store.ListAsync(), LexiconStorageError.CorruptCatalog);
            await Error(() => box.Store.PrepareGlossaryAsync(Glossary()), LexiconStorageError.CorruptCatalog);
            Check(await File.ReadAllTextAsync(box.CatalogPath) == text, "Corrupt catalog was silently replaced");
        }
        await File.WriteAllTextAsync(box.CatalogPath, original);
    }

    private static async Task MissingCatalog()
    {
        using var box = new Sandbox();
        var resource = await box.Dictionary();
        File.Delete(box.CatalogPath);
        await Error(() => box.Store.ListAsync(), LexiconStorageError.CorruptCatalog);
        await Error(() => box.Store.PrepareGlossaryAsync(Glossary()), LexiconStorageError.CorruptCatalog);
        Check(!File.Exists(box.CatalogPath) && File.Exists(box.Blob(resource)), "Missing manifest reset/deleted old data");
    }

    private static async Task Busy()
    {
        using var box = new Sandbox();
        await box.Glossary();
        var before = Hash(box.CatalogPath);
        using var gate = new FileStream(Path.Combine(box.Data, "lexicons", "catalog.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Error(() => box.Store.ListAsync(), LexiconStorageError.Busy);
        await Error(() => box.Store.PrepareGlossaryAsync(Glossary()), LexiconStorageError.Busy);
        var child = await Child("--busy-child", box.Data);
        Check(child.ExitCode == 0 && child.Output.Contains("BUSY_OK") && Hash(box.CatalogPath) == before, "Cross-process gate failed: " + child.Output);
    }

    private static async Task UnsafePaths()
    {
        using var box = new Sandbox();
        var resource = await box.Glossary();
        await Error(() => box.Store.OpenDictionaryAsync("../private_marker"), LexiconStorageError.UnsafePath);
        await Error(() => box.Store.ExportGlossaryAsync(resource.Revision.ResourceId, box.CatalogPath, overwrite: true), LexiconStorageError.UnsafePath);
        foreach (var relative in new[] { "settings.json", "diagnostics.jsonl", Path.Combine("learning", "book.json") })
        {
            var managed = Path.Combine(box.Data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
            await File.WriteAllTextAsync(managed, "managed-sentinel");
            await Error(() => box.Store.ExportGlossaryAsync(resource.Revision.ResourceId, managed, overwrite: true), LexiconStorageError.UnsafePath);
            Check(await File.ReadAllTextAsync(managed) == "managed-sentinel", "Export overwrote non-lexicon application data");
        }
        using var junction = new Sandbox();
        Directory.CreateDirectory(junction.Data);
        var outside = Path.Combine(junction.Root, "outside"); Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt"); await File.WriteAllTextAsync(sentinel, "untouched");
        var link = Path.Combine(junction.Data, "lexicons");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{outside}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        Check(process.ExitCode == 0, "Junction fixture failed: " + await output + await error);
        await Error(() => junction.Store.PrepareGlossaryAsync(Glossary()), LexiconStorageError.UnsafePath);
        await Error(() => junction.Store.CollectGarbageAsync(), LexiconStorageError.UnsafePath);
        Check(await File.ReadAllTextAsync(sentinel) == "untouched" && Directory.GetFileSystemEntries(outside).Length == 1, "Storage traversed a junction");
    }

    private static async Task CleanupFailure()
    {
        using var box = new Sandbox();
        var store = box.Store;
        using var preview = await store.PrepareGlossaryAsync(Glossary());
        using (var held = new FileStream(preview.ContentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Error(() => { preview.Dispose(); return Task.CompletedTask; }, LexiconStorageError.IoFailure);
            Check(error.CleanupPending && (await store.CollectGarbageAsync()).DeferredItems > 0, "Failed cleanup was hidden");
        }
        Check((await store.CollectGarbageAsync()).DeferredItems == 0 && box.NoStages() && (await store.ListAsync()).Count == 0,
            "Deferred staging cleanup could not recover");
    }

    private static async Task CrashBeforePublication()
    {
        using var box = new Sandbox();
        var old = await box.Dictionary();
        var hash = Hash(box.CatalogPath);
        var source = await box.Csv("thread,new,,,,,\n");
        var child = await Child("--crash-before-publish-child", box.Data, source);
        Check(child.ExitCode == 17 && Hash(box.CatalogPath) == hash, "Interrupted pre-publication writer changed the catalog: " + child.Output);
        using (var reader = await box.Store.OpenDictionaryAsync(old.Revision.ResourceId))
            Check((await reader.LookupAsync("thread")).Entries.Single().Entry.Definition == "old", "Old library failed after writer exit");
        var cleanup = await box.Store.CollectGarbageAsync();
        Check(cleanup.RemovedFiles == 2 && cleanup.DeferredItems == 0 && box.NoStages() && File.Exists(box.Blob(old)), "Interrupted-import recovery deleted old data or retained orphans");
    }

    private static async Task CrashAfterPublication()
    {
        using var box = new Sandbox();
        var old = await box.Dictionary();
        var source = await box.Csv("thread,new,,,,,\n");
        var child = await Child("--crash-after-publish-child", box.Data, source);
        Check(child.ExitCode == 18, "Post-publication interruption fixture failed: " + child.Output);
        using (var reader = await box.Store.OpenDictionaryAsync(old.Revision.ResourceId))
            Check((await reader.LookupAsync("thread")).Entries.Single().Entry.Definition == "new", "Committed generation was lost after writer exit");
        var cleanup = await box.Store.CollectGarbageAsync();
        Check(cleanup.RemovedFiles == 1 && cleanup.DeferredItems == 0 && !File.Exists(box.Blob(old)), "Deferred old generation was not collected after process exit");
    }

    private static async Task ReaderLifetime()
    {
        using var box = new Sandbox();
        var resource = await box.Dictionary();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => box.Store.OpenDictionaryAsync(resource.Revision.ResourceId, cancelled.Token));
        var reader = await box.Store.OpenDictionaryAsync(resource.Revision.ResourceId);
        await Throws<OperationCanceledException>(() => reader.LookupAsync("thread", cancellationToken: cancelled.Token));
        reader.Dispose(); reader.Dispose();
        await Throws<ObjectDisposedException>(() => reader.LookupAsync("thread"));
        Check(!(await box.Store.DeleteAsync(resource)).CleanupPending, "Reader cancellation/disposal retained a hidden connection");
    }

    private sealed class InlineProgress(Action<LexiconImportProgress> action) : IProgress<LexiconImportProgress>
    { public void Report(LexiconImportProgress value) => action(value); }
}
