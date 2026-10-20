using System.Text;
using XTranslate.Core.Lexicons;

internal static class DelimitedTextTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("CSV/TSV preserve quoted separators, escaped quotes, Unicode and physical newlines", Quoting),
        ("Delimited records handle all line endings, empty fields and EOF without phantom rows", Endings),
        ("UTF-8 and quoting survive one-byte input frames", Framing),
        ("Malformed quoted records report safe logical/physical positions", Malformed),
        ("Lexicon inputs accept UTF-8 BOM only and reject invalid encodings/control bytes", Encodings),
        ("CSV byte, field, record, column and row budgets apply during reads", Limits),
        ("CSV enumeration leaves caller streams open on success, failure and early disposal", Ownership),
        ("CSV cancellation reaches an in-flight read and does not close the caller stream", Cancellation),
        ("Deterministic generated CSV round trips reproduce every original field", RoundTrips)
    };

    private static async Task Quoting()
    {
        const string csv = "\uFEFFword,meaning,extra\r\n\"a,b\",\"say \"\"hello\"\"\r\nnext\",\r\n词😀,\"<b>plain</b>\",  spaces  ";
        var rows = await Parse(csv);
        Check(rows.Count == 3 && rows[1].RecordNumber == 2 && rows[1].LineNumber == 2 && rows[2].LineNumber == 4, "Record/line tracking failed");
        Check(rows[1].Fields.SequenceEqual(new[] { "a,b", "say \"hello\"\r\nnext", "" }) &&
            rows[2].Fields.SequenceEqual(new[] { "词😀", "<b>plain</b>", "  spaces  " }), "CSV content was normalized or lost");
        using var stream = Input("\"a\tb\"\t\"line1\nline2\"\t\"\"\n");
        var tsv = await Read(stream, DelimitedTextFormat.Tsv);
        Check(tsv.Count == 1 && tsv[0].Fields.SequenceEqual(new[] { "a\tb", "line1\nline2", "" }), "TSV quoting failed");
        Throws<NotSupportedException>(() => ((IList<string>)rows[0].Fields)[0] = "modified");
    }

    private static async Task Endings()
    {
        foreach (var ending in new[] { "\n", "\r", "\r\n" })
        {
            var rows = await Parse("a,b" + ending + "c," + ending);
            Check(rows.Count == 2 && rows[1].Fields.SequenceEqual(new[] { "c", "" }) && rows[1].LineNumber == 2, "Line ending generated a phantom row");
            Check((await Parse(ending)).Single().Fields.SequenceEqual(new[] { "" }), "An explicit blank record was dropped");
        }
        Check((await Parse("\"\"" )).Single().Fields.Single() == "", "Empty quoted final field disappeared");
        Check((await Parse(",,")).Single().Fields.Count == 3, "Empty final fields disappeared");
        Check((await Parse("")).Count == 0 && (await Parse("\uFEFF")).Count == 0, "Empty stream produced data");
        var loneCr = await Parse("\"a\rb\",c\rd,e");
        Check(loneCr[0].Fields[0] == "a\rb" && loneCr[1].LineNumber == 3, "Quoted lone CR tracking failed");
    }

    private static async Task Framing()
    {
        const string source = "\uFEFF\"😀,é\",\"a\"\"b\r\n中\"\r\n\"last\",x";
        for (var chunk = 1; chunk <= 9; chunk++)
        {
            using var stream = new ProbeStream(Encoding.UTF8.GetBytes(source), chunk);
            var rows = await Read(stream);
            Check(rows.Count == 2 && rows[0].Fields.SequenceEqual(new[] { "😀,é", "a\"b\r\n中" }) && rows[1].LineNumber == 3,
                "Chunk boundary broke decoding or parser state");
        }
    }

    private static async Task Malformed()
    {
        foreach (var bad in new[] { "a\"b,x", "\"a\"x,b", "\"unfinished", "\"x\" ,y", " \"x\",y" })
            await Error(Parse(bad), LexiconErrorCode.MalformedDelimitedText);
        var failure = await Error(Parse("head,meaning\r\n\"two\nlines\",ok\nsecret-marker\"bad,x"), LexiconErrorCode.MalformedDelimitedText);
        Check(failure.RecordNumber == 3 && failure.LineNumber == 4 && failure.ColumnNumber == 1, "Error location is not the offending record/line/field");
        Check(!failure.ToString().Contains("secret-marker", StringComparison.Ordinal) && failure.InnerException is null, "Failure exposed input content");
    }

    private static async Task Encodings()
    {
        foreach (var bytes in new[]
        {
            new byte[] { 0xFF, 0xFE, 0x61, 0 }, new byte[] { 0xFE, 0xFF, 0, 0x61 },
            new byte[] { 0xC0, 0xAF }, new byte[] { 0xED, 0xA0, 0x80 }, new byte[] { 0xF4, 0x90, 0x80, 0x80 },
            new byte[] { 0xE4, 0xB8 }
        })
        {
            using var stream = new ProbeStream(bytes, 1);
            var error = await Error(Read(stream), LexiconErrorCode.InvalidEncoding);
            Check(error.InnerException is null && error.RecordNumber == 0 && error.LineNumber == 0 && error.ColumnNumber == 0,
                "Decoder details or a guessed error position were retained");
        }
        foreach (var control in new[] { '\0', '\u001B', '\u007F' })
            await Error(Parse("source,\"private" + control + "text\""), LexiconErrorCode.InvalidCharacter);
        Check((await Parse("a,\uFFFD")).Single().Fields[1] == "\uFFFD", "A valid UTF-8 replacement character was rejected as an encoding failure");
    }

    private static async Task Limits()
    {
        var exact = new DelimitedTextLimits(MaxBytes: 4);
        Check((await Parse("a,b\n", exact)).Count == 1, "Exact byte boundary was rejected");
        using var stream = new ProbeStream(Encoding.UTF8.GetBytes("a,b\n" + new string('x', 10_000)), int.MaxValue);
        await Error(Read(stream, limits: exact), LexiconErrorCode.InputTooLarge);
        Check(stream.BytesRead == exact.MaxBytes + 1, "Byte cap buffered arbitrary extra input");
        var bytes = Encoding.UTF8.GetByteCount("\uFEFF中,x");
        Check((await Parse("\uFEFF中,x", exact with { MaxBytes = bytes })).Count == 1, "UTF-8 BOM/multibyte exact boundary failed");
        await Error(Parse("\uFEFF中,x", exact with { MaxBytes = bytes - 1 }), LexiconErrorCode.InputTooLarge);
        Check((await Parse("\"abcd\",x", new(MaxFieldChars: 4))).Count == 1, "Decoded field boundary counted quote syntax");
        await Error(Parse("\"abcde\",x", new(MaxFieldChars: 4)), LexiconErrorCode.FieldTooLong);
        var newlineLimit = await Error(Parse("\"a\nb\",x", new(MaxFieldChars: 1)), LexiconErrorCode.FieldTooLong);
        Check(newlineLimit.LineNumber == 1 && newlineLimit.ColumnNumber == 1, "A failing quoted newline was attributed to the following line");
        Check((await Parse("a,b\r\n", new(MaxRecordChars: 3))).Count == 1, "Record boundary incorrectly counted its terminator");
        await Error(Parse("ab,b", new(MaxRecordChars: 3)), LexiconErrorCode.RecordTooLong);
        Check((await Parse("a,b", new(MaxColumns: 2))).Count == 1, "Exact column count failed");
        await Error(Parse("a,b,", new(MaxColumns: 2)), LexiconErrorCode.TooManyColumns);
        Check((await Parse("a\nb\n", new(MaxRecords: 2))).Count == 2, "Exact record count failed");
        await Error(Parse("a\nb\nc", new(MaxRecords: 2)), LexiconErrorCode.TooManyRecords);
        foreach (var invalid in new[] { new DelimitedTextLimits(MaxBytes: 0), new(MaxBytes: long.MaxValue), new(MaxRecords: 0),
            new(MaxColumns: 0), new(MaxFieldChars: 0), new(MaxRecordChars: 0) })
            await ThrowsAsync<ArgumentOutOfRangeException>(Parse("a", invalid));
        using var badFormat = Input("a");
        await ThrowsAsync<ArgumentOutOfRangeException>(Read(badFormat, (DelimitedTextFormat)99));
    }

    private static async Task Ownership()
    {
        using var complete = new ProbeStream(Encoding.UTF8.GetBytes("a,b"));
        await Read(complete);
        Check(!complete.Disposed && complete.ReadCalls > 0, "Successful read took ownership");
        using var invalid = new ProbeStream(Encoding.UTF8.GetBytes("\"incomplete"));
        await Error(Read(invalid), LexiconErrorCode.MalformedDelimitedText);
        Check(!invalid.Disposed, "Failed read closed caller stream");
        using var early = new ProbeStream(Encoding.UTF8.GetBytes("a,b\n" + new string('x', 30_000)));
        await using (var enumerator = DelimitedText.ReadAsync(early).GetAsyncEnumerator())
            Check(await enumerator.MoveNextAsync() && enumerator.Current.Fields[0] == "a", "Could not read first row");
        Check(!early.Disposed && early.BytesRead < 30_000, "Early disposal closed the input or buffered the whole file");
    }

    private static async Task Cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var before = new ProbeStream(Encoding.UTF8.GetBytes("a,b"));
        await ThrowsAsync<OperationCanceledException>(Read(before, token: cancelled.Token));
        Check(before.BytesRead == 0 && !before.Disposed, "Pre-cancellation touched input");

        using var during = new CancellationTokenSource();
        using var blocked = new BlockingStream();
        var reading = Read(blocked, token: during.Token);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        during.Cancel();
        await ThrowsAsync<OperationCanceledException>(reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Check(blocked.Cancelled && !blocked.Disposed, "Cancellation did not reach the underlying read");

        using var between = new CancellationTokenSource();
        using var buffered = new ProbeStream(Encoding.UTF8.GetBytes("a,b\nc,d\n"));
        await using var enumerator = DelimitedText.ReadAsync(buffered, cancellationToken: between.Token).GetAsyncEnumerator();
        Check(await enumerator.MoveNextAsync(), "First buffered record missing");
        between.Cancel();
        await ThrowsAsync<OperationCanceledException>(enumerator.MoveNextAsync().AsTask());
    }

    private static async Task RoundTrips()
    {
        var random = new Random(197);
        var alphabet = new[] { "a", "中", "😀", "\"", ",", "\r", "\n", "\t", " ", "e\u0301", "<b>" };
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var expected = Enumerable.Range(0, random.Next(1, 8)).Select(_ => Enumerable.Range(0, random.Next(1, 7))
                .Select(_ => string.Concat(Enumerable.Range(0, random.Next(0, 9)).Select(_ => alphabet[random.Next(alphabet.Length)]))).ToArray()).ToArray();
            // Independent serializer: quote every field. The reader must not rely on field lengths or separators.
            var text = string.Join("\r\n", expected.Select(row => string.Join(",", row.Select(cell => "\"" + cell.Replace("\"", "\"\"") + "\""))));
            if (iteration % 2 == 0) text += "\r\n";
            using var stream = new ProbeStream(Encoding.UTF8.GetBytes(text), random.Next(1, 12));
            var actual = await Read(stream);
            Check(actual.Count == expected.Length && actual.Select((row, index) => row.Fields.SequenceEqual(expected[index])).All(value => value), "Generated CSV round trip changed data");
        }
    }

    internal static MemoryStream Input(string text) => new(Encoding.UTF8.GetBytes(text), writable: false);
    private static async Task<List<DelimitedTextRecord>> Parse(string text, DelimitedTextLimits? limits = null)
    {
        using var input = Input(text);
        return await Read(input, limits: limits);
    }
    private static async Task<List<DelimitedTextRecord>> Read(Stream stream, DelimitedTextFormat format = DelimitedTextFormat.Csv,
        DelimitedTextLimits? limits = null, CancellationToken token = default)
    {
        var rows = new List<DelimitedTextRecord>();
        await foreach (var row in DelimitedText.ReadAsync(stream, format, limits, token)) rows.Add(row);
        return rows;
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static async Task<LexiconImportException> Error(Task task, LexiconErrorCode code)
    {
        try { await task; }
        catch (LexiconImportException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}"); return error; }
        throw new Exception("Expected " + code);
    }
    internal static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    internal static async Task<T> ThrowsAsync<T>(Task task) where T : Exception
    {
        try { await task; }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    internal sealed class ProbeStream(byte[] bytes, int chunkSize = 4096) : Stream
    {
        private int _offset;
        public int BytesRead => _offset;
        public int ReadCalls { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            var size = Math.Min(Math.Min(count, chunkSize), bytes.Length - _offset);
            bytes.AsSpan(_offset, size).CopyTo(buffer.AsSpan(offset, size));
            _offset += size;
            return size;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            var size = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - _offset);
            bytes.AsMemory(_offset, size).CopyTo(buffer);
            _offset += size;
            return ValueTask.FromResult(size);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
