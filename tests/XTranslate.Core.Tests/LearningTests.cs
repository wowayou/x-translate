using System.Text;
using System.Text.Json.Nodes;
using XTranslate.Core.Learning;

internal static class LearningTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Learning content is explicit, validated and preserves plain Unicode context", Content),
        ("Learning snapshots are immutable and edits never force glossary terminology", Snapshots),
        ("Recall self-ratings schedule deterministic UTC intervals capped at one year", Schedule),
        ("Daily review limit persists independently of deleting cards and resets on a new UTC day", DailyLimit),
        ("Editing learning material resets recall while notes preserve it; stale revisions fail", Editing),
        ("Learning backups retain personal metadata, origins, revisions and recall state", RoundTrip),
        ("Learning JSON rejects malformed, missing, duplicate, future and inconsistent data safely", InvalidJson),
        ("Learning card count, total text and input byte limits reject rather than truncate", Limits)
    };
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    internal static LearningContent Entry(string prompt = "account for") => new(LearningKind.Phrase, prompt, "把……考虑在内",
        "We need to account for latency.", "personal note", "en", "zh-CN", LearningOrigin.Personal);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Error(Action action, LearningError code)
    {
        try { action(); } catch (LearningException e) { Check(e.Code == code && e.InnerException is null, "Wrong or unsafe learning error"); return; }
        throw new Exception("Expected " + code);
    }
    private static Task Content()
    {
        var raw = Entry("  C++ \U0001f600 e\u0301\n");
        Check(LearningBook.Empty.Add(raw, Now).Cards[0].Content == raw, "Content was normalized or changed");
        foreach (var bad in new[] { raw with { Prompt = " " }, raw with { Answer = "" }, raw with { Context = null! },
            raw with { Note = "private_marker\u0001" }, raw with { Prompt = "\ud800" }, raw with { SourceLanguage = "auto" },
            raw with { TargetLanguage = "ZH-cn" }, raw with { Kind = (LearningKind)100 }, raw with { Origin = (LearningOrigin)100 },
            raw with { Prompt = new string('x',1001) }, raw with { Answer = new string('x',4001) },
            raw with { Context = new string('x',3001) }, raw with { Note = new string('x',2001) } })
            Error(() => LearningBook.Empty.Add(bad, Now), LearningError.InvalidData);
        LearningBook.Empty.Add(raw with { SourceLanguage = "und", TargetLanguage = "und" }, Now);
        return Task.CompletedTask;
    }
    private static Task Snapshots()
    {
        var first = LearningBook.Empty.Add(Entry(), Now);
        var card = first.Cards.Single();
        var next = first.Edit(card, card.Content with { Answer = "不同的个人笔记" }, Now.AddMinutes(1));
        Check(first.Cards.Single() == card && first.Generation != next.Generation && LearningBook.Empty.Cards.Count == 0, "Snapshot was mutated");
        Check(first.Cards is not LearningCard[] && ((IList<LearningCard>)first.Cards).IsReadOnly, "Mutable card collection escaped");
        Check(next.Cards[0].Content.Answer != card.Content.Answer, "Edit was lost");
        return Task.CompletedTask;
    }
    private static Task Schedule()
    {
        foreach (var (rating, days) in new[] { (RecallRating.Forgotten,1), (RecallRating.Effortful,1), (RecallRating.Easy,3) })
        {
            var book = LearningBook.Empty.Add(Entry(), Now);
            var rated = book.Rate(book.Cards[0], rating, Now);
            var card = rated.Cards[0];
            Check(card.DueUtc == Now.AddDays(days) && card.IntervalDays == days && card.LastRating == rating && card.ReviewCount == 1,
                "First recall schedule drifted");
            Error(() => rated.Rate(card, rating, Now.AddHours(1)), LearningError.NotDue);
        }
        var current = LearningBook.Empty.Add(Entry(), Now);
        var time = Now;
        for (var i = 0; i < 15; i++) { current = current.Rate(current.Cards[0], RecallRating.Easy, time); time = current.Cards[0].DueUtc; }
        Check(current.Cards[0].IntervalDays == 365, "Easy intervals are not capped");
        current = current.Rate(current.Cards[0], RecallRating.Forgotten, time);
        Check(current.Cards[0].IntervalDays == 1, "Forgotten did not shorten schedule");
        Error(() => current.Edit(current.Cards[0], Entry(), Now), LearningError.InvalidTime);
        Error(() => LearningBook.Empty.Add(Entry(), Now.ToOffset(TimeSpan.FromHours(8))), LearningError.InvalidTime);
        return Task.CompletedTask;
    }
    private static Task DailyLimit()
    {
        var book = LearningBook.Empty.SetDailyLimit(1).Add(Entry("one"), Now).Add(Entry("two"), Now);
        book = book.Rate(book.Cards[0], RecallRating.Easy, Now);
        var other = book.Cards[1];
        Error(() => book.Rate(other, RecallRating.Easy, Now), LearningError.DailyLimit);
        book = book.Delete(book.Cards[0]);
        Check(book.ReviewsToday(Now) == 1, "Delete reset daily allowance");
        Error(() => book.Rate(other, RecallRating.Easy, Now), LearningError.DailyLimit);
        book = LearningJson.Read(LearningJson.Write(book));
        Check(book.ReviewsToday(Now) == 1 && book.DailyLimit == 1, "Daily limit did not survive backup");
        book = book.Rate(book.Cards.Single(), RecallRating.Effortful, Now.AddDays(1));
        Check(book.ReviewsToday(Now.AddDays(1)) == 1 && book.ReviewsToday(Now) == 0, "New day failed to reset count");
        Error(() => book.SetDailyLimit(0), LearningError.InvalidData);
        Error(() => book.SetDailyLimit(51), LearningError.InvalidData);
        return Task.CompletedTask;
    }
    private static Task Editing()
    {
        var book = LearningBook.Empty.Add(Entry(), Now);
        var old = book.Cards[0]; book = book.Rate(old, RecallRating.Easy, Now);
        Error(() => book.Delete(old), LearningError.StaleRevision);
        var rated = book.Cards[0];
        book = book.Edit(rated, rated.Content with { Note = "note only", Origin = LearningOrigin.AiReview }, Now.AddMinutes(1));
        Check(book.Cards[0].DueUtc == rated.DueUtc && book.Cards[0].ReviewCount == 1, "Note edit erased recall history");
        var before = book.Cards[0];
        book = book.Edit(before, before.Content with { Context = "Different context" }, Now.AddMinutes(2));
        Check(book.Cards[0].ReviewCount == 0 && book.Cards[0].LastRating is null && book.Cards[0].DueUtc == Now.AddMinutes(2), "Material change retained stale recall");
        Error(() => book.Delete(old with { Id = new string('a',32) }), LearningError.NotFound);
        return Task.CompletedTask;
    }
    private static Task RoundTrip()
    {
        var book = LearningBook.Empty.Add(Entry() with { Answer = "<script>literal data</script>", Origin = LearningOrigin.MachineTranslation }, Now);
        book = book.Rate(book.Cards[0], RecallRating.Easy, Now);
        var copy = LearningJson.Read(LearningJson.Write(book));
        Check(copy.Cards.SequenceEqual(book.Cards) && copy.Generation == book.Generation && copy.ReviewsOnDay == book.ReviewsOnDay, "Full backup lost state");
        Check(copy.Restore(book).Generation != book.Generation, "Restore reused an old concurrency generation");
        var empty = LearningJson.Read(LearningJson.Write(LearningBook.Empty));
        Check(empty.Cards.Count == 0, "Empty backup is invalid");
        return Task.CompletedTask;
    }
    private static Task InvalidJson()
    {
        var valid = LearningJson.Write(LearningBook.Empty.Add(Entry(), Now));
        var bad = new List<byte[]> { Encoding.UTF8.GetBytes("null"), Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("private_marker"), new byte[] { 0xff, 0xfe } };
        void Mutate(Action<JsonNode> action) { var n = JsonNode.Parse(valid)!; action(n); bad.Add(Encoding.UTF8.GetBytes(n.ToJsonString())); }
        Mutate(n => n["version"] = 2); Mutate(n => n["private_marker"] = "private_marker");
        Mutate(n => n.AsObject().Remove("reviewsOnDay")); Mutate(n => n["cards"] = null);
        Mutate(n => n["cards"]![0] = null); Mutate(n => n["cards"]![0]!["content"] = null);
        Mutate(n => n["cards"]![0]!["revision"] = 0); Mutate(n => n["cards"]![0]!["revision"] = long.MaxValue);
        Mutate(n => n["cards"]![0]!["content"]!["kind"] = 1);
        Mutate(n => n["cards"]![0]!["lastRating"] = "unknown");
        Mutate(n => n["cards"]![0]!["reviewCount"] = 1);
        Mutate(n => n["cards"]![0]!["dueUtc"] = Now.AddDays(1).ToString("O"));
        Mutate(n => n["cards"]![0]!["updatedUtc"] = Now.AddDays(-1).ToString("O"));
        Mutate(n => n["generation"] = "../private_marker"); Mutate(n => n["reviewsOnDay"] = 1);
        Mutate(n => ((JsonArray)n["cards"]!).Add(n["cards"]![0]!.DeepClone()));
        var json = Encoding.UTF8.GetString(valid);
        bad.Add(Encoding.UTF8.GetBytes(json.Replace("\"version\": 1", "\"version\": 1,\"version\": 1")));
        foreach (var data in bad) Error(() => LearningJson.Read(data), LearningError.InvalidData);
        return Task.CompletedTask;
    }
    private static Task Limits()
    {
        var n = JsonNode.Parse(LearningJson.Write(LearningBook.Empty.Add(Entry(), Now)))!;
        var sample = n["cards"]![0]!.DeepClone();
        JsonArray Cards(int count, string prompt) => new(Enumerable.Range(0,count).Select(_ =>
        {
            var c = sample.DeepClone(); c["id"] = Guid.NewGuid().ToString("N"); c["content"]!["prompt"] = prompt; return c;
        }).ToArray());
        n["cards"] = Cards(5000, "x");
        Check(LearningJson.Read(Encoding.UTF8.GetBytes(n.ToJsonString())).Cards.Count == 5000, "Exact card limit rejected");
        n["cards"] = Cards(5001, "x");
        Error(() => LearningJson.Read(Encoding.UTF8.GetBytes(n.ToJsonString())), LearningError.InvalidData);
        n["cards"] = Cards(2000, new string('x',1000));
        Error(() => LearningJson.Read(Encoding.UTF8.GetBytes(n.ToJsonString())), LearningError.TooLarge);
        Error(() => LearningJson.Read(new byte[LearningJson.MaxBytes + 1]), LearningError.TooLarge);
        return Task.CompletedTask;
    }
}
