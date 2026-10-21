using System.Text;

namespace XTranslate.Core;

public enum LocalOcrError { Unavailable, UnsupportedLanguage, InvalidImage, ImageTooLarge, EmptyText, OutputTooLarge, Busy, Timeout, Failed }
public sealed class LocalOcrException(LocalOcrError code) : Exception($"Local OCR failed: {code}.")
{
    public LocalOcrError Code { get; } = code;
}
public sealed record LocalOcrLanguage(string Tag, string Name);
public sealed record LocalOcrResult(string Text, string Language, long DurationMs);

/// <summary>Finite memory/layout constraints, not an OCR accuracy guarantee. BGRA bytes are owned by the caller.</summary>
public static class LocalOcrLimits
{
    public const int MaxDimension = 8192;
    public const int MaxPixels = 8_000_000;
    public const int MaxTextChars = 20_000;
    public static void ValidateImage(int width, int height, int byteCount)
    {
        if (width <= 0 || height <= 0 || byteCount < 0) throw new LocalOcrException(LocalOcrError.InvalidImage);
        var pixels = (long)width * height;
        if (width > MaxDimension || height > MaxDimension || pixels > MaxPixels) throw new LocalOcrException(LocalOcrError.ImageTooLarge);
        if (pixels * 4 != byteCount) throw new LocalOcrException(LocalOcrError.InvalidImage);
    }
    public static void ValidateLanguage(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag != tag.Trim() || tag.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new LocalOcrException(LocalOcrError.UnsupportedLanguage);
    }
    public static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new LocalOcrException(LocalOcrError.EmptyText);
        if (text.Length > MaxTextChars) throw new LocalOcrException(LocalOcrError.OutputTooLarge);
        try { _ = new UTF8Encoding(false,true).GetByteCount(text); }
        catch (EncoderFallbackException) { throw new LocalOcrException(LocalOcrError.Failed); }
    }
}
