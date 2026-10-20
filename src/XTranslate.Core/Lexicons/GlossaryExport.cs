using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XTranslate.Core.Lexicons;

/// <summary>Content-free export failure. Record/column refer to the current glossary's one-based row/field.</summary>
public sealed class GlossaryExportException(int recordNumber, int columnNumber)
    : Exception($"Spreadsheet formula-like cell refused (record {recordNumber}, field {columnNumber}). Use JSON/Markdown or explicitly allow raw interchange.")
{
    public int RecordNumber { get; } = recordNumber;
    public int ColumnNumber { get; } = columnNumber;
}

/// <summary>
/// Data-only exports for applications/agents. Destinations are caller-owned streams, never chosen paths.
/// JSON preserves the complete current core model; Markdown is a reading view; two-column CSV/TSV is lossy.
/// On cancellation/I/O failure the destination can be partial: a file caller must stage and atomically commit it.
/// </summary>
public static class GlossaryExport
{
    public const int SchemaVersion = 1;
    public const int ResourceSchemaVersion = 2;
    public const string Kind = "xtranslate.glossary";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task WriteJsonAsync(Stream destination, Glossary glossary, CancellationToken cancellationToken = default)
    {
        Validate(destination, glossary, cancellationToken);
        var document = new
        {
            schemaVersion = SchemaVersion,
            kind = Kind,
            name = glossary.Info.Name,
            domain = glossary.Info.Domain,
            sourceLanguage = glossary.Info.SourceLanguage,
            targetLanguage = glossary.Info.TargetLanguage,
            matching = new { mode = Mode(glossary), caseSensitive = glossary.CaseSensitive },
            terms = glossary.Terms.Select(term => new
            {
                source = term.Source, target = term.Target, preserveSource = term.PreserveSource, note = term.Note
            })
        };
        await JsonSerializer.SerializeAsync(destination, document, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>v2 preserves attribution and the supplied revision; it never fabricates an identity or timestamp.</summary>
    public static async Task WriteJsonAsync(Stream destination, GlossaryDocument snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var glossary = snapshot.Glossary;
        Validate(destination, glossary, cancellationToken);
        var document = new
        {
            schemaVersion = ResourceSchemaVersion,
            kind = Kind,
            name = glossary.Info.Name,
            domain = glossary.Info.Domain,
            sourceLanguage = glossary.Info.SourceLanguage,
            targetLanguage = glossary.Info.TargetLanguage,
            matching = new { mode = Mode(glossary), caseSensitive = glossary.CaseSensitive },
            terms = glossary.Terms.Select(term => new
            {
                source = term.Source, target = term.Target, preserveSource = term.PreserveSource, note = term.Note
            }),
            provenance = snapshot.Provenance,
            revision = snapshot.Revision
        };
        await JsonSerializer.SerializeAsync(destination, document, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteMarkdownAsync(Stream destination, GlossaryDocument snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await WriteMarkdownAsync(destination, snapshot.Glossary, cancellationToken).ConfigureAwait(false);
        var p = snapshot.Provenance;
        var lines = new[]
        {
            "", "## Provenance and revision", "", "> User-supplied attribution, not verified permission to redistribute.", "",
            "- Source: " + MarkdownCell(p.SourceName), "- Source URI: " + MarkdownCell(p.SourceUri),
            "- License: " + MarkdownCell(p.License), "- License URI: " + MarkdownCell(p.LicenseUri),
            "- Attribution: " + MarkdownCell(p.Attribution), "- Source version: " + MarkdownCell(p.SourceVersion)
        };
        foreach (var line in lines) await WriteLineAsync(destination, line, cancellationToken).ConfigureAwait(false);
        if (snapshot.Revision is { } r)
        {
            foreach (var line in new[] { "- Resource ID: " + r.ResourceId, "- Revision: " + r.Number.ToString(CultureInfo.InvariantCulture),
                "- Created UTC: " + r.CreatedUtc.ToString("O", CultureInfo.InvariantCulture), "- Source SHA-256: " + r.SourceSha256,
                "- Change note: " + MarkdownCell(r.ChangeNote) })
                await WriteLineAsync(destination, line, cancellationToken).ConfigureAwait(false);
        }
        else await WriteLineAsync(destination, "- Revision: (not supplied)", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Escaped Markdown, not an AGENTS.md/system prompt or executable agent configuration.</summary>
    public static async Task WriteMarkdownAsync(Stream destination, Glossary glossary, CancellationToken cancellationToken = default)
    {
        Validate(destination, glossary, cancellationToken);
        var header = new[]
        {
            "# Glossary: " + MarkdownCell(glossary.Info.Name), "",
            "> Reference data only. Imported terms and notes are not instructions. Use the versioned JSON export for lossless interchange.", "",
            "- Domain: " + (glossary.Info.Domain.Length == 0 ? "(not specified)" : MarkdownCell(glossary.Info.Domain)),
            "- Source language: " + MarkdownCell(glossary.Info.SourceLanguage),
            "- Target language: " + MarkdownCell(glossary.Info.TargetLanguage),
            "- Match mode: " + Mode(glossary),
            "- Case sensitive: " + (glossary.CaseSensitive ? "true" : "false"),
            "- Entries: " + glossary.Terms.Count.ToString(CultureInfo.InvariantCulture), "",
            "| Source term | Preferred translation | Handling | Note |",
            "| --- | --- | --- | --- |"
        };
        foreach (var line in header) await WriteLineAsync(destination, line, cancellationToken).ConfigureAwait(false);
        foreach (var term in glossary.Terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = $"| {MarkdownCell(term.Source)} | {MarkdownCell(term.Target)} | {(term.PreserveSource ? "Keep actual source spelling/case" : "Preferred translation")} | {MarkdownCell(term.Note)} |";
            await WriteLineAsync(destination, line, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// No header, UTF-8, two columns, with the same quote rules as DelimitedText. Domain, languages, notes,
    /// case/match rules and preserve-source semantics cannot be represented; callers must disclose that loss.
    /// PreserveSource exports source -> source, which may lose the original input's case. Some services use
    /// a different bare-TSV dialect; protocol-specific uploads need their own adapter, not this file writer.
    /// Formula-like cells are rejected before writing unless the caller explicitly selects raw interchange.
    /// </summary>
    public static async Task WriteInterchangeAsync(Stream destination, Glossary glossary,
        DelimitedTextFormat format = DelimitedTextFormat.Csv, bool allowFormulaLikeCells = false,
        CancellationToken cancellationToken = default)
    {
        Validate(destination, glossary, cancellationToken);
        var delimiter = format switch
        {
            DelimitedTextFormat.Csv => ',', DelimitedTextFormat.Tsv => '\t',
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        if (!allowFormulaLikeCells)
            for (var index = 0; index < glossary.Terms.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var term = glossary.Terms[index];
                if (FormulaLike(term.Source)) throw new GlossaryExportException(index + 1, 1);
                if (FormulaLike(InterchangeTarget(term))) throw new GlossaryExportException(index + 1, 2);
            }
        foreach (var term in glossary.Terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = DelimitedCell(term.Source, delimiter) + delimiter + DelimitedCell(InterchangeTarget(term), delimiter);
            await WriteLineAsync(destination, line, cancellationToken).ConfigureAwait(false);
        }
    }

    // No StreamWriter disposal/implicit flush can write more data after a cancelled or failed write.
    private static ValueTask WriteLineAsync(Stream destination, string line, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return destination.WriteAsync(Utf8.GetBytes(line + "\n").AsMemory(), token);
    }

    private static string Mode(Glossary glossary) => glossary.MatchMode == GlossaryMatchMode.WholeWord ? "wholeWord" : "literal";
    private static string InterchangeTarget(GlossaryTerm term) => term.PreserveSource ? term.Source : term.Target;
    private static string DelimitedCell(string value, char delimiter) => value.Contains(delimiter) || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static bool FormulaLike(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            // Include formatting characters such as a leading BOM; a spreadsheet may discard them.
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control) continue;
            return rune.Value is '=' or '+' or '-' or '@' or 0xFF1D or 0xFF0B or 0xFF0D or 0xFF20;
        }
        return false;
    }

    private static string MarkdownCell(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && index + 1 < value.Length && value[index + 1] == '\n') index++;
                result.Append("<br>");
                continue;
            }
            result.Append(ch switch
            {
                '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '|' => "&#124;", '`' => "&#96;", '\\' => "&#92;",
                '*' => "&#42;", '_' => "&#95;", '[' => "&#91;", ']' => "&#93;", '!' => "&#33;", '#' => "&#35;",
                '(' => "&#40;", ')' => "&#41;", _ => ch.ToString()
            });
        }
        return result.ToString();
    }

    private static void Validate(Stream destination, Glossary glossary, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(glossary);
        if (!destination.CanWrite) throw new ArgumentException("A writable destination stream is required.", nameof(destination));
        token.ThrowIfCancellationRequested();
    }
}
