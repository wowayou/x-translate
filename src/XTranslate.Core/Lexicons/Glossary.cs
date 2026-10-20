using System.Globalization;
using System.Text;

namespace XTranslate.Core.Lexicons;

public enum GlossaryMatchMode { WholeWord, Literal }

/// <summary>Language codes use the app's explicit catalog, never "auto". Provider support is a separate decision.</summary>
public sealed record GlossaryInfo(string Name, string Domain, string SourceLanguage, string TargetLanguage)
{
    public GlossaryInfo Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name) || !LexiconProvenance.Text(Name, 120) || !LexiconProvenance.Text(Domain, 120))
            throw new LexiconImportException(LexiconErrorCode.InvalidGlossaryInfo);
        var source = TranslationLanguages.Find(SourceLanguage);
        var target = TranslationLanguages.Find(TargetLanguage);
        if (source is null || target is null) throw new LexiconImportException(LexiconErrorCode.InvalidLanguage);
        return this with { SourceLanguage = source.Code, TargetLanguage = target.Code };
    }
}

/// <summary>PreserveSource is explicit; an empty ordinary target is invalid. Notes are not translation instructions.</summary>
public sealed record GlossaryTerm(string Source, string Target, string Note = "", bool PreserveSource = false);

/// <summary>Offsets/lengths refer to the unchanged input's UTF-16 code units, suitable for WPF selection.</summary>
public sealed record GlossaryMatch(GlossaryTerm Term, int Offset, int Length, string SourceText, string PreferredTranslation);

/// <summary>
/// Validated, detached snapshot of one domain/language pair. Matching neither translates nor replaces text.
/// Construction and matching are bounded and cancellation-aware; there are no regexes, file writes or HTTP calls.
/// </summary>
public sealed class Glossary
{
    public const int MaxTerms = 10_000;
    public const int MaxSourceChars = 256;
    public const int MaxTargetChars = 512;
    public const int MaxNoteChars = 2048;
    public const int MaxTotalChars = 1_000_000;
    public const int MaxMatchTextChars = 3000;
    private readonly StringComparison _comparison;
    public GlossaryInfo Info { get; }
    public bool CaseSensitive { get; }
    public GlossaryMatchMode MatchMode { get; }
    public IReadOnlyList<GlossaryTerm> Terms { get; }

