namespace XTranslate.Diagnostics;

/// <summary>Pure helpers for decoding PSAPI working-set block flags.</summary>
public static class WorkingSetPageDecoder
{
    private const ulong SharedBit = 1UL << 8;

    public static bool IsShared(ulong flags, int pointerBits = 64)
    {
        if (pointerBits is not (32 or 64))
            throw new ArgumentOutOfRangeException(nameof(pointerBits), "Pointer width must be 32 or 64 bits.");

        return (flags & SharedBit) != 0;
    }

    public static int CountPrivatePages(ReadOnlySpan<ulong> flags, int pointerBits = 64)
    {
        if (pointerBits is not (32 or 64))
            throw new ArgumentOutOfRangeException(nameof(pointerBits), "Pointer width must be 32 or 64 bits.");

        var privatePages = 0;
        foreach (var value in flags)
        {
            if (!IsShared(value, pointerBits)) privatePages++;
        }

        return privatePages;
    }
}
