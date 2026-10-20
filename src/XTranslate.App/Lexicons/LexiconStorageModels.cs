using XTranslate.Core.Lexicons;

namespace XTranslate.App.Lexicons;

public enum LexiconKind { Dictionary, Glossary }
public enum GlossaryFileFormat { Json, Markdown, Csv, Tsv }
public enum LexiconStorageError
{
    IoFailure, AccessDenied, Busy, UnsafePath, CorruptCatalog, CorruptResource,
    ResourceNotFound, StaleRevision, AlreadyExists, TooManyResources, ResourceTooLarge, InvalidStage
}

/// <summary>Safe for diagnostics: no imported content, file paths, SQL, or inner exception.</summary>
public sealed class LexiconStorageException(LexiconStorageError code, bool cleanupPending = false)
    : Exception($"Local lexicon operation failed: {code}.")
{
    public LexiconStorageError Code { get; } = code;
    public bool CleanupPending { get; } = cleanupPending;
}

/// <summary>Small catalog entry, not the dictionary contents. StorageKey is an immutable file generation.</summary>
public sealed record LexiconResource(LexiconKind Kind, LexiconRevision Revision, LexiconProvenance Provenance,
    string Name, string Domain, string SourceLanguage, string TargetLanguage, int EntryCount,
    string StorageKey, long ByteLength, string ContentSha256, string CapturedInputSha256);

public sealed record LexiconImportProgress(long CapturedBytes, int IndexedEntries);
public sealed record LexiconCommitResult(LexiconResource Resource, bool CleanupPending);
public sealed record LexiconDeleteResult(bool CleanupPending);
public sealed record LexiconCleanupResult(int RemovedFiles, int DeferredItems);
public sealed record DictionaryLookupPage(IReadOnlyList<DictionaryEntryRecord> Entries, int TotalCount, int NextOffset)
{
    public bool HasMore => NextOffset < TotalCount;
}

internal enum StorageCheckpoint { SourceCaptured, DictionaryRowWritten, ContentFlushed, BlobMoved, CatalogFlushed, ExportFlushed }

/// <summary>
/// Owns only temporary files. Dispose cancels the preview. Commit consumes it even on failure; never retry it blindly.
/// A successful commit with CleanupPending remains committed; explicit garbage collection can remove deferred files.
/// </summary>
public sealed class PreparedLexiconImport : IDisposable
{
    private readonly FileStream _lease;
    private int _state;
    internal LexiconStore Owner { get; }
    internal string DirectoryPath { get; }
    internal string ContentPath { get; }
    internal LexiconResource? Expected { get; }
    public LexiconResource Resource { get; }
    public IReadOnlyList<DictionaryEntryRecord> DictionarySample { get; }
    public GlossaryDocument? Glossary { get; }

    internal PreparedLexiconImport(LexiconStore owner, string directory, FileStream lease, LexiconResource resource,
        LexiconResource? expected, IReadOnlyList<DictionaryEntryRecord>? sample, GlossaryDocument? glossary)
    {
        Owner = owner;
        DirectoryPath = directory;
        ContentPath = Path.Combine(directory, resource.Kind == LexiconKind.Dictionary ? "dictionary.db" : "glossary.json");
        _lease = lease;
        Resource = resource;
        Expected = expected;
        DictionarySample = sample ?? Array.Empty<DictionaryEntryRecord>();
        Glossary = glossary;
    }

    internal void BeginCommit(LexiconStore owner)
    {
        if (!ReferenceEquals(Owner, owner) || Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new LexiconStorageException(LexiconStorageError.InvalidStage);
    }

    internal bool Finish()
    {
        Interlocked.Exchange(ref _state, 2);
        _lease.Dispose();
        return LexiconFiles.CleanStage(DirectoryPath);
    }

    public void Dispose()
    {
        var state = Interlocked.CompareExchange(ref _state, 2, 0);
        if (state == 2) return;
        if (state == 1) throw new InvalidOperationException("The preview is being committed.");
        if (!Finish()) throw new LexiconStorageException(LexiconStorageError.IoFailure, cleanupPending: true);
    }
}
