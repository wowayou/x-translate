using System.Diagnostics;
using System.Text;
using XTranslate.Core.Lexicons;
using static DelimitedTextTests;

internal static class DictionaryCsvTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Dictionary CSV maps named columns without conflating repeated meanings or interpreting markup", Mapping),
        ("ECDICT preset keeps phonetic, part-of-speech and word-form data as source fields", Ecdict),
        ("Dictionary schema validation rejects missing, duplicate and ambiguous mappings", InvalidSchema),
        ("Dictionary row validation reports missing meanings, invalid words and shape changes", InvalidRows),
        ("Dictionary preview validates beyond its sample and never returns partial success", Preview),
        ("Dictionary ignored columns are bounded and cancellation leaves the input alone", Boundaries),
        ("A generated 100000-entry dictionary is validated with a bounded preview", LargePreview)
    };

    private static async Task Mapping()
    {
        using var input = Input(" Meaning ,EXAMPLE,Word,ignored,phonetic\n\"  first\nmeaning  \",<b>plain</b>,bank,unused,bæŋk\nriver edge,,bank,unused,\n");
        var map = new DictionaryColumnMap("word", "meaning", Phonetic: "phonetic", Examples: "example");
        var entries = await Entries(input, map);
        Check(entries.Count == 2 && entries[0].Entry.Headword == "bank" && entries[1].Entry.Headword == "bank", "Repeated meanings were silently deduplicated");
        Check(entries[0].Entry.Definition == "  first\nmeaning  " && entries[0].Entry.Examples == "<b>plain</b>" &&
            entries[0].Entry.Phonetic == "bæŋk" && entries[0].Entry.Translation == "" && entries[0].Entry.Inflections == "",
            "Mapped text changed or unmapped fields were fabricated");
        Check(entries[1].RecordNumber == 3 && entries[1].LineNumber == 4, "Dictionary source coordinates were lost");
        using var translationOnly = Input("word,zh\nhello,你好");
        Check((await Entries(translationOnly, new("word", Translation: "zh"))).Single().Entry.Definition == "", "Translation-only schema was rejected");
    }

    private static async Task Ecdict()
    {
        using var input = Input("word,phonetic,definition,translation,pos,collins,oxford,tag,bnc,frq,exchange,detail,audio\n" +
            "perceived,pəˈsiːvd,notice\\nobserve,察觉,v.,0,0,,0,0,0:perceive/d:perceived,,\n");
        var entry = (await Entries(input, DictionaryColumnMap.Ecdict)).Single().Entry;
        Check(entry.Headword == "perceived" && entry.Phonetic == "pəˈsiːvd" && entry.Definition == "notice\\nobserve" &&
            entry.Translation == "察觉" && entry.PartOfSpeech == "v." && entry.Inflections == "0:perceive/d:perceived" && entry.Examples == "",
            "ECDICT preset changed source fields or treated its text as a command/escape language");
    }

    private static async Task InvalidSchema()
    {
        await Error(PreviewText("word,meaning\na,b", new("word")), LexiconErrorCode.InvalidColumnMapping);
        await Error(PreviewText("word,meaning\na,b", new("missing", "meaning")), LexiconErrorCode.InvalidColumnMapping);
        await Error(PreviewText("word,meaning\na,b", new("word", "meaning", "meaning")), LexiconErrorCode.InvalidColumnMapping);
        await Error(PreviewText("word,meaning\na,b", new("word", "meaning", Phonetic: "absent")), LexiconErrorCode.InvalidColumnMapping);
        foreach (var header in new[] { "word,WORD", "word,", "\"bad\nheader\",meaning" })
            await Error(PreviewText(header + "\na,b", new("word", "meaning")), LexiconErrorCode.InvalidHeader);
        await Error(PreviewText("", new("word", "meaning")), LexiconErrorCode.EmptyInput);
        await Error(PreviewText("\uFEFF", new("word", "meaning")), LexiconErrorCode.EmptyInput);
        await Error(PreviewText("word,meaning\n", new("word", "meaning")), LexiconErrorCode.MissingData);
    }

    private static async Task InvalidRows()
    {
        foreach (var (row, code) in new[]
        {
            (",yes", LexiconErrorCode.MissingHeadword), ("a,  ", LexiconErrorCode.MissingDefinition),
            ("a,b,c", LexiconErrorCode.WrongColumnCount), ("a", LexiconErrorCode.WrongColumnCount),
            (" a,b", LexiconErrorCode.InvalidHeadword), ("\"a\nb\",yes", LexiconErrorCode.InvalidHeadword),
            (new string('x', DictionaryCsv.MaxHeadwordChars + 1) + ",yes", LexiconErrorCode.FieldTooLong)
        })
        {
            var error = await Error(PreviewText("word,meaning\r\nok,valid\r\n" + row, new("word", "meaning")), code);
            Check(error.RecordNumber == 3 && error.LineNumber == 3, "Invalid row did not identify its source location");
        }
        using var input = Input("zh,word,en\n,,\n");
        var missingWord = await Error(Entries(input, new("word", "en", "zh")), LexiconErrorCode.MissingHeadword);
        Check(missingWord.ColumnNumber == 2, "Reordered word error lost its column");
        using var absentMeaning = Input("zh,word,en\n,source,\n");
        var missingMeaning = await Error(Entries(absentMeaning, new("word", "en", "zh")), LexiconErrorCode.MissingDefinition);
        Check(missingMeaning.ColumnNumber == 3, "Reordered definition error lost its column");
    }

    private static async Task Preview()
    {
        var csv = "word,meaning\n" + string.Concat(Enumerable.Range(0, 200).Select(index => $"word{index},meaning{index}\n"));
        using var input = new ProbeStream(Encoding.UTF8.GetBytes(csv), 17);
        var result = await DictionaryCsv.PreviewAsync(input, new("word", "meaning"), sampleSize: 3);
        Check(result.EntryCount == 200 && result.Sample.Count == 3 && result.Sample[2].Entry.Headword == "word2" &&
            input.BytesRead == Encoding.UTF8.GetByteCount(csv) && !input.Disposed, "Preview did not scan the file or retained more than its sample");
        Throws<NotSupportedException>(() => ((IList<DictionaryEntryRecord>)result.Sample).Clear());
        using var invalidLate = Input(csv + "last,\n");
        var error = await Error(DictionaryCsv.PreviewAsync(invalidLate, new("word", "meaning"), sampleSize: 1), LexiconErrorCode.MissingDefinition);
        Check(error.RecordNumber == 202, "Validation stopped at the preview size");
        using var syntaxLate = Input(csv + "\"unfinished");
        await Error(DictionaryCsv.PreviewAsync(syntaxLate, new("word", "meaning"), sampleSize: 1), LexiconErrorCode.MalformedDelimitedText);
        using var noSample = Input(csv);
        Check((await DictionaryCsv.PreviewAsync(noSample, new("word", "meaning"), sampleSize: 0)).Sample.Count == 0, "Count-only scan retained samples");
        using var tooMany = Input(csv);
        await ThrowsAsync<ArgumentOutOfRangeException>(DictionaryCsv.PreviewAsync(tooMany, new("word", "meaning"), sampleSize: 101));
    }

    private static async Task Boundaries()
    {
        using var ignored = Input("word,meaning,ignored\na,b,123456789\n");
        await Error(DictionaryCsv.PreviewAsync(ignored, new("word", "meaning"), limits: new(MaxFieldChars: 8)), LexiconErrorCode.FieldTooLong);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var untouched = new ProbeStream(Encoding.UTF8.GetBytes("word,meaning\na,b"));
        await ThrowsAsync<OperationCanceledException>(DictionaryCsv.PreviewAsync(untouched, new("word", "meaning"), cancellationToken: cancelled.Token));
        Check(untouched.ReadCalls == 0 && !untouched.Disposed, "Cancelled dictionary import touched input");
    }

    private static async Task LargePreview()
    {
        using var source = new GeneratedDictionaryStream(100_000);
        var watch = Stopwatch.StartNew();
        var preview = await DictionaryCsv.PreviewAsync(source, new("word", "meaning"), sampleSize: 3);
        Check(preview.EntryCount == 100_000 && preview.Sample.Count == 3 && source.GeneratedEntries == 100_000 && source.MaximumBufferBytes < 128,
            "Large generated input was truncated or the preview grew with its row count");
        Console.WriteLine($"INFO fixed dictionary scan: entries={preview.EntryCount}, bytes={source.BytesRead}, sample={preview.Sample.Count}, elapsedMs={watch.Elapsed.TotalMilliseconds:0.0}");
    }

    private static async Task<DictionaryImportPreview> PreviewText(string csv, DictionaryColumnMap columns)
    {
        using var input = Input(csv);
        return await DictionaryCsv.PreviewAsync(input, columns);
    }
    private static async Task<List<DictionaryEntryRecord>> Entries(Stream input, DictionaryColumnMap columns)
    {
        var result = new List<DictionaryEntryRecord>();
        await foreach (var entry in DictionaryCsv.ReadAsync(input, columns)) result.Add(entry);
        return result;
    }

    private sealed class GeneratedDictionaryStream(int entryCount) : Stream
    {
        private byte[] _pending = Encoding.UTF8.GetBytes("word,meaning\n");
        private int _offset;
        public int GeneratedEntries { get; private set; }
        public int MaximumBufferBytes { get; private set; }
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int ReadInto(Span<byte> destination)
        {
            var written = 0;
            while (written < destination.Length)
            {
                if (_offset == _pending.Length)
                {
                    if (GeneratedEntries == entryCount) break;
                    _pending = Encoding.UTF8.GetBytes($"word{GeneratedEntries:D6},fixed definition 中文 {GeneratedEntries:D6}\n");
                    _offset = 0;
                    GeneratedEntries++;
                    MaximumBufferBytes = Math.Max(MaximumBufferBytes, _pending.Length);
                }
                var size = Math.Min(destination.Length - written, _pending.Length - _offset);
                _pending.AsSpan(_offset, size).CopyTo(destination[written..]);
                _offset += size;
                written += size;
            }
            BytesRead += written;
            return written;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadInto(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadInto(buffer.Span));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
