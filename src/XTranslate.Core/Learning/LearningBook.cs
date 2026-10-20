namespace XTranslate.Core.Learning;

public enum LearningKind { Word, Phrase, Pattern }
public enum LearningOrigin { Personal, MachineTranslation, AiReview }
public enum RecallRating { Forgotten, Effortful, Easy }
public enum LearningError { InvalidData, TooLarge, NotFound, StaleRevision, NotDue, DailyLimit, InvalidTime }

/// <summary>Safe for diagnostics: no personal learning content.</summary>
public sealed class LearningException(LearningError code) : Exception($"Learning operation failed: {code}.")
{
    public LearningError Code { get; } = code;
}

public sealed record LearningContent(LearningKind Kind, string Prompt, string Answer, string Context, string Note,
    string SourceLanguage, string TargetLanguage, LearningOrigin Origin);

public sealed record LearningCard(string Id, long Revision, LearningContent Content,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, DateTimeOffset DueUtc,
    int IntervalDays, int ReviewCount, DateTimeOffset? LastReviewedUtc, RecallRating? LastRating);

/// <summary>Bounded, immutable personal notebook, separate from dictionaries and forced translation terminology.</summary>
public sealed class LearningBook
{
    public const int MaxCards = 5000;
    public const int MaxTextChars = 2_000_000;
    public const int DefaultDailyLimit = 10;
    public const string EmptyGeneration = "00000000000000000000000000000000";
    public string Generation { get; }
    public IReadOnlyList<LearningCard> Cards { get; }
    public int DailyLimit { get; }
    public DateOnly? ReviewDay { get; }
    public int ReviewsOnDay { get; }
    public static LearningBook Empty { get; } = new(EmptyGeneration, Array.Empty<LearningCard>(), DefaultDailyLimit, null, 0);

    private LearningBook(string generation, LearningCard[] cards, int dailyLimit, DateOnly? reviewDay, int reviewsOnDay)
    {
        Generation = generation; Cards = Array.AsReadOnly(cards); DailyLimit = dailyLimit;
        ReviewDay = reviewDay; ReviewsOnDay = reviewsOnDay;
    }

    internal static LearningBook Read(string generation, LearningCard[] cards, int dailyLimit, DateOnly? day, int reviews)
    {
        if (!IsId(generation) || cards is null || cards.Length > MaxCards || dailyLimit is < 1 or > 50 ||
            reviews is < 0 or > 10000 || day is null && reviews != 0 || day < DateOnly.FromDateTime(DateTime.UnixEpoch))
            throw new LearningException(LearningError.InvalidData);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long chars = 0;
        foreach (var card in cards)
        {
            Validate(card);
            if (!ids.Add(card.Id)) throw new LearningException(LearningError.InvalidData);
            chars += TextSize(card.Content);
        }
        if (chars > MaxTextChars) throw new LearningException(LearningError.TooLarge);
        return new(generation, cards.ToArray(), dailyLimit, day, reviews);
    }

    public LearningBook Add(LearningContent content, DateTimeOffset now)
    {
        Validate(content); CheckTime(now);
        if (Cards.Count >= MaxCards) throw new LearningException(LearningError.TooLarge);
        var card = new LearningCard(Guid.NewGuid().ToString("N"), 1, content, now, now, now, 0, 0, null, null);
        return Next(Cards.Append(card));
    }

    public LearningBook Edit(LearningCard expected, LearningContent content, DateTimeOffset now)
    {
        var old = Current(expected); Validate(content); CheckTime(now, old.UpdatedUtc);
        // Notes and attribution may be refined without erasing recall history; learning material changes reset it.
        var reset = (content with { Note = old.Content.Note, Origin = old.Content.Origin }) != old.Content;
        var updated = old with { Revision = old.Revision + 1, Content = content, UpdatedUtc = now };
        if (reset) updated = updated with { DueUtc = now, IntervalDays = 0, ReviewCount = 0, LastReviewedUtc = null, LastRating = null };
        return Replace(old, updated);
    }

    public LearningBook Delete(LearningCard expected)
    {
        var old = Current(expected);
        return Next(Cards.Where(c => c.Id != old.Id));
    }

    public LearningBook Rate(LearningCard expected, RecallRating rating, DateTimeOffset now)
    {
        var old = Current(expected); CheckTime(now, old.UpdatedUtc);
        if (!Enum.IsDefined(rating)) throw new LearningException(LearningError.InvalidData);
        if (now < old.DueUtc) throw new LearningException(LearningError.NotDue);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (ReviewDay is { } day && today < day) throw new LearningException(LearningError.InvalidTime);
        var count = ReviewsToday(now);
        if (count >= DailyLimit) throw new LearningException(LearningError.DailyLimit);
        if (old.ReviewCount >= 100000) throw new LearningException(LearningError.TooLarge);
        var interval = rating switch
        {
            RecallRating.Forgotten => 1,
            RecallRating.Effortful => Math.Max(1, old.IntervalDays),
            _ => old.IntervalDays == 0 ? 3 : Math.Min(365, old.IntervalDays * 2 + 1)
        };
        var updated = old with { Revision = old.Revision + 1, UpdatedUtc = now, DueUtc = now.AddDays(interval),
            IntervalDays = interval, ReviewCount = old.ReviewCount + 1, LastReviewedUtc = now, LastRating = rating };
        return Next(Cards.Select(c => c.Id == old.Id ? updated : c), today, count + 1);
    }