    private Glossary(GlossaryInfo info, bool caseSensitive, GlossaryMatchMode matchMode, List<GlossaryTerm> terms)
    {
        Info = info;
        CaseSensitive = caseSensitive;
        MatchMode = matchMode;
        Terms = Array.AsReadOnly(terms.ToArray());
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    public static Glossary Create(GlossaryInfo info, IEnumerable<GlossaryTerm> terms, bool caseSensitive = false,
        GlossaryMatchMode matchMode = GlossaryMatchMode.WholeWord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new Builder(info, caseSensitive, matchMode);
        var row = 0;
        foreach (var term in terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(term, ++row, row);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return builder.Build();
    }

    /// <summary>
    /// Left-to-right non-overlapping matches, longest valid phrase at each position. WholeWord prevents matching
    /// inside letters/digits/marks/connector identifiers; adjacent Han characters do not block English terms.
    /// Literal disables that boundary check. Neither mode normalizes Unicode, rewrites input, or treats terms as regex.
    /// </summary>
    public IReadOnlyList<GlossaryMatch> FindMatches(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length > MaxMatchTextChars) throw new LexiconImportException(LexiconErrorCode.MatchTextTooLong);
        if (!LexiconText.IsValid(text, multiline: true)) throw new LexiconImportException(LexiconErrorCode.InvalidCharacter);
        if (text.Length == 0) return Array.Empty<GlossaryMatch>();
        // At most 3000 slots. Search each literal with the runtime's ordinal IndexOf instead of
        // testing every term at every character (slow for thousands of common-prefix terms).
        // Keeping one longest candidate per offset also avoids buffering every overlapping hit.
        var longestAt = new GlossaryTerm?[text.Length];
        foreach (var term in Terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = 0;
            while (start < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = text.IndexOf(term.Source, start, _comparison);
                if (offset < 0) break;
                if ((MatchMode == GlossaryMatchMode.Literal || HasWordBoundaries(text, offset, term.Source.Length)) &&
                    (longestAt[offset] is not { } prior || term.Source.Length > prior.Source.Length))
                    longestAt[offset] = term;
                start = offset + 1; // Include overlapping occurrences of this literal.
            }
        }
        var matches = new List<GlossaryMatch>();
        for (var position = 0; position < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (longestAt[position] is not { } term) { position++; continue; }
            var source = text.Substring(position, term.Source.Length);
            matches.Add(new GlossaryMatch(term, position, term.Source.Length, source, term.PreserveSource ? source : term.Target));
            position += term.Source.Length;
        }
        return Array.AsReadOnly(matches.ToArray());
    }

    private static bool HasWordBoundaries(string text, int position, int length)
    {
        if (position > 0)
        {
            var before = position - 1;
            if (char.IsLowSurrogate(text[before])) before--;
            if (IsIdentifierPart(Rune.GetRuneAt(text, before))) return false;
        }
        return position + length == text.Length || !IsIdentifierPart(Rune.GetRuneAt(text, position + length));
    }

    private static bool IsIdentifierPart(Rune rune)
    {
        // Han text has no space-delimited word boundaries. This is literal phrase matching, not segmentation.
        var value = rune.Value;
        if (value == 0x3007 || value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF) return false;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
            UnicodeCategory.ConnectorPunctuation;
    }

    /// <summary>One validation path shared by CSV import and future in-app edits. It never discards duplicate rows.</summary>
    internal sealed class Builder
    {
        private readonly GlossaryInfo _info;
        private readonly bool _caseSensitive;
        private readonly GlossaryMatchMode _mode;
        private readonly List<GlossaryTerm> _terms = new();
        private readonly Dictionary<string, (GlossaryTerm Term, int Record)> _seen;
        private int _totalChars;

        internal Builder(GlossaryInfo info, bool caseSensitive, GlossaryMatchMode mode)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            _info = info.Normalize();
            _caseSensitive = caseSensitive;
            _mode = mode;
            _seen = new Dictionary<string, (GlossaryTerm, int)>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        }

        internal void Add(GlossaryTerm term, int record, int line, int sourceColumn = 1, int targetColumn = 2, int noteColumn = 3)
        {
            ArgumentNullException.ThrowIfNull(term);
            LexiconImportException Error(LexiconErrorCode code, int column) => new(code, record, line, column);
            if (_terms.Count >= MaxTerms) throw Error(LexiconErrorCode.TooManyTerms, sourceColumn);
            if (string.IsNullOrWhiteSpace(term.Source)) throw Error(LexiconErrorCode.MissingSourceTerm, sourceColumn);
            if (term.Source.Length > MaxSourceChars) throw Error(LexiconErrorCode.FieldTooLong, sourceColumn);
            if (!LexiconText.IsValid(term.Source, false) || LexiconText.HasOuterWhitespace(term.Source))
                throw Error(LexiconErrorCode.InvalidTerm, sourceColumn);
            if (term.Target is null || !term.PreserveSource && string.IsNullOrWhiteSpace(term.Target))
                throw Error(LexiconErrorCode.MissingTargetTerm, targetColumn);
            if (term.Target.Length > MaxTargetChars) throw Error(LexiconErrorCode.FieldTooLong, targetColumn);
            if (!LexiconText.IsValid(term.Target, false) || LexiconText.HasOuterWhitespace(term.Target) ||
                term.PreserveSource && term.Target.Length > 0 && term.Target != term.Source)
                throw Error(LexiconErrorCode.InvalidTerm, targetColumn);
            if (term.Note is null) throw Error(LexiconErrorCode.InvalidTerm, noteColumn);
            if (term.Note.Length > MaxNoteChars) throw Error(LexiconErrorCode.FieldTooLong, noteColumn);
            if (!LexiconText.IsValid(term.Note, true)) throw Error(LexiconErrorCode.InvalidTerm, noteColumn);
            var size = term.Source.Length + term.Target.Length + term.Note.Length;
            if (size > MaxTotalChars - _totalChars) throw Error(LexiconErrorCode.GlossaryTooLarge, sourceColumn);
            if (_seen.TryGetValue(term.Source, out var prior))
            {
                var same = term.Target == prior.Term.Target && term.PreserveSource == prior.Term.PreserveSource;
                throw new LexiconImportException(same ? LexiconErrorCode.DuplicateTerm : LexiconErrorCode.ConflictingTerm,
                    record, line, sourceColumn, prior.Record);
            }
            _seen.Add(term.Source, (term, record));
            _terms.Add(term);
            _totalChars += size;
        }

        internal Glossary Build()
        {
            if (_terms.Count == 0) throw new LexiconImportException(LexiconErrorCode.MissingData);
            return new Glossary(_info, _caseSensitive, _mode, _terms);
        }
    }
}
