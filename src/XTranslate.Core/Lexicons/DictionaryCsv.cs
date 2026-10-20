using System.Runtime.CompilerServices;

namespace XTranslate.Core.Lexicons;

/// <summary>Exact header-name mapping; null means deliberately not imported. Names ignore case and outer header whitespace.</summary>
public sealed record DictionaryColumnMap(
    string Headword,
    string? Definition = null,
    string? Translation = null,
    string? Phonetic = null,
    string? PartOfSpeech = null,
    string? Examples = null,
    string? Inflections = null)
{
    /// <summary>ECDICT CSV, not the unrelated StarDict file format or ECDICT's SQLite export.</summary>
    public static DictionaryColumnMap Ecdict { get; } = new("word", "definition", "translation", "phonetic", "pos", Inflections: "exchange");
}

/// <summary>Plain source fields. No HTML interpretation, fabricated examples, word-form guessing or implicit unescaping.</summary>
public sealed record DictionaryEntry(string Headword, string Definition, string Translation, string Phonetic,
    string PartOfSpeech, string Examples, string Inflections);

public sealed record DictionaryEntryRecord(int RecordNumber, int LineNumber, DictionaryEntry Entry);

/// <summary>A full-file validation with only a bounded sample retained. It is not a persistent dictionary index.</summary>
public sealed class DictionaryImportPreview
{
    public int EntryCount { get; }
    public IReadOnlyList<DictionaryEntryRecord> Sample { get; }
    internal DictionaryImportPreview(int entryCount, List<DictionaryEntryRecord> sample)
    {
        EntryCount = entryCount;
        Sample = Array.AsReadOnly(sample.ToArray());
    }
}

public static class DictionaryCsv
{
    public const int MaxHeadwordChars = 256;
    public const int MaxPreviewEntries = 100;

    /// <summary>
    /// A header is required. Every mapped header must exist; unselected columns are still subject to parser limits.
    /// Repeated headwords remain separate entries/meanings. The caller must not commit until enumeration completes.
    /// Invalid rows abort, with no success result, rather than silently becoming missing dictionary entries.
    /// </summary>
    public static async IAsyncEnumerable<DictionaryEntryRecord> ReadAsync(Stream input, DictionaryColumnMap columns,
        DelimitedTextLimits? limits = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (string.IsNullOrWhiteSpace(columns.Headword) || columns.Definition is null && columns.Translation is null)
            throw new LexiconImportException(LexiconErrorCode.InvalidColumnMapping);
        ResolvedColumns? mapping = null;
        var entries = 0;
        await foreach (var row in DelimitedText.ReadAsync(input, DelimitedTextFormat.Csv, limits, cancellationToken).ConfigureAwait(false))
        {
            if (mapping is null)
            {
                LexiconText.ValidateHeader(row);
                mapping = Resolve(row, columns);
                continue;
            }
            if (row.Fields.Count != mapping.FieldCount)
                throw new LexiconImportException(LexiconErrorCode.WrongColumnCount, row.RecordNumber, row.LineNumber);
            var word = row.Fields[mapping.Headword];
            if (string.IsNullOrWhiteSpace(word)) throw At(LexiconErrorCode.MissingHeadword, row, mapping.Headword);
            if (word.Length > MaxHeadwordChars) throw At(LexiconErrorCode.FieldTooLong, row, mapping.Headword);
            if (!LexiconText.IsValid(word, multiline: false) || LexiconText.HasOuterWhitespace(word))
                throw At(LexiconErrorCode.InvalidHeadword, row, mapping.Headword);
            var definition = Field(row, mapping.Definition);
            var translation = Field(row, mapping.Translation);
            if (string.IsNullOrWhiteSpace(definition) && string.IsNullOrWhiteSpace(translation))
                throw At(LexiconErrorCode.MissingDefinition, row, mapping.Definition >= 0 ? mapping.Definition : mapping.Translation);
            entries++;
            yield return new DictionaryEntryRecord(row.RecordNumber, row.LineNumber, new DictionaryEntry(word, definition, translation,
                Field(row, mapping.Phonetic), Field(row, mapping.PartOfSpeech), Field(row, mapping.Examples), Field(row, mapping.Inflections)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (mapping is null) throw new LexiconImportException(LexiconErrorCode.EmptyInput);
        if (entries == 0) throw new LexiconImportException(LexiconErrorCode.MissingData);
    }

    public static async Task<DictionaryImportPreview> PreviewAsync(Stream input, DictionaryColumnMap columns,
        int sampleSize = 20, DelimitedTextLimits? limits = null, CancellationToken cancellationToken = default)
    {
        if (sampleSize is < 0 or > MaxPreviewEntries) throw new ArgumentOutOfRangeException(nameof(sampleSize));
        var sample = new List<DictionaryEntryRecord>(sampleSize);
        var count = 0;
        await foreach (var row in ReadAsync(input, columns, limits, cancellationToken).ConfigureAwait(false))
        {
            count++;
            if (sample.Count < sampleSize) sample.Add(row);
        }
        return new DictionaryImportPreview(count, sample);
    }

    private sealed record ResolvedColumns(int FieldCount, int Headword, int Definition, int Translation, int Phonetic,
        int PartOfSpeech, int Examples, int Inflections);

    private static ResolvedColumns Resolve(DelimitedTextRecord header, DictionaryColumnMap columns)
    {
        var names = header.Fields.Select((name, index) => (Name: name.Trim(), Index: index))
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<int>();
        int Column(string? name)
        {
            if (name is null) return -1;
            if (!names.TryGetValue(name.Trim(), out var index) || !selected.Add(index))
                throw new LexiconImportException(LexiconErrorCode.InvalidColumnMapping, header.RecordNumber, header.LineNumber);
            return index;
        }
        return new ResolvedColumns(header.Fields.Count, Column(columns.Headword), Column(columns.Definition), Column(columns.Translation),
            Column(columns.Phonetic), Column(columns.PartOfSpeech), Column(columns.Examples), Column(columns.Inflections));
    }

    private static string Field(DelimitedTextRecord row, int column) => column < 0 ? "" : row.Fields[column];
    private static LexiconImportException At(LexiconErrorCode code, DelimitedTextRecord row, int column) =>
        new(code, row.RecordNumber, row.LineNumber, column + 1);
}