    public LearningBook SetDailyLimit(int limit)
    {
        if (limit is < 1 or > 50) throw new LearningException(LearningError.InvalidData);
        return Read(Guid.NewGuid().ToString("N"), Cards.ToArray(), limit, ReviewDay, ReviewsOnDay);
    }

    /// <summary>Explicit full restore creates a fresh generation: old previews can never overwrite a restored book.</summary>
    public LearningBook Restore(LearningBook backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return Read(Guid.NewGuid().ToString("N"), backup.Cards.ToArray(), backup.DailyLimit, backup.ReviewDay, backup.ReviewsOnDay);
    }

    public int ReviewsToday(DateTimeOffset now) => ReviewDay == DateOnly.FromDateTime(now.UtcDateTime) ? ReviewsOnDay : 0;
    public IReadOnlyList<LearningCard> Due(DateTimeOffset now) => Array.AsReadOnly(Cards.Where(c => c.DueUtc <= now)
        .OrderBy(c => c.DueUtc).ThenBy(c => c.CreatedUtc).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray());

    private LearningCard Current(LearningCard expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var card = Cards.FirstOrDefault(c => c.Id == expected.Id) ?? throw new LearningException(LearningError.NotFound);
        if (card.Revision != expected.Revision) throw new LearningException(LearningError.StaleRevision);
        if (card.Revision >= long.MaxValue - 1) throw new LearningException(LearningError.TooLarge);
        return card;
    }
    private LearningBook Replace(LearningCard old, LearningCard updated) => Next(Cards.Select(c => c.Id == old.Id ? updated : c));
    private LearningBook Next(IEnumerable<LearningCard> cards) => Read(Guid.NewGuid().ToString("N"), cards.ToArray(), DailyLimit, ReviewDay, ReviewsOnDay);
    private LearningBook Next(IEnumerable<LearningCard> cards, DateOnly day, int count) => Read(Guid.NewGuid().ToString("N"), cards.ToArray(), DailyLimit, day, count);

    private static void CheckTime(DateTimeOffset now, DateTimeOffset? previous = null)
    {
        if (!ValidUtc(now) || now > DateTimeOffset.MaxValue.AddDays(-366) || previous is { } before && now < before)
            throw new LearningException(LearningError.InvalidTime);
    }
    internal static bool IsId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value >= DateTimeOffset.UnixEpoch;
    private static long TextSize(LearningContent c) => (long)c.Prompt.Length + c.Answer.Length + c.Context.Length + c.Note.Length;

    public static void Validate(LearningContent content)
    {
        if (content is null || !Enum.IsDefined(content.Kind) || !Enum.IsDefined(content.Origin) ||
            !ValidText(content.Prompt, 1000, true) || !ValidText(content.Answer, 4000, true) ||
            !ValidText(content.Context, 3000, false) || !ValidText(content.Note, 2000, false) ||
            !Language(content.SourceLanguage) || !Language(content.TargetLanguage)) throw new LearningException(LearningError.InvalidData);
    }
    private static bool Language(string? value) => value == "und" || value is not null && TranslationLanguages.Find(value)?.Code == value;
    private static bool ValidText(string? value, int max, bool required)
    {
        if (value is null || value.Length > max || required && string.IsNullOrWhiteSpace(value) ||
            value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) return false;
        try { _ = new System.Text.UTF8Encoding(false, true).GetByteCount(value); return true; }
        catch (System.Text.EncoderFallbackException) { return false; }
    }
    internal static void Validate(LearningCard card)
    {
        if (card is null || !IsId(card.Id) || card.Revision is < 1 or >= long.MaxValue ||
            !ValidUtc(card.CreatedUtc) || !ValidUtc(card.UpdatedUtc) || !ValidUtc(card.DueUtc) || card.UpdatedUtc < card.CreatedUtc ||
            card.IntervalDays is < 0 or > 365 || card.ReviewCount is < 0 or > 100000)
            throw new LearningException(LearningError.InvalidData);
        Validate(card.Content);
        if (card.ReviewCount == 0)
        {
            if (card.IntervalDays != 0 || card.LastReviewedUtc is not null || card.LastRating is not null ||
                card.DueUtc < card.CreatedUtc || card.DueUtc > card.UpdatedUtc)
                throw new LearningException(LearningError.InvalidData);
        }
        else if (card.IntervalDays == 0 || card.LastReviewedUtc is not { } reviewed || !ValidUtc(reviewed) ||
                 reviewed < card.CreatedUtc || reviewed > card.UpdatedUtc || reviewed > DateTimeOffset.MaxValue.AddDays(-365) || card.LastRating is not { } rating || !Enum.IsDefined(rating) ||
                 card.DueUtc != reviewed.AddDays(card.IntervalDays)) throw new LearningException(LearningError.InvalidData);
    }
}
