using System.Text;
using XTranslate.App.Lexicons;
using XTranslate.Core.Lexicons;
using static TestSupport;

internal static class StorageTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Constructing/listing an unused store and pre-cancelled import create no files", NoStartupIo),
        ("Preview remains uncommitted and uses captured bytes even if the original changes", CapturedSnapshot),
        ("Exact on-disk lookup preserves all fields, duplicate meanings, Unicode and literal identifiers", Lookup),
        ("Dictionary result pages bound retained text and expose continuation", PageBudget),
        ("Dictionary is readable after a separate process restart and moving the portable data folder", RestartAndMove),
        ("CSV/TSV and manual glossary saves retain languages, matching and attribution", Glossaries),
        ("v2 restore preserves metadata and identity while v1 gains an explicit local revision", Restore),
        ("Concurrent stale previews cannot overwrite a newer version or a delete/restore cycle", StaleRevision),
        ("Readers pin their immutable generation while replacement becomes visible to new readers", PinnedReplacement),
        ("Delete publishes atomically and defers removal while an old reader is active", PinnedDelete),
        ("A preview belongs to one store instance and is single-use", StageOwnership),
        ("Read-only catalogs and lock files remain queryable without writes", ReadOnlyReads),
        ("Manual garbage collection preserves referenced files, live previews and unknown files", GarbageCollection),
        ("All glossary exports use the stored snapshot without silently changing formula data", Exports),
        ("Catalog resource and revision ceilings reject overflow without losing old resources", Limits)
    };

    private static async Task NoStartupIo()
    {
        using var box = new Sandbox();
        var store = box.Store;
        Check((await store.ListAsync()).Count == 0 && !Directory.Exists(box.Data), "Idle store touched disk");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => store.PrepareGlossaryAsync(Glossary(), cancellationToken: cancelled.Token));
        Check(!Directory.Exists(box.Data), "Pre-cancelled operation created data");
    }

    private static async Task CapturedSnapshot()
    {
        using var box = new Sandbox();
        var existing = await box.Dictionary();
        var before = Hash(box.CatalogPath);
        var store = box.Store;
        var source = await box.Csv("thread,preview,线程,θ,n,source example,plural\n");
        var sourceHash = Hash(source);
        using var preview = await store.PrepareDictionaryAsync(source, TestSupport.DictionaryInfo, Columns, Provenance, existing, "updated");
        Check(preview.DictionarySample.Single().Entry.Definition == "preview" && Hash(box.CatalogPath) == before,
            "Preview wrote the catalog or did not reflect input");
        Check(Hash(source) == sourceHash && (await store.ListAsync()).Single() == existing, "Import changed the source or exposed preview");
        await File.WriteAllTextAsync(source, "broken original after preview");
        var committed = await store.CommitAsync(preview);
        Check(committed.Resource.Revision.Number == existing.Revision.Number + 1 && committed.Resource.Provenance == Provenance &&
            committed.Resource.CapturedInputSha256 == sourceHash && committed.Resource.Revision.SourceSha256 == sourceHash, "Capture/revision attribution was lost");
        using var reader = await store.OpenDictionaryAsync(existing.Revision.ResourceId);
        Check((await reader.LookupAsync("thread")).Entries.Single().Entry.Definition == "preview", "Commit re-read the changed original");
        Check(box.NoStages(), "Successful commit retained raw input");
    }

    private static async Task Lookup()
    {
        using var box = new Sandbox();
        var input = await box.Csv("API,upper,接口,音标,n,actual example,forms\nAPI,second,,,,,\napi,lower,,,,,\nmyAPIKey,identifier,,,,,\nC++,cpp,,,,,\n.NET,dotnet,,,,,\n线程,中文,,,,,\n𐐀,supplementary,,,,,\né,combining,,,,,\n");
        var store = box.Store;
        using var preview = await store.PrepareDictionaryAsync(input, TestSupport.DictionaryInfo, Columns, Provenance, sampleSize: 2);
        Check(preview.Resource.EntryCount == 9 && preview.DictionarySample.Count == 2, "Sample/total was wrong");
        var resource = (await store.CommitAsync(preview)).Resource;
        using var reader = await store.OpenDictionaryAsync(resource.Revision.ResourceId);
        var first = await reader.LookupAsync("API", pageSize: 1);
        var second = await reader.LookupAsync("API", first.NextOffset, 1);
        Check(first.TotalCount == 2 && first.HasMore && first.NextOffset == 1 && second.Entries.Single().Entry.Definition == "second" && !second.HasMore,
            "Repeated meanings/paging were changed");
        var entry = first.Entries.Single();
        Check(entry.RecordNumber == 2 && entry.LineNumber == 2 && entry.Entry.Translation == "接口" && entry.Entry.Phonetic == "音标" &&
            entry.Entry.PartOfSpeech == "n" && entry.Entry.Examples == "actual example" && entry.Entry.Inflections == "forms", "Source fields were not preserved");
        foreach (var key in new[] { "api", "myAPIKey", "C++", ".NET", "线程", "𐐀", "é" })
            Check((await reader.LookupAsync(key)).Entries.Single().Entry.Headword == key, "Literal lookup missed a key");
        foreach (var key in new[] { "Api", "APIKey", "C+", "é", "𐐨", "not found", "' OR 1=1 --" })
            Check((await reader.LookupAsync(key)).TotalCount == 0, "Lookup guessed/folded/normalized/executed input");
        Check((await reader.LookupAsync("API", offset: 10)).Entries.Count == 0, "Offset beyond end returned data");
        await Throws<ArgumentException>(() => reader.LookupAsync(" API"));
        await Throws<ArgumentOutOfRangeException>(() => reader.LookupAsync("API", pageSize: 101));
    }

    private static async Task PageBudget()
    {
        using var box = new Sandbox();
        var body = string.Concat(Enumerable.Range(0, 20).Select(_ => "thread," + new string('x', 65_536) + ",,,,,\n"));
        var store = box.Store;
        using var preview = await store.PrepareDictionaryAsync(await box.Csv(body), TestSupport.DictionaryInfo, Columns, sampleSize: 100);
        Check(preview.DictionarySample.Count < 20, "Preview retained more than the text budget");
        var resource = (await store.CommitAsync(preview)).Resource;
        using var reader = await store.OpenDictionaryAsync(resource.Revision.ResourceId);
        var first = await reader.LookupAsync("thread", pageSize: 100);
        var next = await reader.LookupAsync("thread", first.NextOffset, 100);
        Check(first.HasMore && !next.HasMore && first.Entries.Count + next.Entries.Count == 20 &&
            first.Entries.Sum(row => DictionaryDatabase.Fields(row.Entry).Sum(field => field.Length)) <= LocalDictionaryReader.MaxPageChars,
            "Result budget truncated or hid meanings");
    }

    private static async Task RestartAndMove()
    {
        using var box = new Sandbox();
        var resource = await box.Dictionary("restart");
        var moved = Path.Combine(box.Root, "moved-data");
        Directory.Move(box.Data, moved);
        var child = await Child("--read-child", moved, resource.Revision.ResourceId, "restart");
        Check(child.ExitCode == 0 && child.Output.Contains("RESTART_READ_OK"), "Separate-process portable restart failed: " + child.Output);
    }

    private static async Task Glossaries()
    {
        using var box = new Sandbox();
        var store = box.Store;
        var source = Path.Combine(box.Root, "terms.tsv");
        await File.WriteAllTextAsync(source, "source\ttarget\tnote\tkeep\nthread\t线程\treference\tfalse\nAPI\t\tkeep actual case\ttrue\n");
        using var preview = await store.PrepareGlossaryCsvAsync(source, new("Software", "Development", "EN", "zh-cn"),
            new(DelimitedTextFormat.Tsv, true, new(0, 1, 2, 3), true, GlossaryMatchMode.Literal), Provenance);
        var resource = (await store.CommitAsync(preview)).Resource;
        var snapshot = await box.Store.LoadGlossaryAsync(resource.Revision.ResourceId);
        Check(snapshot.Provenance == Provenance && snapshot.Glossary.Info.SourceLanguage == "en" && snapshot.Glossary.CaseSensitive &&
            snapshot.Glossary.MatchMode == GlossaryMatchMode.Literal && snapshot.Glossary.Terms[1].PreserveSource, "Glossary disk semantics drifted");
        using var edited = await store.PrepareGlossaryAsync(Glossary("新线程"), resource, "manual edit");
        var saved = (await store.CommitAsync(edited)).Resource;
        Check(saved.Revision.Number == 2 && saved.Revision.ChangeNote == "manual edit" && saved.CapturedInputSha256 == "" &&
            (await box.Store.LoadGlossaryAsync(saved.Revision.ResourceId)).Glossary.Terms[0].Target == "新线程", "Manual edit was not persisted");
        Check(Directory.GetFiles(Path.Combine(box.Data, "lexicons", "dictionaries")).Length == 0 && !File.Exists(Path.Combine(box.Data, "settings.json")),
            "Glossary was mixed into dictionaries/settings");
    }

    private static async Task Restore()
    {
        using var box = new Sandbox();
        using var other = new Sandbox();
        var resource = await box.Glossary();
        var backup = Path.Combine(box.Root, "backup.json");
        await box.Store.ExportGlossaryAsync(resource.Revision.ResourceId, backup);
        var store = other.Store;
        using var preview = await store.PrepareGlossaryJsonAsync(backup);
        var restored = (await store.CommitAsync(preview)).Resource;
        Check(restored.Revision == resource.Revision && restored.Provenance == resource.Provenance && restored.CapturedInputSha256 == Hash(backup), "v2 restore lost metadata/capture");
        using var collision = await store.PrepareGlossaryJsonAsync(backup);
        await Error(() => store.CommitAsync(collision), LexiconStorageError.AlreadyExists);
        var legacy = Path.Combine(box.Root, "v1.json");
        using (var stream = File.Create(legacy)) await GlossaryExport.WriteJsonAsync(stream, Glossary().Glossary);
        using var old = await store.PrepareGlossaryJsonAsync(legacy);
        var migrated = (await store.CommitAsync(old)).Resource;
        Check(migrated.Revision.Number == 1 && migrated.Revision.ResourceId != resource.Revision.ResourceId &&
            migrated.Provenance == LexiconProvenance.Unspecified, "v1 migration invented provenance or reused identity");
        using var replace = await store.PrepareGlossaryJsonAsync(backup, migrated, "explicit replacement");
        var replaced = (await store.CommitAsync(replace)).Resource;
        Check(replaced.Revision.ResourceId == migrated.Revision.ResourceId && replaced.Revision.Number == 2 && replaced.Provenance == resource.Provenance,
            "Explicit replacement took an imported ID or lost attribution");
    }

    private static async Task StaleRevision()
    {
        using var box = new Sandbox();
        var original = await box.Glossary();
        var store = box.Store;
        using var earlier = await store.PrepareGlossaryAsync(Glossary("stale"), original);
        using var newer = await store.PrepareGlossaryAsync(Glossary("current"), original);
        var current = (await store.CommitAsync(newer)).Resource;
        var bytes = Hash(box.CatalogPath);
        await Error(() => store.CommitAsync(earlier), LexiconStorageError.StaleRevision);
        await Error(() => store.DeleteAsync(original), LexiconStorageError.StaleRevision);
        Check(Hash(box.CatalogPath) == bytes, "Stale writer modified the catalog");
        var snapshot = await store.LoadGlossaryAsync(current.Revision.ResourceId);
        using var pending = await store.PrepareGlossaryAsync(Glossary("late"), current);
        await store.DeleteAsync(current);
        using var restored = await store.PrepareGlossaryAsync(snapshot);
        var fresh = (await store.CommitAsync(restored)).Resource;
        Check(fresh.Revision == current.Revision && fresh.StorageKey != current.StorageKey, "Restore fixture did not exercise same revision/different generation");
        await Error(() => store.CommitAsync(pending), LexiconStorageError.StaleRevision);
    }

    private static async Task PinnedReplacement()
    {
        using var box = new Sandbox();
        var original = await box.Dictionary();
        var store = box.Store;
        using (var pinned = await store.OpenDictionaryAsync(original.Revision.ResourceId))
        {
            using var preview = await store.PrepareDictionaryAsync(await box.Csv("thread,new,,,,,\n"), TestSupport.DictionaryInfo, Columns, replace: original);
            var result = await store.CommitAsync(preview);
            Check(result.CleanupPending && File.Exists(box.Blob(original)), "Active reader was deleted or deferred cleanup hidden");
            Check((await pinned.LookupAsync("thread")).Entries.Single().Entry.Definition == "old", "Pinned reader changed generation");
            using var latest = await store.OpenDictionaryAsync(original.Revision.ResourceId);
            Check((await latest.LookupAsync("thread")).Entries.Single().Entry.Definition == "new", "New reader sees old generation");
        }
        var cleanup = await store.CollectGarbageAsync();
        Check(cleanup.RemovedFiles == 1 && cleanup.DeferredItems == 0 && !File.Exists(box.Blob(original)), "Unpinned obsolete file was not collected");
    }

    private static async Task PinnedDelete()
    {
        using var box = new Sandbox();
        var resource = await box.Dictionary();
        var store = box.Store;
        using (var reader = await store.OpenDictionaryAsync(resource.Revision.ResourceId))
        {
            Check((await store.DeleteAsync(resource)).CleanupPending && (await store.ListAsync()).Count == 0, "Delete did not publish/defer honestly");
            Check((await reader.LookupAsync("thread")).TotalCount == 1, "Logical delete invalidated an existing reader");
            await Error(() => store.OpenDictionaryAsync(resource.Revision.ResourceId), LexiconStorageError.ResourceNotFound);
        }
        Check((await store.CollectGarbageAsync()).DeferredItems == 0 && !File.Exists(box.Blob(resource)), "Deleted data could not be removed");
    }

    private static async Task StageOwnership()
    {
        using var box = new Sandbox();
        var store = box.Store;
        using var preview = await store.PrepareGlossaryAsync(Glossary());
        await Error(() => box.Store.CommitAsync(preview), LexiconStorageError.InvalidStage);
        var resource = (await store.CommitAsync(preview)).Resource;
        await Error(() => store.CommitAsync(preview), LexiconStorageError.InvalidStage);
        using var cancelled = await store.PrepareGlossaryAsync(Glossary(), resource);
        cancelled.Dispose();
        await Error(() => store.CommitAsync(cancelled), LexiconStorageError.InvalidStage);
        Check(box.NoStages(), "Single-use preview leaked staging");
    }

    private static async Task ReadOnlyReads()
    {
        using var box = new Sandbox();
        var resource = await box.Dictionary();
        var lockPath = Path.Combine(box.Data, "lexicons", "catalog.lock");
        File.SetAttributes(box.CatalogPath, FileAttributes.ReadOnly);
        File.SetAttributes(lockPath, FileAttributes.ReadOnly);
        using var reader = await box.Store.OpenDictionaryAsync(resource.Revision.ResourceId);
        Check((await reader.LookupAsync("thread")).TotalCount == 1 && (await box.Store.ListAsync()).Single() == resource, "Read-only storage required writes");
        await Error(() => box.Store.PrepareGlossaryAsync(Glossary()), LexiconStorageError.AccessDenied);
    }

    private static async Task GarbageCollection()
    {
        using var box = new Sandbox();
        var resource = await box.Glossary();
        var store = box.Store;
        using var live = await store.PrepareGlossaryAsync(Glossary(), resource);
        var orphan = Path.Combine(box.Data, "lexicons", "glossaries", new string('f', 32) + ".json");
        var unknown = Path.Combine(box.Data, "lexicons", "glossaries", "user-added.txt");
        await File.WriteAllTextAsync(orphan, "orphan"); await File.WriteAllTextAsync(unknown, "keep");
        var cleanup = await store.CollectGarbageAsync();
        Check(cleanup.RemovedFiles == 1 && cleanup.DeferredItems == 2 && File.Exists(unknown) && File.Exists(box.Blob(resource)) &&
            File.Exists(live.ContentPath), "GC removed live/user content or failed to identify deferred items");
        await store.CommitAsync(live);
        Check(File.Exists(unknown), "Commit deleted an unknown user file");
    }

    private static async Task Exports()
    {
        using var box = new Sandbox();
        var store = box.Store;
        var resource = await box.Glossary();
        foreach (var format in Enum.GetValues<GlossaryFileFormat>())
        {
            var path = Path.Combine(box.Root, "export." + format);
            await store.ExportGlossaryAsync(resource.Revision.ResourceId, path, format);
            Check(new FileInfo(path).Length > 0, "Empty export");
            await Error(() => store.ExportGlossaryAsync(resource.Revision.ResourceId, path, format), LexiconStorageError.AlreadyExists);
            await store.ExportGlossaryAsync(resource.Revision.ResourceId, path, format, overwrite: true);
            if (format == GlossaryFileFormat.Json)
            {
                using var stream = File.OpenRead(path);
                var snapshot = await GlossaryJson.ReadAsync(stream);
                Check(snapshot.Revision == resource.Revision && snapshot.Provenance == resource.Provenance, "File export dropped metadata");
            }
        }
        using var formula = await store.PrepareGlossaryAsync(new GlossaryDocument(XTranslate.Core.Lexicons.Glossary.Create(
            new("Formula", "", "en", "zh-CN"), new[] { new GlossaryTerm("literal", "=private_marker") })), resource);
        var changed = (await store.CommitAsync(formula)).Resource;
        var target = Path.Combine(box.Root, "formula.csv");
        await File.WriteAllTextAsync(target, "old output");
        await Throws<GlossaryExportException>(() => store.ExportGlossaryAsync(changed.Revision.ResourceId, target, GlossaryFileFormat.Csv, overwrite: true));
        Check(await File.ReadAllTextAsync(target) == "old output", "Refused CSV export damaged old output");
        await store.ExportGlossaryAsync(changed.Revision.ResourceId, target, GlossaryFileFormat.Csv, overwrite: true, allowFormulaLikeCells: true);
        Check((await File.ReadAllTextAsync(target)).Contains("=private_marker") && !Directory.EnumerateFiles(box.Root, ".lexicon-export-*").Any(), "Raw opt-in changed data or leaked output temp");
    }

    private static async Task Limits()
    {
        using var box = new Sandbox();
        var store = box.Store;
        for (var i = 0; i < LexiconStore.MaxResources; i++)
        {
            using var preview = await store.PrepareGlossaryAsync(Glossary());
            await store.CommitAsync(preview);
        }
        var bytes = Hash(box.CatalogPath);
        await Error(() => store.PrepareGlossaryAsync(Glossary()), LexiconStorageError.TooManyResources);
        Check((await store.ListAsync()).Count == LexiconStore.MaxResources && Hash(box.CatalogPath) == bytes, "Resource ceiling lost data");
        using var revisions = new Sandbox();
        var rstore = revisions.Store;
        var snapshot = new GlossaryDocument(Glossary().Glossary, Provenance, new(new string('a', 32), LexiconRevision.MaxNumber, DateTimeOffset.UtcNow));
        using var initial = await rstore.PrepareGlossaryAsync(snapshot);
        var resource = (await rstore.CommitAsync(initial)).Resource;
        await Throws<LexiconImportException>(() => rstore.PrepareGlossaryAsync(Glossary(), resource));
        Check((await rstore.ListAsync()).Single() == resource && revisions.NoStages(), "Revision overflow damaged existing data");
    }
}
