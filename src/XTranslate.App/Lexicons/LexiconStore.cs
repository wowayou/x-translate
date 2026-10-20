using System.Text.Json;
using System.Text.Json.Serialization;
using XTranslate.Core.Lexicons;

namespace XTranslate.App.Lexicons;

/// <summary>
/// App-owned, on-demand storage under data/lexicons. Construction does no I/O and starts no tasks.
/// All mutation publishes one catalog rename; dictionary files and glossary snapshots are immutable generations.
/// </summary>
public sealed partial class LexiconStore
{
    public const int MaxResources = 128;
    private const int MaxCatalogBytes = 16 * 1024 * 1024;
    private readonly string _root;
    private readonly Action<StorageCheckpoint>? _checkpoint;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };
    private sealed record Catalog(int Version, LexiconResource[] Resources);

    public LexiconStore(string dataDirectory) : this(dataDirectory, null) { }
    internal LexiconStore(string dataDirectory, Action<StorageCheckpoint>? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _root = Path.Combine(Path.GetFullPath(dataDirectory), "lexicons");
        _checkpoint = checkpoint;
    }

    public Task<IReadOnlyList<LexiconResource>> ListAsync(CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            LexiconFiles.NoLinks(_root);
            if (!Directory.Exists(_root))
            {
                if (File.Exists(_root)) throw new LexiconStorageException(LexiconStorageError.CorruptCatalog);
                return Task.FromResult<IReadOnlyList<LexiconResource>>(Array.Empty<LexiconResource>());
            }
            using var gate = Gate(write: false);
            return Task.FromResult<IReadOnlyList<LexiconResource>>(Array.AsReadOnly(ReadCatalog(cancellationToken).Resources));
        }, cancellationToken);

    public Task<LexiconCommitResult> CommitAsync(PreparedLexiconImport preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return Run(async () =>
        {
            preview.BeginCommit(this);
            string? moved = null;
            LexiconResource? old = null;
            var committed = false;
            try
            {
                using var gate = Gate(write: true);
                var catalog = ReadCatalog(cancellationToken);
                old = CheckExpected(catalog, preview.Expected, preview.Resource.Revision.ResourceId);
                if (old is null && catalog.Resources.Length >= MaxResources)
                    throw new LexiconStorageException(LexiconStorageError.TooManyResources);
                ValidateResource(preview.Resource);
                using (var pin = LexiconFiles.ReadLocked(preview.ContentPath))
                    await VerifyAsync(pin, preview.Resource, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ContentPath(preview.Resource);
                LexiconFiles.NoLinks(destination);
                File.Move(preview.ContentPath, destination); // Unique generation; never overwrite a resource file.
                moved = destination;
                _checkpoint?.Invoke(StorageCheckpoint.BlobMoved);
                // Reopen/pin the final name before publishing it; corrupt or changed staging is never admitted.
                using (var pin = LexiconFiles.ReadLocked(destination))
                {
                    await VerifyAsync(pin, preview.Resource, cancellationToken).ConfigureAwait(false);
                    var resources = catalog.Resources.ToList();
                    if (old is null) resources.Add(preview.Resource);
                    else resources[resources.FindIndex(item => item.Revision.ResourceId == old.Revision.ResourceId)] = preview.Resource;
                    WriteCatalog(new(1, resources.ToArray()), cancellationToken);
                    committed = true;
                }
            }
            catch (Exception error) when (!committed)
            {
                var clean = preview.Finish();
                if (moved is not null) clean &= LexiconFiles.DeleteFile(moved);
                MarkCleanup(error, clean);
                throw;
            }
            // Publication is the commit point. Cleanup cannot roll it back, nor can late cancellation.
            var cleaned = preview.Finish();
            if (old is not null) cleaned &= LexiconFiles.DeleteFile(ContentPath(old));
            return new LexiconCommitResult(preview.Resource, !cleaned);
        }, cancellationToken);
    }

    public Task<LexiconDeleteResult> DeleteAsync(LexiconResource expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return Run(() =>
        {
            using var gate = Gate(write: true);
            var catalog = ReadCatalog(cancellationToken);
            var old = CheckExpected(catalog, expected, expected.Revision.ResourceId)!;
            WriteCatalog(new(1, catalog.Resources.Where(item => item.Revision.ResourceId != old.Revision.ResourceId).ToArray()), cancellationToken);
            return Task.FromResult(new LexiconDeleteResult(!LexiconFiles.DeleteFile(ContentPath(old))));
        }, cancellationToken);
    }

    private Task<T> Run<T>(Func<Task<T>> action, CancellationToken token) =>
        Task.Run(() => LexiconFiles.Safe(action, token), token);

    private string ContentPath(LexiconResource resource) => Path.Combine(_root,
        resource.Kind == LexiconKind.Dictionary ? "dictionaries" : "glossaries",
        resource.StorageKey + (resource.Kind == LexiconKind.Dictionary ? ".db" : ".json"));

    private FileStream Gate(bool write)
    {
        LexiconFiles.NoLinks(_root);
        if (write) Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "catalog.lock");
        LexiconFiles.NoLinks(path);
        try
        {
            return new FileStream(path, write ? FileMode.OpenOrCreate : FileMode.Open,
                write ? FileAccess.ReadWrite : FileAccess.Read, write ? FileShare.None : FileShare.Read);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            throw new LexiconStorageException(LexiconStorageError.Busy);
        }
    }

    private Catalog ReadCatalog(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.Combine(_root, "catalog.json");
        LexiconFiles.NoLinks(path);
        if (!File.Exists(path))
        {
            // A missing catalog is only empty before first publication, never a silent reset of existing data.
            foreach (var name in new[] { "dictionaries", "glossaries" })
            {
                var directory = Path.Combine(_root, name);
                LexiconFiles.NoLinks(directory);
                if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                    throw new LexiconStorageException(LexiconStorageError.CorruptCatalog);
            }
            return new(1, Array.Empty<LexiconResource>());
        }
        try
        {
            using var stream = LexiconFiles.ReadLocked(path);
            if (stream.Length is < 1 or > MaxCatalogBytes) throw new JsonException();
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("resources", out var items) ||
                items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaxResources) throw new JsonException();
            foreach (var item in items.EnumerateArray())
            {
                // These records have convenient optional constructor arguments in Core. A persisted catalog must
                // nevertheless contain every field: a missing license/hash is corruption, not an implicit default.
                RequireFields(item, "revision", "provenance");
                RequireFields(item.GetProperty("revision"), "resourceId", "number", "createdUtc", "sourceSha256", "changeNote");
                RequireFields(item.GetProperty("provenance"), "sourceName", "sourceUri", "license", "licenseUri", "attribution", "sourceVersion");
            }
            var catalog = root.Deserialize<Catalog>(Json);
            if (catalog is null || catalog.Version != 1 || catalog.Resources is null || catalog.Resources.Length > MaxResources)
                throw new JsonException();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var generations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in catalog.Resources)
            {
                ValidateResource(resource);
                if (!ids.Add(resource.Revision.ResourceId) || !generations.Add(resource.StorageKey)) throw new JsonException();
            }
            token.ThrowIfCancellationRequested();
            return catalog;
        }
        catch (Exception error) when (error is JsonException or LexiconImportException or ArgumentException or NullReferenceException)
        {
            throw new LexiconStorageException(LexiconStorageError.CorruptCatalog);
        }
    }

    private static void RequireFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object || fields.Any(field => !element.TryGetProperty(field, out _)))
            throw new JsonException();
    }

    private static void ValidateResource(LexiconResource resource)
    {
        if (resource is null || resource.Revision is null || resource.Provenance is null || !Enum.IsDefined(resource.Kind) ||
            !LexiconRevision.IsId(resource.StorageKey) || !LexiconRevision.IsHash(resource.ContentSha256) ||
            resource.CapturedInputSha256 is null || resource.CapturedInputSha256.Length != 0 && !LexiconRevision.IsHash(resource.CapturedInputSha256) ||
            resource.EntryCount < 1 || resource.EntryCount > (resource.Kind == LexiconKind.Dictionary ? DictionaryDatabase.MaxEntries : Glossary.MaxTerms) ||
            resource.ByteLength < 1 || resource.ByteLength > (resource.Kind == LexiconKind.Dictionary ? DictionaryDatabase.MaxBytes : GlossaryJson.MaxFileBytes))
            throw new LexiconImportException(LexiconErrorCode.InvalidMetadata);
        resource.Revision.Validate();
        resource.Provenance.Validate();
        if (resource.Kind == LexiconKind.Dictionary)
        {
            var info = new DictionaryInfo(resource.Name, resource.SourceLanguage, resource.TargetLanguage);
            if (resource.Domain != "" || info.Normalize() != info) throw new LexiconImportException(LexiconErrorCode.InvalidMetadata);
        }
        else
        {
            var info = new GlossaryInfo(resource.Name, resource.Domain, resource.SourceLanguage, resource.TargetLanguage);
            if (info.Normalize() != info) throw new LexiconImportException(LexiconErrorCode.InvalidMetadata);
        }
    }

    private static LexiconResource? CheckExpected(Catalog catalog, LexiconResource? expected, string id)
    {
        if (!LexiconRevision.IsId(id)) throw new LexiconStorageException(LexiconStorageError.UnsafePath);
        var current = catalog.Resources.SingleOrDefault(item => item.Revision.ResourceId == id);
        if (expected is null)
        {
            if (current is not null) throw new LexiconStorageException(LexiconStorageError.AlreadyExists);
            return null;
        }
        if (current is null) throw new LexiconStorageException(LexiconStorageError.ResourceNotFound);
        if (current.StorageKey != expected.StorageKey || current.Kind != expected.Kind)
            throw new LexiconStorageException(LexiconStorageError.StaleRevision);
        return current;
    }

    private void WriteCatalog(Catalog catalog, CancellationToken token)
    {
        var temporary = Path.Combine(_root, ".catalog-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, catalog, Json);
                if (stream.Length > MaxCatalogBytes) throw new LexiconStorageException(LexiconStorageError.ResourceTooLarge);
                stream.Flush(flushToDisk: true);
            }
            _checkpoint?.Invoke(StorageCheckpoint.CatalogFlushed);
            token.ThrowIfCancellationRequested();
            LexiconFiles.Publish(temporary, Path.Combine(_root, "catalog.json"), overwrite: true);
        }
        catch (Exception error)
        {
            MarkCleanup(error, LexiconFiles.DeleteFile(temporary));
            throw;
        }
    }

    private static async Task VerifyAsync(FileStream pin, LexiconResource resource, CancellationToken token)
    {
        if (pin.Length != resource.ByteLength || await LexiconFiles.HashAsync(pin, token).ConfigureAwait(false) != resource.ContentSha256)
            throw new LexiconStorageException(LexiconStorageError.CorruptResource);
    }

    private static void MarkCleanup(Exception error, bool clean)
    {
        if (clean) return;
        if (error is LexiconStorageException storage) throw new LexiconStorageException(storage.Code, cleanupPending: true);
        error.Data[LexiconFiles.CleanupKey] = true;
    }
}
