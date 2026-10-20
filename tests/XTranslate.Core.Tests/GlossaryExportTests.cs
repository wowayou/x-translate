using System.Reflection;
using System.Text;
using System.Text.Json;
using XTranslate.Core.Lexicons;
using static DelimitedTextTests;

internal static class GlossaryExportTests
{
    private static readonly GlossaryInfo Info = new("Software \"reference\"", "Development", "en", "zh-CN");
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Versioned JSON export losslessly preserves the core glossary model for other consumers", JsonRoundTrip),
        ("Markdown export escapes structure and labels imported notes as reference data", Markdown),
        ("CSV/TSV exports round trip their two-column data and expose preserve-rule loss", Interchange),
        ("Spreadsheet formula-like cells are rejected before any output without explicit raw opt-in", FormulaSafety),
        ("Glossary export leaves destinations open and propagates cancellation and I/O failures", OwnershipAndFailures),
        ("Published JSON schema and example agree with the versioned export fields", SchemaAndExample)
    };

    private static Glossary Fixture() => Glossary.Create(Info, new[]
    {
        new GlossaryTerm("thread", "线程", "line1\r\nline2 😀"),
        new GlossaryTerm("API", "", "Keep the exact input", PreserveSource: true),
        new GlossaryTerm("a\"b,c", "quoted \"译法\", value", "literal symbols | & < >")
    }, caseSensitive: true, matchMode: GlossaryMatchMode.Literal);

    private static async Task JsonRoundTrip()
    {
        var table = Fixture();
        using var output = new MemoryStream();
        await GlossaryExport.WriteJsonAsync(output, table);
        Check(output.CanWrite && !output.ToArray().AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "JSON closed output or added a BOM");
        using var document = JsonDocument.Parse(output.ToArray());
        var root = document.RootElement;
        Check(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("kind").GetString() == "xtranslate.glossary", "Missing interchange discriminator/version");
        var restored = Reconstruct(root);
        Check(restored.Info == table.Info && restored.CaseSensitive == table.CaseSensitive && restored.MatchMode == table.MatchMode &&
            restored.Terms.SequenceEqual(table.Terms) && restored.FindMatches("thread API a\"b,c").SequenceEqual(table.FindMatches("thread API a\"b,c")),
            "JSON export lost data or matching semantics");
        using var second = new MemoryStream();
        await GlossaryExport.WriteJsonAsync(second, table);
        Check(output.ToArray().SequenceEqual(second.ToArray()), "Export contains an unstable timestamp or generated identifier");
        Check(root.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[]
            { "schemaVersion", "kind", "name", "domain", "sourceLanguage", "targetLanguage", "matching", "terms" }.Order()),
            "Export unexpectedly included application configuration or omitted metadata");
    }

    private static async Task Markdown()
    {
        var table = Glossary.Create(Info with { Name = "[title](https://example.test) | <img>" }, new[]
        {
            new GlossaryTerm("API", "", "| ![image](https://example.test/x)\n```\n<script>ignore</script> & **text**", true),
            new GlossaryTerm("C#", "C#", "line1\r\nline2\rline3\tend")
        });
        using var output = new MemoryStream();
        await GlossaryExport.WriteMarkdownAsync(output, table);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Check(text.Contains("Reference data only") && text.Contains("not instructions") && text.Contains("Keep actual source spelling/case"),
            "Markdown hides the data-only or keep-original contract");
        Check(!text.Contains("<script>") && !text.Contains("<img>") && !text.Contains("![image]") && !text.Contains("\n```") &&
            text.Contains("&#124;") && text.Contains("&lt;script&gt;") && text.Contains("line1<br>line2<br>line3"),
            "Imported text can break Markdown structure or become active HTML/images");
        var rows = text.Split('\n').Where(line => line.StartsWith("| ", StringComparison.Ordinal)).ToArray();
        Check(rows.Length == table.Terms.Count + 2 && rows.All(line => line.Split('|').Length == 6), "Embedded pipe/newline corrupted the table");
        Check(output.CanWrite, "Markdown export closed its destination");
    }

    private static async Task Interchange()
    {
        var table = Fixture();
        foreach (var format in new[] { DelimitedTextFormat.Csv, DelimitedTextFormat.Tsv })
        {
            using var output = new MemoryStream();
            await GlossaryExport.WriteInterchangeAsync(output, table, format);
            var bytes = output.ToArray();
            Check(!bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) && output.CanWrite, "Interchange added BOM or closed output");
            output.Position = 0;
            var restored = await GlossaryCsv.ImportAsync(output, Info, new(Format: format, CaseSensitive: true, MatchMode: GlossaryMatchMode.Literal));
            Check(restored.Terms.Select(term => term.Source).SequenceEqual(table.Terms.Select(term => term.Source)) &&
                restored.Terms.Select(term => term.Target).SequenceEqual(table.Terms.Select(term => term.PreserveSource ? term.Source : term.Target)),
                "Quoted interchange fields do not round trip through the declared dialect");
            Check(restored.Terms.All(term => term.Note == "" && !term.PreserveSource), "Two-column interchange pretended to preserve unavailable metadata");
        }
    }

    private static async Task FormulaSafety()
    {
        foreach (var prefix in new[] { "=", "+", "-", "@", "\uFEFF=", "\u200B+", "＝", "＋", "－", "＠" })
        {
            var table = Glossary.Create(Info, new[] { new GlossaryTerm("safe", "normal"), new(prefix + "private_marker", "x") });
            foreach (var format in new[] { DelimitedTextFormat.Csv, DelimitedTextFormat.Tsv })
            {
                using var destination = new MemoryStream();
                var error = await ThrowsAsync<GlossaryExportException>(GlossaryExport.WriteInterchangeAsync(destination, table, format));
                Check(destination.Length == 0 && error.RecordNumber == 2 && error.ColumnNumber == 1 && !error.ToString().Contains("private_marker"),
                    "Refusal wrote partial data, lost location or exposed a formula");
            }
        }
        var target = Glossary.Create(Info, new[] { new GlossaryTerm("safe", "=private_marker") });
        using var targetOutput = new MemoryStream();
        Check((await ThrowsAsync<GlossaryExportException>(GlossaryExport.WriteInterchangeAsync(targetOutput, target))).ColumnNumber == 2,
            "Only source cells were checked");
        using var raw = new MemoryStream();
        await GlossaryExport.WriteInterchangeAsync(raw, target, allowFormulaLikeCells: true);
        raw.Position = 0;
        var restored = await GlossaryCsv.ImportAsync(raw, Info);
        Check(restored.Terms.Single().Target == "=private_marker", "Explicit raw export silently prefixed or changed a term");
        using var json = new MemoryStream();
        await GlossaryExport.WriteJsonAsync(json, target);
        using var safeJson = JsonDocument.Parse(json.ToArray());
        Check(Reconstruct(safeJson.RootElement).Terms.Single() == target.Terms.Single(), "Safe JSON unnecessarily changed formula-like data");
    }

    private static async Task OwnershipAndFailures()
    {
        var table = Fixture();
        Func<Stream, CancellationToken, Task>[] writers =
        {
            (stream, token) => GlossaryExport.WriteJsonAsync(stream, table, token),
            (stream, token) => GlossaryExport.WriteMarkdownAsync(stream, table, token),
            (stream, token) => GlossaryExport.WriteInterchangeAsync(stream, table, cancellationToken: token)
        };
        foreach (var write in writers)
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            using var untouched = new MemoryStream();
            await ThrowsAsync<OperationCanceledException>(write(untouched, cancelled.Token));
            Check(untouched.Length == 0 && untouched.CanWrite, "Pre-cancelled export touched destination");
            using var during = new CancellationTokenSource();
            using var blocked = new ProbeOutput(block: true);
            var exporting = write(blocked, during.Token);
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            during.Cancel();
            await ThrowsAsync<OperationCanceledException>(exporting.WaitAsync(TimeSpan.FromSeconds(5)));
            Check(blocked.WriteCalls == 1 && blocked.SyncWrites == 0 && !blocked.Disposed, "Cancellation caused hidden flush/retry or closed output");
            using var failed = new ProbeOutput(block: false);
            await ThrowsAsync<IOException>(write(failed, CancellationToken.None));
            Check(!failed.Disposed && failed.WriteCalls == 1, "I/O error was swallowed/retried or closed output");
        }
    }

    private static async Task SchemaAndExample()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var schemaStream = assembly.GetManifestResourceStream("Glossary.Schema.json") ?? throw new Exception("Schema resource missing");
        using var schema = JsonDocument.Parse(schemaStream);
        using var exampleStream = assembly.GetManifestResourceStream("Glossary.Example.json") ?? throw new Exception("Example resource missing");
        using var example = JsonDocument.Parse(exampleStream);
        Check(schema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == GlossaryExport.SchemaVersion &&
            schema.RootElement.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString() == GlossaryExport.Kind,
            "Schema version/kind drifted from the exporter");
        var reconstructed = Reconstruct(example.RootElement);
        using var output = new MemoryStream();
        await GlossaryExport.WriteJsonAsync(output, reconstructed);
        using var exported = JsonDocument.Parse(output.ToArray());
        Check(JsonElement.DeepEquals(example.RootElement, exported.RootElement), "Documented example cannot be emitted by the exporter");
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet();
        Check(exported.RootElement.EnumerateObject().All(property => required.Remove(property.Name)) && required.Count == 0,
            "Exported top-level fields disagree with the schema contract");
        // Full JSON Schema validation belongs to consumers/tooling; business rules are validated by Glossary.Create.
    }

    private static Glossary Reconstruct(JsonElement root)
    {
        var info = new GlossaryInfo(root.GetProperty("name").GetString()!, root.GetProperty("domain").GetString()!,
            root.GetProperty("sourceLanguage").GetString()!, root.GetProperty("targetLanguage").GetString()!);
        var terms = root.GetProperty("terms").EnumerateArray().Select(term => new GlossaryTerm(term.GetProperty("source").GetString()!,
            term.GetProperty("target").GetString()!, term.GetProperty("note").GetString()!, term.GetProperty("preserveSource").GetBoolean()));
        var matching = root.GetProperty("matching");
        return Glossary.Create(info, terms, matching.GetProperty("caseSensitive").GetBoolean(),
            matching.GetProperty("mode").GetString() == "literal" ? GlossaryMatchMode.Literal : GlossaryMatchMode.WholeWord);
    }

    private sealed class ProbeOutput(bool block) : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCalls { get; private set; }
        public int SyncWrites { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            Started.TrySetResult();
            if (block) await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new IOException("Fixed export write failure");
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Write(byte[] buffer, int offset, int count) { SyncWrites++; throw new IOException("Unexpected synchronous write"); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
