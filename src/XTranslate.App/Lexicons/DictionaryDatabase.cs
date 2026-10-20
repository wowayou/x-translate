using Microsoft.Data.Sqlite;
using XTranslate.Core.Lexicons;

namespace XTranslate.App.Lexicons;

internal static class DictionaryDatabase
{
    internal const long MaxBytes = 1024L * 1024 * 1024;
    internal const int MaxEntries = 1_000_000;
    internal const int ApplicationId = 0x58544C58;
    internal const string TableSql = "CREATE TABLE entries (id INTEGER PRIMARY KEY, record_number INTEGER NOT NULL, line_number INTEGER NOT NULL, headword TEXT NOT NULL COLLATE BINARY, definition TEXT NOT NULL, translation TEXT NOT NULL, phonetic TEXT NOT NULL, part_of_speech TEXT NOT NULL, examples TEXT NOT NULL, inflections TEXT NOT NULL)";
    internal const string IndexSql = "CREATE INDEX exact_headword ON entries(headword, id)";

    internal static SqliteConnection Connect(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            connection.EnableExtensions(false);
            SQLitePCL.raw.sqlite3_limit(connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, 1024 * 1024);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA cache_size=-2048; PRAGMA temp_store=MEMORY;" +
                (readOnly ? "PRAGMA query_only=ON;" : "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA max_page_count=262144;");
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static async Task<(int Count, IReadOnlyList<DictionaryEntryRecord> Sample)> BuildAsync(string path, Stream input,
        DictionaryColumnMap columns, int sampleSize, IProgress<LexiconImportProgress>? progress, long capturedBytes,
        Action<StorageCheckpoint>? checkpoint, CancellationToken token)
    {
        using var connection = Connect(path, readOnly: false);
        using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1; {TableSql}; {IndexSql};";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        // Maintain the index during insertion; no whole-dictionary managed sort or temporary sort database.
        command.CommandText = "INSERT INTO entries VALUES (NULL,$record,$line,$word,$definition,$translation,$phonetic,$pos,$examples,$inflections)";
        foreach (var name in new[] { "$record", "$line" }) command.Parameters.Add(name, SqliteType.Integer);
        foreach (var name in new[] { "$word", "$definition", "$translation", "$phonetic", "$pos", "$examples", "$inflections" })
            command.Parameters.Add(name, SqliteType.Text);
        command.Prepare();
        var sample = new List<DictionaryEntryRecord>();
        var sampleChars = 0;
        var sampleFull = false;
        var count = 0;
        await foreach (var row in DictionaryCsv.ReadAsync(input, columns, cancellationToken: token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var e = row.Entry;
            command.Parameters[0].Value = row.RecordNumber;
            command.Parameters[1].Value = row.LineNumber;
            var fields = Fields(e);
            for (var i = 0; i < fields.Length; i++) command.Parameters[i + 2].Value = fields[i];
            command.ExecuteNonQuery();
            count++;
            if (!sampleFull && sample.Count < sampleSize)
            {
                var chars = fields.Sum(field => field.Length);
                if (sampleChars + chars <= LocalDictionaryReader.MaxPageChars) { sample.Add(row); sampleChars += chars; }
                else sampleFull = true;
            }
            checkpoint?.Invoke(StorageCheckpoint.DictionaryRowWritten);
            if (count % 1024 == 0) progress?.Report(new(capturedBytes, count));
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        token.ThrowIfCancellationRequested();
        return (count, Array.AsReadOnly(sample.ToArray()));
    }

    internal static string[] Fields(DictionaryEntry e) =>
        new[] { e.Headword, e.Definition, e.Translation, e.Phonetic, e.PartOfSpeech, e.Examples, e.Inflections };
}

/// <summary>
/// Pins one immutable dictionary generation, not its full contents. Open and query off the UI thread; dispose when
/// the view no longer needs it. Exact ordinal/case-sensitive lookup only; no normalization, morphology, or HTTP.
/// </summary>
public sealed class LocalDictionaryReader : IDisposable
{
    public const int MaxPageSize = 100;
    public const int MaxPageChars = 1_000_000;
    private readonly FileStream _pin;
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;
    public LexiconResource Resource { get; }

    internal LocalDictionaryReader(FileStream pin, string path, LexiconResource resource, CancellationToken token)
    {
        _pin = pin;
        Resource = resource;
        _connection = DictionaryDatabase.Connect(path, readOnly: true);
        try
        {
            using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(_connection.Handle));
            using var command = _connection.CreateCommand();
            object? Scalar(string sql) { token.ThrowIfCancellationRequested(); command.CommandText = sql; return command.ExecuteScalar(); }
            if (Convert.ToInt64(Scalar("PRAGMA application_id")) != DictionaryDatabase.ApplicationId ||
                Convert.ToInt64(Scalar("PRAGMA user_version")) != 1 ||
                Convert.ToInt64(Scalar("SELECT count(*) FROM sqlite_schema")) != 2 ||
                Scalar("SELECT sql FROM sqlite_schema WHERE name='entries'") as string != DictionaryDatabase.TableSql ||
                Scalar("SELECT sql FROM sqlite_schema WHERE name='exact_headword'") as string != DictionaryDatabase.IndexSql ||
                Scalar("PRAGMA quick_check(1)") as string != "ok" ||
                Convert.ToInt64(Scalar("SELECT count(*) FROM entries")) != resource.EntryCount)
                throw new LexiconStorageException(LexiconStorageError.CorruptResource);
            token.ThrowIfCancellationRequested();
        }
        catch { _connection.Dispose(); throw; }
    }

    public Task<DictionaryLookupPage> LookupAsync(string headword, int offset = 0, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(headword) || headword.Length > DictionaryCsv.MaxHeadwordChars || headword.Trim() != headword ||
            headword.Any(char.IsControl)) throw new ArgumentException("An exact headword is required.", nameof(headword));
        // Reject unpaired surrogates instead of letting the UTF-8 provider silently replace them.
        _ = new System.Text.UTF8Encoding(false, true).GetByteCount(headword);
        if (offset is < 0 or > DictionaryDatabase.MaxEntries || pageSize is < 1 or > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        return Task.Run(() => LexiconFiles.Safe(() => Task.FromResult(Lookup(headword, offset, pageSize, cancellationToken)), cancellationToken), cancellationToken);
    }

    private DictionaryLookupPage Lookup(string headword, int offset, int size, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(_connection.Handle));
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM entries WHERE headword=$word";
            command.Parameters.AddWithValue("$word", headword);
            var total = Convert.ToInt32(command.ExecuteScalar());
            if (total < 0 || total > Resource.EntryCount) throw new LexiconStorageException(LexiconStorageError.CorruptResource);
            command.CommandText = "SELECT record_number,line_number,headword,definition,translation,phonetic,part_of_speech,examples,inflections " +
                "FROM entries WHERE headword=$word ORDER BY id LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$limit", size);
            command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader();
            var rows = new List<DictionaryEntryRecord>();
            var chars = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var fields = Enumerable.Range(2, 7).Select(reader.GetString).ToArray();
                if (fields[0] != headword || fields.Any(field => field.Length > 65_536))
                    throw new LexiconStorageException(LexiconStorageError.CorruptResource);
                var length = fields.Sum(field => field.Length);
                if (chars + length > MaxPageChars) break; // Explicit page continuation, never truncate a meaning.
                var record = reader.GetInt32(0);
                var line = reader.GetInt32(1);
                if (record < 2 || record > DictionaryDatabase.MaxEntries + 1 || line < 2)
                    throw new LexiconStorageException(LexiconStorageError.CorruptResource);
                rows.Add(new(record, line, new(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6])));
                chars += length;
            }
            token.ThrowIfCancellationRequested();
            return new(Array.AsReadOnly(rows.ToArray()), total, Math.Min(offset + rows.Count, total));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
            _pin.Dispose();
        }
    }
}
