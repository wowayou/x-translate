using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace XTranslate.App.Lexicons;

internal static class LexiconFiles
{
    internal const string CleanupKey = "LexiconCleanupPending";

    internal static async Task<T> Safe<T>(Func<Task<T>> action, CancellationToken token)
    {
        try { return await action().ConfigureAwait(false); }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            var code = error switch
            {
                UnauthorizedAccessException => LexiconStorageError.AccessDenied,
                SqliteException sqlite when sqlite.SqliteErrorCode is 11 or 26 => LexiconStorageError.CorruptResource,
                _ => LexiconStorageError.IoFailure
            };
            throw new LexiconStorageException(code, error.Data[CleanupKey] is true);
        }
    }

    internal static void NoLinks(string path)
    {
        // Generated child names plus reparse checks prevent catalog paths/junctions escaping the data directory.
        // This is not a sandbox against another process with the same user's filesystem privileges.
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new LexiconStorageException(LexiconStorageError.UnsafePath);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static FileStream ReadLocked(string path)
    {
        NoLinks(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    }

    internal static async Task<string> HashAsync(Stream input, CancellationToken token)
    {
        input.Position = 0;
        var digest = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        input.Position = 0;
        return Convert.ToHexStringLower(digest);
    }

    internal static void Publish(string temporary, string destination, bool overwrite)
    {
        NoLinks(temporary);
        NoLinks(destination);
        // Both names are siblings/on the same volume. No delete-then-move and no copy fallback.
        if (overwrite && File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
    }

    internal static bool DeleteFile(string path)
    {
        try { NoLinks(path); File.Delete(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LexiconStorageException) { return false; }
    }

    internal static bool CleanStage(string directory)
    {
        try
        {
            NoLinks(directory);
            if (!Directory.Exists(directory)) return true;
            // Never recursively follow links or delete unexpected/user-added files.
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { "lease", "input", "dictionary.db", "dictionary.db-journal", "glossary.json" };
            var files = Directory.EnumerateFileSystemEntries(directory).Take(7).ToArray();
            if (files.Length > allowed.Count || files.Any(path => !allowed.Contains(Path.GetFileName(path)))) return false;
            foreach (var path in files) NoLinks(path);
            foreach (var path in files) File.Delete(path);
            Directory.Delete(directory);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LexiconStorageException) { return false; }
    }
}
