using System.Security.Cryptography;
using XTranslate.Core.Lexicons;

namespace XTranslate.App.Lexicons;

public sealed partial class LexiconStore
{
    public Task<PreparedLexiconImport> PrepareDictionaryAsync(string sourcePath, DictionaryInfo info, DictionaryColumnMap columns,
        LexiconProvenance? provenance = null, LexiconResource? replace = null, string changeNote = "", int sampleSize = 20,
        IProgress<LexiconImportProgress>? progress = null, CancellationToken cancellationToken = default) => Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(columns);
        info = info.Normalize();
        provenance ??= LexiconProvenance.Unspecified;
        provenance.Validate();
        if (sampleSize is < 0 or > DictionaryCsv.MaxPreviewEntries) throw new ArgumentOutOfRangeException(nameof(sampleSize));
        return await WithStageAsync(LexiconKind.Dictionary, replace, async (directory, lease) =>
        {
            using var captured = await CaptureAsync(sourcePath, directory, 256L * 1024 * 1024, progress, cancellationToken).ConfigureAwait(false);
            var hash = await LexiconFiles.HashAsync(captured, cancellationToken).ConfigureAwait(false);
            var built = await DictionaryDatabase.BuildAsync(Path.Combine(directory, "dictionary.db"), captured, columns, sampleSize,
                progress, captured.Length, _checkpoint, cancellationToken).ConfigureAwait(false);
            var revision = Revision(replace, null, hash, changeNote);
            var resource = await DescribeAsync(directory, LexiconKind.Dictionary, revision, provenance, info.Name, "", info.SourceLanguage,
                info.TargetLanguage, built.Count, hash, cancellationToken).ConfigureAwait(false);
            return new PreparedLexiconImport(this, directory, lease, resource, replace, built.Sample, null);
        }, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    public Task<PreparedLexiconImport> PrepareGlossaryCsvAsync(string sourcePath, GlossaryInfo info, GlossaryCsvOptions? options = null,
        LexiconProvenance? provenance = null, LexiconResource? replace = null, string changeNote = "",
        IProgress<LexiconImportProgress>? progress = null, CancellationToken cancellationToken = default) => Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(info);
        info = info.Normalize();
        provenance ??= LexiconProvenance.Unspecified;
        provenance.Validate();
        return await WithStageAsync(LexiconKind.Glossary, replace, async (directory, lease) =>
        {
            using var captured = await CaptureAsync(sourcePath, directory, GlossaryCsv.MaxFileBytes, progress, cancellationToken).ConfigureAwait(false);
            var hash = await LexiconFiles.HashAsync(captured, cancellationToken).ConfigureAwait(false);
            var glossary = await GlossaryCsv.ImportAsync(captured, info, options, cancellationToken).ConfigureAwait(false);
            return await StageGlossaryAsync(directory, lease, new(glossary, provenance), replace, hash, changeNote, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>
    /// v1 gains a new local identity. v2 new imports restore the supplied identity/revision; an existing ID is a
    /// conflict, never implicit overwrite. Explicit replacement keeps the local ID and creates its next revision.
    /// CapturedInputSha256 records these actual input bytes separately from a restored, user-supplied revision.
    /// </summary>
    public Task<PreparedLexiconImport> PrepareGlossaryJsonAsync(string sourcePath, LexiconResource? replace = null, string changeNote = "",
        IProgress<LexiconImportProgress>? progress = null, CancellationToken cancellationToken = default) => Run(() =>
        WithStageAsync(LexiconKind.Glossary, replace, async (directory, lease) =>
        {
            using var captured = await CaptureAsync(sourcePath, directory, GlossaryJson.MaxFileBytes, progress, cancellationToken).ConfigureAwait(false);
            var hash = await LexiconFiles.HashAsync(captured, cancellationToken).ConfigureAwait(false);
            var snapshot = await GlossaryJson.ReadAsync(captured, cancellationToken).ConfigureAwait(false);
            return await StageGlossaryAsync(directory, lease, snapshot, replace, hash, changeNote, cancellationToken).ConfigureAwait(false);
        }, cancellationToken), cancellationToken);

    /// <summary>For validated manual edits/copies. Pass a document without Revision to create a fresh identity.</summary>
    public Task<PreparedLexiconImport> PrepareGlossaryAsync(GlossaryDocument snapshot, LexiconResource? replace = null, string changeNote = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Run(() => WithStageAsync(LexiconKind.Glossary, replace,
            (directory, lease) => StageGlossaryAsync(directory, lease, snapshot, replace, "", changeNote, cancellationToken), cancellationToken), cancellationToken);
    }

    private async Task<PreparedLexiconImport> WithStageAsync(LexiconKind kind, LexiconResource? replace,
        Func<string, FileStream, Task<PreparedLexiconImport>> build, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string directory;
        FileStream lease;
        using (var gate = Gate(write: true))
        {
            var catalog = ReadCatalog(token);
            if (replace is not null)
            {
                if (replace.Kind != kind) throw new LexiconStorageException(LexiconStorageError.StaleRevision);
                CheckExpected(catalog, replace, replace.Revision.ResourceId);
            }
            else if (catalog.Resources.Length >= MaxResources) throw new LexiconStorageException(LexiconStorageError.TooManyResources);
            foreach (var name in new[] { "dictionaries", "glossaries", ".staging" })
            {
                var path = Path.Combine(_root, name);
                LexiconFiles.NoLinks(path);
                Directory.CreateDirectory(path);
            }
            if (!File.Exists(Path.Combine(_root, "catalog.json"))) WriteCatalog(catalog, token);
            directory = Path.Combine(_root, ".staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            lease = new FileStream(Path.Combine(directory, "lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        try
        {
            var result = await build(directory, lease).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error)
        {
            lease.Dispose();
            MarkCleanup(error, LexiconFiles.CleanStage(directory));
            throw;
        }
    }

    private async Task<FileStream> CaptureAsync(string sourcePath, string directory, long maxBytes,
        IProgress<LexiconImportProgress>? progress, CancellationToken token)
    {
        // A read-only sharing lock fixes the original bytes during capture. The selected file is never changed.
        using var source = LexiconFiles.ReadLocked(sourcePath);
        if (source.Length > maxBytes) throw new LexiconImportException(LexiconErrorCode.InputTooLarge);
        var captured = new FileStream(Path.Combine(directory, "input"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        try
        {
            var buffer = new byte[64 * 1024];
            long count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - count + 1)), token).ConfigureAwait(false);
                if (read == 0) break;
                if (read > maxBytes - count) throw new LexiconImportException(LexiconErrorCode.InputTooLarge);
                await captured.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                count += read;
                progress?.Report(new(count, 0));
            }
            token.ThrowIfCancellationRequested();
            captured.Flush(flushToDisk: true);
            captured.Position = 0;
            _checkpoint?.Invoke(StorageCheckpoint.SourceCaptured);
            return captured;
        }
        catch { captured.Dispose(); throw; }
    }

    private async Task<PreparedLexiconImport> StageGlossaryAsync(string directory, FileStream lease, GlossaryDocument snapshot,
        LexiconResource? replace, string capturedHash, string changeNote, CancellationToken token)
    {
        var revision = Revision(replace, snapshot.Revision, capturedHash, changeNote);
        var document = new GlossaryDocument(snapshot.Glossary, snapshot.Provenance, revision);
        using (var output = new FileStream(Path.Combine(directory, "glossary.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await GlossaryExport.WriteJsonAsync(output, document, token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        var info = snapshot.Glossary.Info;
        var resource = await DescribeAsync(directory, LexiconKind.Glossary, revision, document.Provenance, info.Name, info.Domain,
            info.SourceLanguage, info.TargetLanguage, document.Glossary.Terms.Count, capturedHash, token).ConfigureAwait(false);
        return new(this, directory, lease, resource, replace, null, document);
    }

    private static LexiconRevision Revision(LexiconResource? replace, LexiconRevision? restored, string hash, string note)
    {
        LexiconRevision revision;
        if (replace is null && restored is not null)
        {
            // A restore is not a new edit. Changing the note requires an explicit replacement/new document.
            if (note.Length != 0) throw new LexiconImportException(LexiconErrorCode.InvalidMetadata);
            revision = restored;
        }
        else revision = new(replace?.Revision.ResourceId ?? Guid.NewGuid().ToString("N"),
            (replace?.Revision.Number ?? 0) + 1, DateTimeOffset.UtcNow, hash, note);
        revision.Validate();
        return revision;
    }

    private async Task<LexiconResource> DescribeAsync(string directory, LexiconKind kind, LexiconRevision revision, LexiconProvenance provenance,
        string name, string domain, string source, string target, int entries, string capturedHash, CancellationToken token)
    {
        var path = Path.Combine(directory, kind == LexiconKind.Dictionary ? "dictionary.db" : "glossary.json");
        using (var flush = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) flush.Flush(flushToDisk: true);
        _checkpoint?.Invoke(StorageCheckpoint.ContentFlushed);
        using var pin = LexiconFiles.ReadLocked(path);
        if (pin.Length > (kind == LexiconKind.Dictionary ? DictionaryDatabase.MaxBytes : GlossaryJson.MaxFileBytes))
            throw new LexiconStorageException(LexiconStorageError.ResourceTooLarge);
        var resource = new LexiconResource(kind, revision, provenance, name, domain, source, target, entries,
            Guid.NewGuid().ToString("N"), pin.Length, await LexiconFiles.HashAsync(pin, token).ConfigureAwait(false), capturedHash);
        ValidateResource(resource);
        return resource;
    }
}
