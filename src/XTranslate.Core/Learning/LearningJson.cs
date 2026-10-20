using System.Text.Json;
using System.Text.Json.Serialization;

namespace XTranslate.Core.Learning;

/// <summary>Versioned full backup, including personal text and scheduling state. No secrets, scripts or settings.</summary>
public static class LearningJson
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private sealed record Document(int Version, string Generation, int DailyLimit, DateOnly? ReviewDay,
        int ReviewsOnDay, LearningCard[] Cards);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static byte[] Write(LearningBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var data = JsonSerializer.SerializeToUtf8Bytes(new Document(1, book.Generation, book.DailyLimit, book.ReviewDay,
            book.ReviewsOnDay, book.Cards.ToArray()), Options);
        if (data.Length > MaxBytes) throw new LearningException(LearningError.TooLarge);
        return data;
    }

    public static LearningBook Read(ReadOnlyMemory<byte> data)
    {
        if (data.Length > MaxBytes) throw new LearningException(LearningError.TooLarge);
        try
        {
            var document = JsonSerializer.Deserialize<Document>(data.Span, Options);
            if (document is null || document.Version != 1) throw new LearningException(LearningError.InvalidData);
            return LearningBook.Read(document.Generation, document.Cards, document.DailyLimit, document.ReviewDay, document.ReviewsOnDay);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            // Never retain a parser exception containing personal content, paths, or unknown JSON fields.
            throw new LearningException(LearningError.InvalidData);
        }
    }
}
