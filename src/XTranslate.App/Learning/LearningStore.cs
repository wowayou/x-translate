using XTranslate.App.Lexicons;
using XTranslate.Core.Learning;

namespace XTranslate.App.Learning;

public enum LearningStorageError { IoFailure, AccessDenied, Busy, UnsafePath, Conflict, AlreadyExists }
public sealed class LearningStorageException(LearningStorageError code, bool cleanupPending = false)
    : Exception($"Local learning operation failed: {code}.")
{
    public LearningStorageError Code { get; } = code;
    public bool CleanupPending { get; } = cleanupPending;
}
internal enum LearningCheckpoint { BeforePublish, ExportBeforePublish }

/// <summary>
/// On-demand, bounded JSON notebook. Reuses lexicon file publication/reparse primitives, not its catalog or models.
/// Every change checks the displayed generation under an exclusive writer gate; one sibling rename publishes it.
/// </summary>
public sealed class LearningStore
{
    private readonly string _dataRoot;
    private readonly string _root;
    private readonly string _path;
    private readonly Action<LearningCheckpoint>? _checkpoint;
    public LearningStore(string dataDirectory) : this(dataDirectory, null) { }
    internal LearningStore(string dataDirectory, Action<LearningCheckpoint>? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataRoot = Path.GetFullPath(dataDirectory);
        _root = Path.Combine(_dataRoot, "learning"); _path = Path.Combine(_root, "book.json"); _checkpoint = checkpoint;
    }

    public Task<LearningBook> LoadAsync(CancellationToken token = default) => Run(() => ReadCurrent(token), token);

    /// <summary>Captures and validates a bounded immutable backup once. Confirming restore never rereads the source.</summary>
    public Task<LearningBook> ReadBackupAsync(string path, CancellationToken token = default) => Run(() => ReadFile(path, token), token);

    public Task<LearningBook> ChangeAsync(LearningBook expected, Func<LearningBook, LearningBook> change, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(change);
        return Run(() =>
        {
            LexiconFiles.NoLinks(_root); Directory.CreateDirectory(_root);
            using var gate = Gate();
            var current = ReadCurrent(token);
            if (current.Generation != expected.Generation) throw new LearningStorageException(LearningStorageError.Conflict);
            var updated = change(current);
            ArgumentNullException.ThrowIfNull(updated);
            if (updated.Generation == current.Generation) return current;
            Publish(LearningJson.Write(updated), _path, true, LearningCheckpoint.BeforePublish, token);
            return updated; // Publication is the commit point; late cancellation cannot turn a saved book into failure.
        }, token);
    }

    public Task<LearningBook> RestoreAsync(LearningBook expected, LearningBook capturedBackup, CancellationToken token = default) =>
        ChangeAsync(expected, current => current.Restore(capturedBackup), token);

    /// <summary>Full plain-text backup of the displayed immutable snapshot; never exports into managed data/.</summary>
    public Task ExportAsync(LearningBook snapshot, string destinationPath, bool overwrite = false, CancellationToken token = default) => Run(() =>
    {
        var destination = Path.GetFullPath(destinationPath);
        if (destination.Equals(_dataRoot, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(_dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new LearningStorageException(LearningStorageError.UnsafePath);
        LexiconFiles.NoLinks(destination);
        if (!overwrite && File.Exists(destination)) throw new LearningStorageException(LearningStorageError.AlreadyExists);
        Publish(LearningJson.Write(snapshot), destination, overwrite, LearningCheckpoint.ExportBeforePublish, token);
        return true;
    }, token);

    private LearningBook ReadCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); LexiconFiles.NoLinks(_path);
        if (Directory.Exists(_path)) throw new LearningException(LearningError.InvalidData);
        return File.Exists(_path) ? ReadFile(_path, token) : LearningBook.Empty;
    }

    private static LearningBook ReadFile(string path, CancellationToken token)
    {
        using var input = LexiconFiles.ReadLocked(path);
        if (input.Length > LearningJson.MaxBytes) throw new LearningException(LearningError.TooLarge);
        var data = new byte[checked((int)input.Length)];
        var offset = 0;
        while (offset < data.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = input.Read(data, offset, Math.Min(64 * 1024, data.Length - offset));
            if (count == 0) throw new LearningException(LearningError.InvalidData);
            offset += count;
        }
        token.ThrowIfCancellationRequested();
        return LearningJson.Read(data);
    }

    private FileStream Gate()
    {
        var path = Path.Combine(_root, "book.lock"); LexiconFiles.NoLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        { throw new LearningStorageException(LearningStorageError.Busy); }
    }

    private void Publish(byte[] bytes, string destination, bool overwrite, LearningCheckpoint checkpoint, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".learning-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            LexiconFiles.NoLinks(temporary);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (var offset = 0; offset < bytes.Length; offset += 64 * 1024)
                {
                    token.ThrowIfCancellationRequested(); output.Write(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
                }
                output.Flush(flushToDisk: true);
            }
            _checkpoint?.Invoke(checkpoint); token.ThrowIfCancellationRequested();
            LexiconFiles.Publish(temporary, destination, overwrite);
        }
        catch
        {
            if (!LexiconFiles.DeleteFile(temporary)) throw new LearningStorageException(LearningStorageError.IoFailure, cleanupPending: true);
            throw;
        }
    }

    private static Task<T> Run<T>(Func<T> action, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        try { return action(); }
        catch (LexiconStorageException) { throw new LearningStorageException(LearningStorageError.UnsafePath); }
        catch (UnauthorizedAccessException) { throw new LearningStorageException(LearningStorageError.AccessDenied); }
        catch (IOException) { throw new LearningStorageException(LearningStorageError.IoFailure); }
    }, token);
}
