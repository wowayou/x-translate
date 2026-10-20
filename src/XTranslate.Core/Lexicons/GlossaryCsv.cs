namespace XTranslate.Core.Lexicons;

/// <summary>Zero-based column positions. A missing optional column is deliberate, not a guessed empty value.</summary>
public sealed record GlossaryColumnMap(int Source = 0, int Target = 1, int? Note = null, int? PreserveSource = null);

public sealed record GlossaryCsvOptions(
    DelimitedTextFormat Format = DelimitedTextFormat.Csv,
    bool HasHeader = false,
    GlossaryColumnMap? Columns = null,
    bool CaseSensitive = false,
    GlossaryMatchMode MatchMode = GlossaryMatchMode.WholeWord);

public static class GlossaryCsv
{
    public const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Fully reads/validates one glossary before returning an immutable snapshot. No writes or network activity.
    /// Two-column files have no header unless explicitly selected. Extra/missing fields never silently disappear.
    /// With a header, only explicitly mapped columns are imported; every declared column is still size-bounded.
    /// </summary>
    public static async Task<Glossary> ImportAsync(Stream input, GlossaryInfo info, GlossaryCsvOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new GlossaryCsvOptions();
        var columns = options.Columns ?? new GlossaryColumnMap();
        var selected = new[] { (int?)columns.Source, columns.Target, columns.Note, columns.PreserveSource }
            .Where(column => column.HasValue).Select(column => column!.Value).ToArray();
        if (selected.Any(column => column is < 0 or >= 64) || selected.Distinct().Count() != selected.Length)
            throw new LexiconImportException(LexiconErrorCode.InvalidColumnMapping);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new Glossary.Builder(info, options.CaseSensitive, options.MatchMode);
        var requiredColumns = selected.Max() + 1;
        var fieldCount = requiredColumns;
        var sawRecord = false;
        var limits = new DelimitedTextLimits(MaxBytes: MaxFileBytes, MaxRecords: Glossary.MaxTerms + (options.HasHeader ? 1 : 0));
        await foreach (var row in DelimitedText.ReadAsync(input, options.Format, limits, cancellationToken).ConfigureAwait(false))
        {
            if (!sawRecord && options.HasHeader)
            {
                LexiconText.ValidateHeader(row);
                if (row.Fields.Count < requiredColumns)
                    throw new LexiconImportException(LexiconErrorCode.InvalidColumnMapping, row.RecordNumber, row.LineNumber);
                fieldCount = row.Fields.Count;
                sawRecord = true;
                continue;
            }
            sawRecord = true;
            if (row.Fields.Count != fieldCount)
                throw new LexiconImportException(LexiconErrorCode.WrongColumnCount, row.RecordNumber, row.LineNumber);
            var preserve = false;
            if (columns.PreserveSource is int flag)
            {
                var value = row.Fields[flag];
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) preserve = true;
                else if (!value.Equals("false", StringComparison.OrdinalIgnoreCase))
                    throw new LexiconImportException(LexiconErrorCode.InvalidBoolean, row.RecordNumber, row.LineNumber, flag + 1);
            }
            builder.Add(new GlossaryTerm(row.Fields[columns.Source], row.Fields[columns.Target],
                columns.Note is int note ? row.Fields[note] : "", preserve), row.RecordNumber, row.LineNumber,
                columns.Source + 1, columns.Target + 1, columns.Note is int noteColumn ? noteColumn + 1 : 0);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!sawRecord) throw new LexiconImportException(LexiconErrorCode.EmptyInput);
        return builder.Build();
    }
}
