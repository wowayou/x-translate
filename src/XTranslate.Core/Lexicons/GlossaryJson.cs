using System.Text.Json;

namespace XTranslate.Core.Lexicons;

/// <summary>Strict, bounded v1/v2 reader. No file selection, implicit defaults for unknown fields, or network access.</summary>
public static class GlossaryJson
{
    public const int MaxFileBytes = 16 * 1024 * 1024;

    public static async Task<GlossaryDocument> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new ArgumentException("A readable stream is required.", nameof(input));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var bounded = new BoundedInput(input);
            using var json = await JsonDocument.ParseAsync(bounded, new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var versionElement) ||
                !versionElement.TryGetInt32(out var version)) throw Invalid();
            if (version is not (1 or 2)) throw new LexiconImportException(LexiconErrorCode.UnsupportedSchemaVersion);
            var fields = new[] { "schemaVersion", "kind", "name", "domain", "sourceLanguage", "targetLanguage", "matching", "terms" };
            Object(root, version == 1 ? fields : fields.Concat(new[] { "provenance", "revision" }).ToArray());
            if (Text(root, "kind") != GlossaryExport.Kind) throw Invalid();
            var info = new GlossaryInfo(Text(root, "name"), Text(root, "domain"), Text(root, "sourceLanguage"), Text(root, "targetLanguage"));
            var matching = root.GetProperty("matching");
            Object(matching, "mode", "caseSensitive");
            var mode = Text(matching, "mode") switch
            {
                "wholeWord" => GlossaryMatchMode.WholeWord, "literal" => GlossaryMatchMode.Literal, _ => throw Invalid()
            };
            var builder = new Glossary.Builder(info, Boolean(matching, "caseSensitive"), mode);
            var terms = root.GetProperty("terms");
            if (terms.ValueKind != JsonValueKind.Array) throw Invalid();
            if (terms.GetArrayLength() > Glossary.MaxTerms) throw new LexiconImportException(LexiconErrorCode.TooManyTerms);
            var row = 0;
            foreach (var term in terms.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Object(term, "source", "target", "preserveSource", "note");
                builder.Add(new GlossaryTerm(Text(term, "source"), Text(term, "target"), Text(term, "note"), Boolean(term, "preserveSource")),
                    ++row, 0);
            }
            var provenance = LexiconProvenance.Unspecified;
            LexiconRevision? revision = null;
            if (version == 2)
            {
                var p = root.GetProperty("provenance");
                Object(p, "sourceName", "sourceUri", "license", "licenseUri", "attribution", "sourceVersion");
                provenance = new(Text(p, "sourceName"), Text(p, "sourceUri"), Text(p, "license"), Text(p, "licenseUri"),
                    Text(p, "attribution"), Text(p, "sourceVersion"));
                var r = root.GetProperty("revision");
                if (r.ValueKind != JsonValueKind.Null)
                {
                    Object(r, "resourceId", "number", "createdUtc", "sourceSha256", "changeNote");
                    if (!r.GetProperty("number").TryGetInt32(out var number) || !r.GetProperty("createdUtc").TryGetDateTimeOffset(out var created))
                        throw Invalid();
                    revision = new(Text(r, "resourceId"), number, created, Text(r, "sourceSha256"), Text(r, "changeNote"));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new GlossaryDocument(builder.Build(), provenance, revision);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Do not retain the parser's exception: it can quote imported values or JSON paths.
            throw Invalid();
        }
    }

    private static LexiconImportException Invalid() => new(LexiconErrorCode.InvalidJson);
    private static string Text(JsonElement value, string field) => value.GetProperty(field).ValueKind == JsonValueKind.String
        ? value.GetProperty(field).GetString()! : throw Invalid();
    private static bool Boolean(JsonElement value, string field) => value.GetProperty(field).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid()
    };

    private static void Object(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw Invalid(); // Unknown and duplicate keys are both errors.
        if (remaining.Count != 0) throw Invalid();
    }

    private sealed class BoundedInput(Stream input) : Stream
    {
        private int _remaining = MaxFileBytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining + 1)], cancellationToken).ConfigureAwait(false);
            if (read > _remaining) throw new LexiconImportException(LexiconErrorCode.InputTooLarge);
            _remaining -= read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        // Disposing the wrapper never disposes the caller's stream.
    }
}
