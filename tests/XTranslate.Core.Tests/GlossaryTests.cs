using System.Diagnostics;
using System.Globalization;
using System.Text;
using XTranslate.Core.Lexicons;
using static DelimitedTextTests;

internal static class GlossaryTests
{
    private static readonly GlossaryInfo Info = new("Software", "Development", "en", "zh-CN");
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Glossary CSV/TSV import requires explicit format and header choices", ImportFormats),
        ("Glossary column mapping preserves notes and explicit keep-original flags", ImportMapping),
        ("Glossary imports reject broken shapes, empty data and invalid boolean flags", ImportFailures),
        ("Duplicate/conflicting terms identify both records without exposing their content", Conflicts),
        ("Glossary validation is shared by imports and manual entries", Validation),
        ("Glossary term/count/text budgets reject overflow rather than truncate", Budgets),
        ("Glossary metadata keeps a specific canonical language pair", Metadata),
        ("Glossary snapshots detach from editable lists and expose read-only results", Snapshots),
        ("Term matching protects Unicode word/identifier boundaries and punctuation terms", WordBoundaries),
        ("Term matching picks the longest valid non-overlapping phrase from the left", LongestPhrases),
        ("Literal matching is explicit and never executes imported regex syntax", Literal),
        ("Case matching is ordinal across cultures and supplementary Unicode letters", UnicodeCase),
        ("Literal matcher agrees with an independent leftmost-longest reference", ReferenceMatches),
        ("Glossary cancellation and invalid text never produce partial results", Cancellation),
        ("Shared-prefix and dense-overlap glossaries exercise bounded adversarial matching", SharedPrefix)
    };

    private static async Task ImportFormats()
    {
        using var csv = new ProbeStream(Encoding.UTF8.GetBytes("\uFEFFthread,线程\r\n\"source, term\",\"译法,含逗号\"\r\n"), 1);
        var table = await GlossaryCsv.ImportAsync(csv, Info);
        Check(table.Terms.Count == 2 && table.Terms[1].Source == "source, term" && table.Terms[1].Target == "译法,含逗号" && !csv.Disposed,
            "CSV glossary content or caller ownership changed");
        using var tsv = Input("thread\t线程\nprocess\t进程");
        Check((await GlossaryCsv.ImportAsync(tsv, Info, new(Format: DelimitedTextFormat.Tsv))).Terms.Count == 2, "TSV glossary failed");
        using var withHeader = Input("source,target\nthread,线程\n");
        Check((await GlossaryCsv.ImportAsync(withHeader, Info, new(HasHeader: true))).Terms.Single().Source == "thread", "Explicit header was imported as data");
        using var noHeader = Input("source,target\nthread,线程\n");
        Check((await GlossaryCsv.ImportAsync(noHeader, Info)).Terms.Count == 2, "A header was silently guessed/dropped");
        using var wrongFormat = Input("thread\t线程");
        await Error(GlossaryCsv.ImportAsync(wrongFormat, Info), LexiconErrorCode.WrongColumnCount);
    }

    private static async Task ImportMapping()
    {
        var options = new GlossaryCsvOptions(HasHeader: true, Columns: new(Source: 2, Target: 0, Note: 3, PreserveSource: 1));
        using var input = Input("preferred,keep,original,note,ignored\n,FALSE,thread,ordinary,x\n");
        var empty = await Error(GlossaryCsv.ImportAsync(input, Info, options), LexiconErrorCode.MissingTargetTerm);
        Check(empty.ColumnNumber == 1, "Reordered target error lost its column");
        using var valid = Input("preferred,keep,original,note,ignored\n,TRUE,API,\"first\nsecond\",x\n线程,false,thread,,x\n");
        var table = await GlossaryCsv.ImportAsync(valid, Info, options);
        Check(table.Terms[0].PreserveSource && table.Terms[0].Target == "" && table.Terms[0].Note == "first\nsecond", "Preserve flag or note was lost");
        var match = table.FindMatches("api").Single();
        Check(match.PreferredTranslation == "api" && match.SourceText == "api", "Keep-original normalized the user's case");
        using var subset = Input("label,original,preferred\nunused,thread,线程");
        Check((await GlossaryCsv.ImportAsync(subset, Info, new(HasHeader: true, Columns: new(1, 2)))).Terms.Single().Target == "线程",
            "Explicit subset column mapping failed");
    }

    private static async Task ImportFailures()
    {
        foreach (var (csv, code) in new[]
        {
            ("", LexiconErrorCode.EmptyInput), ("a,b,c", LexiconErrorCode.WrongColumnCount),
            ("a", LexiconErrorCode.WrongColumnCount), ("a,", LexiconErrorCode.MissingTargetTerm),
            (",b", LexiconErrorCode.MissingSourceTerm), ("\"unfinished", LexiconErrorCode.MalformedDelimitedText),
            ("thread,线程\n\n", LexiconErrorCode.WrongColumnCount)
        }) await Error(Import(csv), code);
        using var headerOnly = Input("source,target\n");
        await Error(GlossaryCsv.ImportAsync(headerOnly, Info, new(HasHeader: true)), LexiconErrorCode.MissingData);
        using var duplicateHeaders = Input("source,SOURCE\na,b");
        await Error(GlossaryCsv.ImportAsync(duplicateHeaders, Info, new(HasHeader: true)), LexiconErrorCode.InvalidHeader);
        foreach (var flag in new[] { "", "yes", " true ", "0" })
        {
            using var input = Input("API,," + flag);
            await Error(GlossaryCsv.ImportAsync(input, Info, new(Columns: new(PreserveSource: 2))), LexiconErrorCode.InvalidBoolean);
        }
        foreach (var map in new[] { new GlossaryColumnMap(0, 0), new(-1, 1), new(0, 64), new(0, 1, 1) })
        {
            using var input = new ProbeStream(Encoding.UTF8.GetBytes("a,b"));
            await Error(GlossaryCsv.ImportAsync(input, Info, new(Columns: map)), LexiconErrorCode.InvalidColumnMapping);
            Check(input.ReadCalls == 0, "Invalid column mapping started reading");
        }
        using var tooShortHeader = Input("source,target\na,b");
        await Error(GlossaryCsv.ImportAsync(tooShortHeader, Info, new(HasHeader: true, Columns: new(Note: 2))), LexiconErrorCode.InvalidColumnMapping);
    }

    private static async Task Conflicts()
    {
        var same = await Error(Import("private_term,译法\nPRIVATE_TERM,译法"), LexiconErrorCode.DuplicateTerm);
        Check(same.RecordNumber == 2 && same.RelatedRecordNumber == 1 && !same.ToString().Contains("private_term", StringComparison.OrdinalIgnoreCase),
            "Duplicate report lost provenance or included imported contents");
        using var input = Input("source,target,note\r\nthread,线程,\"two\r\nlines\"\r\nTHREAD,螺纹,other");
        var conflict = await Error(GlossaryCsv.ImportAsync(input, Info, new(HasHeader: true, Columns: new(Note: 2))), LexiconErrorCode.ConflictingTerm);
        Check(conflict.RecordNumber == 3 && conflict.RelatedRecordNumber == 2 && conflict.LineNumber == 4 && conflict.ColumnNumber == 1,
            "Conflict positions did not distinguish logical records and physical lines");
        using var sensitive = Input("US,美国\nus,我们");
        var table = await GlossaryCsv.ImportAsync(sensitive, Info, new(CaseSensitive: true));
        Check(table.FindMatches("US us").Select(match => match.PreferredTranslation).SequenceEqual(new[] { "美国", "我们" }),
            "Explicit case-sensitive meanings collided");
        DirectError(() => Glossary.Create(Info, new[] { new GlossaryTerm("API", "", PreserveSource: true), new GlossaryTerm("api", "接口") }),
            LexiconErrorCode.ConflictingTerm);
    }

    private static async Task Validation()
    {
        foreach (var (term, code) in new[]
        {
            (new GlossaryTerm("", "x"), LexiconErrorCode.MissingSourceTerm),
            (new GlossaryTerm("term", ""), LexiconErrorCode.MissingTargetTerm),
            (new GlossaryTerm(" term", "x"), LexiconErrorCode.InvalidTerm),
            (new GlossaryTerm("term", " x"), LexiconErrorCode.InvalidTerm),
            (new GlossaryTerm("two\nlines", "x"), LexiconErrorCode.InvalidTerm),
            (new GlossaryTerm("term", "two\tvalues"), LexiconErrorCode.InvalidTerm),
            (new GlossaryTerm("\uD800", "x"), LexiconErrorCode.InvalidTerm),
            (new GlossaryTerm("term", "\uDFFF"), LexiconErrorCode.InvalidTerm),
            (new GlossaryTerm("term", "x", "\0"), LexiconErrorCode.InvalidTerm),
            (new GlossaryTerm("term", "different", PreserveSource: true), LexiconErrorCode.InvalidTerm)
        }) DirectError(() => Glossary.Create(Info, new[] { term }), code);
        using var imported = Input("\"two\nlines\",x");
        await Error(GlossaryCsv.ImportAsync(imported, Info), LexiconErrorCode.InvalidTerm);
        var preserved = Glossary.Create(Info, new[] { new GlossaryTerm("C++", "C++", PreserveSource: true) });
        Check(preserved.FindMatches("C++").Single().PreferredTranslation == "C++", "Explicit same-source preserve was rejected");
    }

    private static Task Budgets()
    {
        Glossary.Create(Info, new[] { new GlossaryTerm(new string('a', Glossary.MaxSourceChars), new string('b', Glossary.MaxTargetChars), new string('n', Glossary.MaxNoteChars)) });
        foreach (var term in new[]
        {
            new GlossaryTerm(new string('a', Glossary.MaxSourceChars + 1), "x"),
            new GlossaryTerm("a", new string('b', Glossary.MaxTargetChars + 1)),
            new GlossaryTerm("a", "b", new string('n', Glossary.MaxNoteChars + 1))
        }) DirectError(() => Glossary.Create(Info, new[] { term }), LexiconErrorCode.FieldTooLong);
        var countBoundary = Enumerable.Range(0, Glossary.MaxTerms).Select(index => new GlossaryTerm($"w{index:D5}", "t")).ToArray();
        Check(Glossary.Create(Info, countBoundary).Terms.Count == Glossary.MaxTerms, "Exact term limit was rejected");
        DirectError(() => Glossary.Create(Info, countBoundary.Append(new("extra", "x"))), LexiconErrorCode.TooManyTerms);
        var totalBoundary = Enumerable.Range(0, 500).Select(index => new GlossaryTerm($"term{index:D4}", "t", new string('n', 1991))).ToArray();
        Check(totalBoundary.Sum(term => term.Source.Length + term.Target.Length + term.Note.Length) == Glossary.MaxTotalChars, "Test budget fixture drifted");
        Glossary.Create(Info, totalBoundary);
        DirectError(() => Glossary.Create(Info, totalBoundary.Append(new("extra", "x"))), LexiconErrorCode.GlossaryTooLarge);
        return Task.CompletedTask;
    }

    private static Task Metadata()
    {
        var table = Glossary.Create(Info with { SourceLanguage = "EN", TargetLanguage = "ZH-cn" }, new[] { new GlossaryTerm("x", "y") });
        Check(table.Info.SourceLanguage == "en" && table.Info.TargetLanguage == "zh-CN", "Language codes were not canonicalized");
        foreach (var info in new[] { Info with { SourceLanguage = "auto" }, Info with { TargetLanguage = "unsupported" } })
            DirectError(() => Glossary.Create(info, new[] { new GlossaryTerm("x", "y") }), LexiconErrorCode.InvalidLanguage);
        foreach (var info in new[] { Info with { Name = "" }, Info with { Name = " padded " }, Info with { Domain = "line\nbreak" }, Info with { Name = new string('x', 121) } })
            DirectError(() => Glossary.Create(info, new[] { new GlossaryTerm("x", "y") }), LexiconErrorCode.InvalidGlossaryInfo);
        DirectError(() => Glossary.Create(Info, Array.Empty<GlossaryTerm>()), LexiconErrorCode.MissingData);
        Throws<ArgumentOutOfRangeException>(() => Glossary.Create(Info, new[] { new GlossaryTerm("x", "y") }, matchMode: (GlossaryMatchMode)99));
        return Task.CompletedTask;
    }

    private static Task Snapshots()
    {
        var original = new List<GlossaryTerm> { new("thread", "线程", "original note") };
        var table = Glossary.Create(Info, original);
        original[0] = original[0] with { Target = "changed" };
        original.Clear();
        Check(table.Terms.Count == 1 && table.FindMatches("thread").Single().PreferredTranslation == "线程", "Snapshot shares an editable collection");
        Throws<NotSupportedException>(() => ((IList<GlossaryTerm>)table.Terms).Clear());
        var matches = table.FindMatches("thread");
        Throws<NotSupportedException>(() => ((IList<GlossaryMatch>)matches).Clear());
        Check(table.Info == Info && table.Terms[0].Note == "original note", "Snapshot lost metadata");
        return Task.CompletedTask;
    }

    private static Task WordBoundaries()
    {
        var table = Glossary.Create(Info, new[] { new GlossaryTerm("API", "接口"), new("C++", "C++"), new(".NET", ".NET") });
        const string text = "API myAPIKey API_v2 API2 调用API接口 (C++) ASP.NET .NET C++17";
        var matches = table.FindMatches(text);
        Check(matches.Select(match => match.SourceText).SequenceEqual(new[] { "API", "API", "C++", ".NET" }), "Identifier/punctuation/Han boundary semantics changed");
        foreach (var match in matches) Check(text.Substring(match.Offset, match.Length) == match.SourceText, "Highlight offsets do not refer to original text");
        var letters = Glossary.Create(Info, new[] { new GlossaryTerm("e", "letter"), new("API", "interface") });
        Check(letters.FindMatches("e\u0301 API\u203Fname \U00010428API").Count == 0, "Combining, connector or supplementary letter boundaries were split");
        Check(table.FindMatches("API\nAPI\tAPI").Count == 3, "Whitespace boundaries failed");
        return Task.CompletedTask;
    }

    private static Task LongestPhrases()
    {
        var terms = new[] { new GlossaryTerm("machine", "机器"), new("learning", "学习"), new("machine learning", "机器学习"), new("软件", "software"), new("软件开发", "software development") };
        var first = Glossary.Create(Info, terms);
        var reverse = Glossary.Create(Info, terms.Reverse());
        const string text = "machine learning；软件开发";
        var matches = first.FindMatches(text);
        Check(matches.Select(match => match.Term.Source).SequenceEqual(new[] { "machine learning", "软件开发" }) &&
            matches.SequenceEqual(reverse.FindMatches(text)), "Longest phrase depended on input order or overlapped");
        Check(first.FindMatches("machine learningX").Single().Term.Source == "machine", "An invalid longer boundary hid a valid shorter match");
        var overlapping = Glossary.Create(Info, new[] { new GlossaryTerm("ab", "1"), new("bcde", "2") }, matchMode: GlossaryMatchMode.Literal);
        Check(overlapping.FindMatches("abcde").Single().SourceText == "ab", "Matching is not leftmost-first");
        var repetitions = Glossary.Create(Info, new[] { new GlossaryTerm("aba", "1"), new("baaa", "2") }, matchMode: GlossaryMatchMode.Literal);
        Check(repetitions.FindMatches("ababaaa").Select(match => match.SourceText).SequenceEqual(new[] { "aba", "baaa" }), "Overlapping occurrences were skipped before selection");
        Check(first.FindMatches("").Count == 0, "Empty preview invented a match");
        return Task.CompletedTask;
    }

    private static Task Literal()
    {
        var table = Glossary.Create(Info, new[] { new GlossaryTerm("API", "接口") }, matchMode: GlossaryMatchMode.Literal);
        Check(table.FindMatches("myAPIKey").Single().Offset == 2, "Explicit literal mode still required boundaries");
        var syntax = Glossary.Create(Info, new[] { new GlossaryTerm("a.b", "literal"), new("(a+)+$", "not executable"), new("C++", "language") });
        Check(syntax.FindMatches("axb aaaa").Count == 0 && syntax.FindMatches("a.b (a+)+$ C++").Count == 3,
            "Imported punctuation executed as a regex");
        return Task.CompletedTask;
    }

    private static Task UnicodeCase()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var insensitive = Glossary.Create(Info, new[] { new GlossaryTerm("FILE", "file"), new("\U00010400", "Deseret"), new("é", "composed"), new("e\u0301", "decomposed") });
            Check(insensitive.FindMatches("file \U00010428 é e\u0301").Select(match => match.PreferredTranslation)
                .SequenceEqual(new[] { "file", "Deseret", "composed", "decomposed" }), "Case folding used current culture or silently normalized Unicode");
            var sensitive = Glossary.Create(Info, new[] { new GlossaryTerm("FILE", "file") }, caseSensitive: true);
            Check(sensitive.FindMatches("file").Count == 0 && sensitive.FindMatches("FILE").Count == 1, "Case-sensitive option was ignored");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        return Task.CompletedTask;
    }

    private static Task ReferenceMatches()
    {
        var random = new Random(431);
        var alphabet = new[] { "a", "A", "b", "+", ".", "中", "😀" };
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var sensitive = iteration % 2 == 0;
            var comparison = sensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var keys = new HashSet<string>(sensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            while (keys.Count < 30)
                keys.Add(string.Concat(Enumerable.Range(0, random.Next(1, 5)).Select(_ => alphabet[random.Next(alphabet.Length)])));
            var terms = keys.Select(key => new GlossaryTerm(key, "target:" + key)).ToArray();
            var table = Glossary.Create(Info, terms, caseSensitive: sensitive, matchMode: GlossaryMatchMode.Literal);
            var text = string.Concat(Enumerable.Range(0, 100).Select(_ => alphabet[random.Next(alphabet.Length)]));
            var expected = new List<(int Offset, string Source)>();
            var longestFirst = terms.OrderByDescending(term => term.Source.Length).ToArray();
            for (var offset = 0; offset < text.Length;)
            {
                var next = longestFirst.FirstOrDefault(term => text.AsSpan(offset).StartsWith(term.Source, comparison));
                if (next is null) { offset++; continue; }
                expected.Add((offset, next.Source));
                offset += next.Source.Length;
            }
            var actual = table.FindMatches(text).Select(match => (match.Offset, match.Term.Source));
            Check(actual.SequenceEqual(expected), "Optimized matching differs from the simple reference");
        }
        return Task.CompletedTask;
    }

    private static async Task Cancellation()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var input = new ProbeStream(Encoding.UTF8.GetBytes("thread,线程"));
        await ThrowsAsync<OperationCanceledException>(GlossaryCsv.ImportAsync(input, Info, cancellationToken: cancel.Token));
        Check(input.ReadCalls == 0 && !input.Disposed, "Pre-cancelled import read or closed input");
        var enumerated = 0;
        IEnumerable<GlossaryTerm> EditableTerms()
        {
            enumerated++;
            yield return new GlossaryTerm("a", "b");
        }
        Throws<OperationCanceledException>(() => Glossary.Create(Info, EditableTerms(), cancellationToken: cancel.Token));
        Check(enumerated == 0, "Pre-cancelled snapshot construction enumerated editable terms");
        var table = Glossary.Create(Info, new[] { new GlossaryTerm("a", "b") });
        Throws<OperationCanceledException>(() => table.FindMatches("a", cancel.Token));
        DirectError(() => table.FindMatches(new string('x', Glossary.MaxMatchTextChars + 1)), LexiconErrorCode.MatchTextTooLong);
        DirectError(() => table.FindMatches("\uD800"), LexiconErrorCode.InvalidCharacter);
        DirectError(() => table.FindMatches("a\0"), LexiconErrorCode.InvalidCharacter);
    }

    private static Task SharedPrefix()
    {
        var prefix = new string('a', 100);
        var terms = Enumerable.Range(0, 4000).Select(index => new GlossaryTerm(prefix + index.ToString("D4", CultureInfo.InvariantCulture), "x"));
        var source = terms.ToArray();
        foreach (var mode in new[] { GlossaryMatchMode.WholeWord, GlossaryMatchMode.Literal })
        {
            var table = Glossary.Create(Info, source, matchMode: mode);
            var watch = Stopwatch.StartNew();
            Check(table.FindMatches(new string('a', Glossary.MaxMatchTextChars)).Count == 0, "Shared-prefix matching invented a result");
            Console.WriteLine($"INFO fixed shared-prefix match: mode={mode}, terms={table.Terms.Count}, textChars={Glossary.MaxMatchTextChars}, elapsedMs={watch.Elapsed.TotalMilliseconds:0.0}");
        }
        var denseTerms = Enumerable.Range(1, Glossary.MaxSourceChars).Select(length => new GlossaryTerm(new string('a', length), "x")).ToArray();
        var repeated = new string('a', Glossary.MaxMatchTextChars);
        foreach (var mode in new[] { GlossaryMatchMode.WholeWord, GlossaryMatchMode.Literal })
        {
            var table = Glossary.Create(Info, denseTerms, matchMode: mode);
            var watch = Stopwatch.StartNew();
            var matches = table.FindMatches(repeated);
            Check(mode == GlossaryMatchMode.WholeWord ? matches.Count == 0 :
                matches.Sum(match => match.Length) == repeated.Length && matches[0].Length == Glossary.MaxSourceChars,
                "Dense overlapping occurrences changed boundary/longest-match semantics");
            Console.WriteLine($"INFO fixed dense-overlap match: mode={mode}, terms={table.Terms.Count}, textChars={repeated.Length}, elapsedMs={watch.Elapsed.TotalMilliseconds:0.0}");
        }
        return Task.CompletedTask;
    }

    private static async Task<Glossary> Import(string csv)
    {
        using var input = Input(csv);
        return await GlossaryCsv.ImportAsync(input, Info);
    }
    private static LexiconImportException DirectError(Action action, LexiconErrorCode code)
    {
        var error = Throws<LexiconImportException>(action);
        Check(error.Code == code, $"Expected {code}, got {error.Code}");
        return error;
    }
}
