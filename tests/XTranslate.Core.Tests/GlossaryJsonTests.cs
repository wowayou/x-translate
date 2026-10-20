using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XTranslate.Core.Lexicons;
using static DelimitedTextTests;

internal static class GlossaryJsonTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("v2 glossary JSON preserves provenance/revision without changing the v1 contract", RoundTrip),
        ("Glossary JSON explicitly migrates v1 without inventing attribution or identity", Legacy),
        ("Glossary JSON rejects unknown/duplicate/missing fields and unknown versions", Structure),
        ("Glossary JSON uses the existing term, language, and metadata validators", Semantics),
        ("Lexicon attribution and revision limits reject malformed identities and timestamps", Metadata),
        ("Glossary JSON bounds bytes/depth and preserves cancellation and stream ownership", Boundaries),
        ("v2 Markdown escapes attribution and records the actual revision", Markdown),
        ("Published v2 example round trips through the production reader and exporter", Example)
    };

    private static GlossaryDocument Fixture() => new(Glossary.Create(new("Fixture", "Development", "en", "zh-CN"),
        new[] { new GlossaryTerm("thread", "线程", "note 😀"), new("API", "", "Keep source", true) }),
        new("User source", "https://example.test/reference", "", "", "line1\nline2 <tag>", "edition-1"),
        new("00000000000000000000000000000001", 7, new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), new string('a', 64), "Local edit"));

    private static async Task<byte[]> Bytes(GlossaryDocument document)
    {
        using var output = new MemoryStream();
        await GlossaryExport.WriteJsonAsync(output, document);
        return output.ToArray();
    }

    private static async Task RoundTrip()
    {
        var original = Fixture();
        using var input = new ProbeStream(await Bytes(original), 1);
        var read = await GlossaryJson.ReadAsync(input);
        Check(!input.Disposed && read.Glossary.Info == original.Glossary.Info && read.Glossary.Terms.SequenceEqual(original.Glossary.Terms) &&
            read.Provenance == original.Provenance && read.Revision == original.Revision, "v2 data/ownership drifted");
        var actualBytes = await Bytes(read);
        var expectedBytes = await Bytes(original);
        Check(actualBytes.SequenceEqual(expectedBytes), "v2 export is not deterministic");
        using var legacy = new MemoryStream();
        await GlossaryExport.WriteJsonAsync(legacy, original.Glossary);
        using var parsed = JsonDocument.Parse(legacy.ToArray());
        Check(parsed.RootElement.GetProperty("schemaVersion").GetInt32() == 1 && !parsed.RootElement.TryGetProperty("provenance", out _), "Existing v1 overload changed");
    }

    private static async Task Legacy()
    {
        using var output = new MemoryStream();
        await GlossaryExport.WriteJsonAsync(output, Fixture().Glossary);
        output.Position = 0;
        var read = await GlossaryJson.ReadAsync(output);
        Check(read.Provenance == LexiconProvenance.Unspecified && read.Revision is null, "v1 import fabricated metadata");
        using var bom = new ProbeStream(new byte[] { 0xef, 0xbb, 0xbf }.Concat(await Bytes(read)).ToArray(), 1);
        Check((await GlossaryJson.ReadAsync(bom)).Revision is null, "BOM-framed v2 import drifted");
    }

    private static async Task Structure()
    {
        var good = Encoding.UTF8.GetString(await Bytes(Fixture()));
        var bad = new List<string> { "null", "[]", "", good + "{}", good.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 2, \"schemaVersion\": 2"),
            good.Replace("\"caseSensitive\": false", "\"caseSensitive\": false, \"caseSensitive\": false"),
            good.Replace("\"name\":", "\"unexpected\":") };
        foreach (var field in new[] { "provenance", "revision", "name", "matching", "terms" })
        {
            var node = JsonNode.Parse(good)!.AsObject(); node.Remove(field); bad.Add(node.ToJsonString());
        }
        foreach (var value in bad)
        {
            using var input = Input(value);
            var error = await ThrowsAsync<LexiconImportException>(GlossaryJson.ReadAsync(input));
            Check(error.Code == LexiconErrorCode.InvalidJson && error.InnerException is null && !error.ToString().Contains("User source"), "Invalid shape was accepted/leaked");
        }
        using var future = Input(good.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 999"));
        Check((await ThrowsAsync<LexiconImportException>(GlossaryJson.ReadAsync(future))).Code == LexiconErrorCode.UnsupportedSchemaVersion, "Future schema was guessed");
    }

    private static async Task Semantics()
    {
        var cases = new (Action<JsonNode> Change, LexiconErrorCode Code)[]
        {
            (node => node["sourceLanguage"] = "auto", LexiconErrorCode.InvalidLanguage),
            (node => node["terms"]![0]!["source"] = "thread ", LexiconErrorCode.InvalidTerm),
            (node => node["terms"]![0]!["target"] = "", LexiconErrorCode.MissingTargetTerm),
            (node => node["terms"]![1]!["target"] = "changed", LexiconErrorCode.InvalidTerm),
            (node => node["terms"]!.AsArray().Add(node["terms"]![0]!.DeepClone()), LexiconErrorCode.DuplicateTerm),
            (node => node["terms"]![0]!["note"] = "secret\u001bmarker", LexiconErrorCode.InvalidTerm),
            (node => node["terms"]![0]!["source"] = new string('a', Glossary.MaxSourceChars + 1), LexiconErrorCode.FieldTooLong),
            (node => node["provenance"]!["license"] = new string('x', 257), LexiconErrorCode.InvalidMetadata),
            (node => node["revision"]!["resourceId"] = "../../private_marker", LexiconErrorCode.InvalidMetadata),
            (node => node["revision"]!["number"] = 0, LexiconErrorCode.InvalidMetadata),
            (node => node["revision"]!["createdUtc"] = "2026-10-16T01:00:00+01:00", LexiconErrorCode.InvalidMetadata)
        };
        foreach (var (change, code) in cases)
        {
            var node = JsonNode.Parse(await Bytes(Fixture()))!; change(node);
            using var input = Input(node.ToJsonString());
            var error = await ThrowsAsync<LexiconImportException>(GlossaryJson.ReadAsync(input));
            Check(error.Code == code && !error.ToString().Contains("private_marker"), $"Wrong semantic failure: {error.Code}, expected {code}");
        }
        foreach (var badString in new[] { "\\uD800", "\\uDC00" })
        {
            var good = Encoding.UTF8.GetString(await Bytes(Fixture())).Replace("thread", badString);
            using var input = Input(good);
            await ThrowsAsync<LexiconImportException>(GlossaryJson.ReadAsync(input));
        }
    }

    private static Task Metadata()
    {
        var revision = Fixture().Revision!;
        foreach (var bad in new[] { revision with { Number = 0 }, revision with { Number = LexiconRevision.MaxNumber + 1 },
            revision with { ResourceId = "../escape" }, revision with { ResourceId = new string('A', 32) },
            revision with { SourceSha256 = "not-a-hash" }, revision with { CreatedUtc = default }, revision with { ChangeNote = new string('x', 2049) } })
            Throws<LexiconImportException>(bad.Validate);
        foreach (var bad in new[] { new LexiconProvenance(SourceName: " space"), new(Attribution: "\u001b"), new(SourceUri: new string('x', 2049)), new(License: null!) })
            Throws<LexiconImportException>(bad.Validate);
        (revision with { Number = LexiconRevision.MaxNumber, ChangeNote = new string('x', 2048) }).Validate();
        new LexiconProvenance(SourceName: new string('x', 256), Attribution: new string('x', 4096)).Validate();
        Check(new DictionaryInfo("Fixture", "EN", "zh-cn").Normalize().SourceLanguage == "en", "Dictionary language normalization drifted");
        Throws<LexiconImportException>(() => new DictionaryInfo(" Fixture", "en", "zh-CN").Normalize());
        return Task.CompletedTask;
    }

    private static async Task Boundaries()
    {
        var good = await Bytes(Fixture());
        var exact = new byte[GlossaryJson.MaxFileBytes];
        good.CopyTo(exact, 0); Array.Fill(exact, (byte)' ', good.Length, exact.Length - good.Length);
        using var atLimit = new MemoryStream(exact);
        await GlossaryJson.ReadAsync(atLimit);
        using var tooLarge = new ProbeStream(exact.Concat(new byte[] { 32 }).ToArray());
        Check((await ThrowsAsync<LexiconImportException>(GlossaryJson.ReadAsync(tooLarge))).Code == LexiconErrorCode.InputTooLarge &&
            tooLarge.BytesRead == GlossaryJson.MaxFileBytes + 1 && !tooLarge.Disposed, "JSON byte cap/ownership failed");
        using var deep = Input(new string('[', 17) + "0" + new string(']', 17));
        await ThrowsAsync<LexiconImportException>(GlossaryJson.ReadAsync(deep));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var untouched = new ProbeStream(good);
        await ThrowsAsync<OperationCanceledException>(GlossaryJson.ReadAsync(untouched, cancelled.Token));
        Check(untouched.ReadCalls == 0 && !untouched.Disposed, "Pre-cancelled JSON read touched input");
        using var during = new CancellationTokenSource();
        using var blocked = new BlockedInput();
        var read = GlossaryJson.ReadAsync(blocked, during.Token);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); during.Cancel();
        await ThrowsAsync<OperationCanceledException>(read.WaitAsync(TimeSpan.FromSeconds(5)));
        Check(!blocked.Disposed, "Cancellation closed caller JSON stream");
        using var invalid = new MemoryStream(new byte[] { 0xff, 0xfe, 0x7b, 0 });
        await ThrowsAsync<LexiconImportException>(GlossaryJson.ReadAsync(invalid));
    }

    private static async Task Markdown()
    {
        var original = Fixture();
        var snapshot = new GlossaryDocument(original.Glossary, original.Provenance with { Attribution = "![x](https://example.test) | <script>\n# data" },
            original.Revision! with { ChangeNote = "<tag>|\nline2" });
        using var stream = new MemoryStream();
        await GlossaryExport.WriteMarkdownAsync(stream, snapshot);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Check(!text.Contains("<script>") && !text.Contains("![x]") && text.Contains("&lt;tag&gt;&#124;<br>line2") &&
            text.Contains(original.Revision!.ResourceId) && text.Contains("not verified permission"), "v2 metadata lost or active Markdown was emitted");
    }

    private static async Task Example()
    {
        using var example = Assembly.GetExecutingAssembly().GetManifestResourceStream("Glossary.ResourceExample.json")!;
        var read = await GlossaryJson.ReadAsync(example);
        using var output = new MemoryStream(await Bytes(read));
        var reread = await GlossaryJson.ReadAsync(output);
        Check(reread.Revision == read.Revision && reread.Provenance == read.Provenance && reread.Glossary.Terms.SequenceEqual(read.Glossary.Terms),
            "v2 example does not round trip");
    }

    private sealed class BlockedInput : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
