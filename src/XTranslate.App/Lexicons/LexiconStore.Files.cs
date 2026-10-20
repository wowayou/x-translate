using XTranslate.Core.Lexicons;

namespace XTranslate.App.Lexicons;

public sealed partial class LexiconStore
{
    public Task<LocalDictionaryReader> OpenDictionaryAsync(string resourceId, CancellationToken cancellationToken = default) => Run(async () =>
    {
        var (resource, pin, path) = Pin(resourceId, LexiconKind.Dictionary, cancellationToken);
        try
        {
            await VerifyAsync(pin, resource, cancellationToken).ConfigureAwait(false);
            return new LocalDictionaryReader(pin, path, resource, cancellationToken);
        }
        catch { pin.Dispose(); throw; }
    }, cancellationToken);

    public Task<GlossaryDocument> LoadGlossaryAsync(string resourceId, CancellationToken cancellationToken = default) =>
        Run(() => LoadGlossaryCoreAsync(resourceId, cancellationToken), cancellationToken);

    private async Task<GlossaryDocument> LoadGlossaryCoreAsync(string id, CancellationToken token)
    {
        var (resource, pin, _) = Pin(id, LexiconKind.Glossary, token);
        using (pin)
        {
            await VerifyAsync(pin, resource, token).ConfigureAwait(false);
            try
            {
                var document = await GlossaryJson.ReadAsync(pin, token).ConfigureAwait(false);
                var info = new GlossaryInfo(resource.Name, resource.Domain, resource.SourceLanguage, resource.TargetLanguage);
                if (document.Glossary.Info != info || document.Provenance != resource.Provenance || document.Revision != resource.Revision ||
                    document.Glossary.Terms.Count != resource.EntryCount)
                    throw new LexiconStorageException(LexiconStorageError.CorruptResource);
                return document;
            }
            catch (LexiconImportException) { throw new LexiconStorageException(LexiconStorageError.CorruptResource); }
        }
    }

    private (LexiconResource Resource, FileStream Pin, string Path) Pin(string id, LexiconKind kind, CancellationToken token)
    {
        if (!LexiconRevision.IsId(id)) throw new LexiconStorageException(LexiconStorageError.UnsafePath);
        using var gate = Gate(write: false);
        var resource = ReadCatalog(token).Resources.SingleOrDefault(item => item.Revision.ResourceId == id && item.Kind == kind)
            ?? throw new LexiconStorageException(LexiconStorageError.ResourceNotFound);
        var path = ContentPath(resource);
        try { return (resource, LexiconFiles.ReadLocked(path), path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new LexiconStorageException(LexiconStorageError.CorruptResource);
        }
    }

    /// <summary>
    /// JSON is a full v2 snapshot. CSV/TSV deliberately loses metadata; raw spreadsheet-formula cells require
    /// explicit opt-in. File overwrite also requires explicit opt-in. No uploads or settings changes.
    /// </summary>
    public Task ExportGlossaryAsync(string resourceId, string destinationPath, GlossaryFileFormat format = GlossaryFileFormat.Json,
        bool overwrite = false, bool allowFormulaLikeCells = false, CancellationToken cancellationToken = default) => Run(async () =>
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        var destination = Path.GetFullPath(destinationPath);
        if (destination.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new LexiconStorageException(LexiconStorageError.UnsafePath);
        LexiconFiles.NoLinks(destination);
        if (!overwrite && File.Exists(destination)) throw new LexiconStorageException(LexiconStorageError.AlreadyExists);
        var snapshot = await LoadGlossaryCoreAsync(resourceId, cancellationToken).ConfigureAwait(false);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".lexicon-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                switch (format)
                {
                    case GlossaryFileFormat.Json:
                        await GlossaryExport.WriteJsonAsync(stream, snapshot, cancellationToken).ConfigureAwait(false); break;
                    case GlossaryFileFormat.Markdown:
                        await GlossaryExport.WriteMarkdownAsync(stream, snapshot, cancellationToken).ConfigureAwait(false); break;
                    default:
                        await GlossaryExport.WriteInterchangeAsync(stream, snapshot.Glossary,
                            format == GlossaryFileFormat.Csv ? DelimitedTextFormat.Csv : DelimitedTextFormat.Tsv,
                            allowFormulaLikeCells, cancellationToken).ConfigureAwait(false); break;
                }
                stream.Flush(flushToDisk: true);
            }
            _checkpoint?.Invoke(StorageCheckpoint.ExportFlushed);
            cancellationToken.ThrowIfCancellationRequested();
            LexiconFiles.Publish(temporary, destination, overwrite);
            return true; // Cancellation after publication cannot turn this into a cancelled save.
        }
        catch (Exception error)
        {
            MarkCleanup(error, LexiconFiles.DeleteFile(temporary));
            throw;
        }
    }, cancellationToken);

    /// <summary>
    /// Explicit, bounded housekeeping (at most 256 candidates per call), never an automatic/background deletion.
    /// Skips live previews/readers, referenced generations, unknown names and reparse points. Retry only on user action.
    /// </summary>
    public Task<LexiconCleanupResult> CollectGarbageAsync(CancellationToken cancellationToken = default) => Run(() =>
    {
        LexiconFiles.NoLinks(_root);
        if (!Directory.Exists(_root)) return Task.FromResult(new LexiconCleanupResult(0, 0));
        using var gate = Gate(write: true);
        var catalog = ReadCatalog(cancellationToken);
        var referenced = catalog.Resources.Select(ContentPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        var deferred = 0;
        var candidates = 0;
        foreach (var name in new[] { "dictionaries", "glossaries", ".staging", "" })
        {
            var directory = Path.Combine(_root, name);
            LexiconFiles.NoLinks(directory);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (name == "" && !Path.GetFileName(path).StartsWith(".catalog-", StringComparison.Ordinal)) continue;
                if (referenced.Contains(path)) continue;
                if (++candidates > 256) return Task.FromResult(new LexiconCleanupResult(removed, deferred + 1));
                var fileName = Path.GetFileName(path);
                if (name == ".staging")
                {
                    if (!LexiconRevision.IsId(fileName)) { deferred++; continue; }
                    var leasePath = Path.Combine(path, "lease");
                    try
                    {
                        LexiconFiles.NoLinks(leasePath);
                        if (File.Exists(leasePath))
                        {
                            using var lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        }
                        if (LexiconFiles.CleanStage(path)) removed++; else deferred++;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or LexiconStorageException) { deferred++; }
                }
                else
                {
                    var valid = name == ""
                        ? fileName.Length == 45 && fileName.EndsWith(".tmp", StringComparison.Ordinal) && LexiconRevision.IsId(fileName.Substring(9, 32))
                        : LexiconRevision.IsId(Path.GetFileNameWithoutExtension(path)) &&
                            Path.GetExtension(path) == (name == "dictionaries" ? ".db" : ".json");
                    if (valid && LexiconFiles.DeleteFile(path)) removed++; else deferred++;
                }
            }
        }
        return Task.FromResult(new LexiconCleanupResult(removed, deferred));
    }, cancellationToken);
}
