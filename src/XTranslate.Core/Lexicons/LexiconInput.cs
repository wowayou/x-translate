using System.Buffers;
using System.Text;

namespace XTranslate.Core.Lexicons;

/// <summary>Stable, content-free reasons for rejecting a local lexical resource.</summary>
public enum LexiconErrorCode
{
    InvalidEncoding, InvalidCharacter, MalformedDelimitedText, InputTooLarge, TooManyRecords,
    TooManyColumns, FieldTooLong, RecordTooLong, EmptyInput, MissingData, InvalidHeader,
    InvalidColumnMapping, WrongColumnCount, MissingHeadword, MissingDefinition, InvalidHeadword,
    InvalidGlossaryInfo, InvalidLanguage, MissingSourceTerm, MissingTargetTerm, InvalidTerm,
    DuplicateTerm, ConflictingTerm, InvalidBoolean, TooManyTerms, GlossaryTooLarge, MatchTextTooLong,
    InvalidMetadata, InvalidDictionaryInfo, InvalidJson, UnsupportedSchemaVersion
}

/// <summary>
/// Positions are one-based logical record, physical line and field numbers; zero means not applicable.
/// Neither the message nor an inner exception contains imported text, a filename or credentials.
/// </summary>
public sealed class LexiconImportException : Exception
{
    public LexiconErrorCode Code { get; }
    public int RecordNumber { get; }
    public int LineNumber { get; }
    public int ColumnNumber { get; }
    public int? RelatedRecordNumber { get; }

    public LexiconImportException(LexiconErrorCode code, int recordNumber = 0, int lineNumber = 0,
        int columnNumber = 0, int? relatedRecordNumber = null)
        : base($"Lexicon input rejected: {code} (record {recordNumber}, line {lineNumber}, field {columnNumber}).")
    {
        Code = code;
        RecordNumber = recordNumber;
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
        RelatedRecordNumber = relatedRecordNumber;
    }
}

public enum DelimitedTextFormat { Csv, Tsv }

/// <summary>
/// Limits apply while reading, including ignored columns. Characters are UTF-16 code units.
/// MaxRecords includes a header. These limits do not authorize buffering the whole input.
/// </summary>
public sealed record DelimitedTextLimits(
    long MaxBytes = 256L * 1024 * 1024,
    int MaxRecords = 1_000_001,
    int MaxColumns = 64,
    int MaxFieldChars = 65_536,
    int MaxRecordChars = 131_072)
{
    internal void Validate()
    {
        if (MaxBytes < 1 || MaxBytes == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(MaxBytes));
        if (MaxRecords < 1 || MaxRecords == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(MaxRecords));
        if (MaxColumns < 1) throw new ArgumentOutOfRangeException(nameof(MaxColumns));
        if (MaxFieldChars < 1) throw new ArgumentOutOfRangeException(nameof(MaxFieldChars));
        if (MaxRecordChars < 1) throw new ArgumentOutOfRangeException(nameof(MaxRecordChars));
    }
}

public sealed class DelimitedTextRecord
{
    public int RecordNumber { get; }
    public int LineNumber { get; }
    public IReadOnlyList<string> Fields { get; }

    internal DelimitedTextRecord(int recordNumber, int lineNumber, string[] fields)
    {
        RecordNumber = recordNumber;
        LineNumber = lineNumber;
        Fields = Array.AsReadOnly(fields);
    }
}

internal static class LexiconText
{
    internal static bool IsValid(string value, bool multiline)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done) return false;
            if (Rune.IsControl(rune) && !(multiline && rune.Value is '\r' or '\n' or '\t')) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    internal static bool HasOuterWhitespace(string value) => value.Length != value.Trim().Length;

    internal static void ValidateHeader(DelimitedTextRecord header)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var column = 0; column < header.Fields.Count; column++)
        {
            var name = header.Fields[column].Trim();
            if (name.Length == 0 || !IsValid(name, multiline: false) || !names.Add(name))
                throw new LexiconImportException(LexiconErrorCode.InvalidHeader, header.RecordNumber, header.LineNumber, column + 1);
        }
    }
}
