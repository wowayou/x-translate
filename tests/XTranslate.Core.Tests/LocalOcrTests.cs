using XTranslate.Core;

internal static class LocalOcrTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string,Func<Task>)[]
    {
        ("Local OCR rejects invalid/oversized BGRA dimensions before allocation", Images),
        ("Local OCR explicit language/text validation never trims, truncates or accepts invalid Unicode", Text)
    };
    private static void Expect(Action action, LocalOcrError code)
    {
        try { action(); }
        catch (LocalOcrException e) { if (e.Code == code && e.InnerException is null) return; throw; }
        throw new Exception("Expected bounded OCR rejection");
    }
    private static Task Images()
    {
        LocalOcrLimits.ValidateImage(8192,1,32768);
        LocalOcrLimits.ValidateImage(4000,2000,32_000_000);
        foreach (var (w,h,n) in new[] {(0,1,0),(1,0,0),(-1,1,0),(1,1,-1),(1,1,3),(2,1,9)})
            Expect(() => LocalOcrLimits.ValidateImage(w,h,n),LocalOcrError.InvalidImage);
        foreach (var (w,h) in new[] {(8193,1),(1,8193),(4001,2000),(int.MaxValue,int.MaxValue)})
            Expect(() => LocalOcrLimits.ValidateImage(w,h,0),LocalOcrError.ImageTooLarge);
        return Task.CompletedTask;
    }
    private static Task Text()
    {
        foreach (var language in new[] {"en-US","zh-Hans-CN","ja","zxx"}) LocalOcrLimits.ValidateLanguage(language);
        foreach (var bad in new[] {""," en-US","en-US ","../en-US","en\nUS","语言",new string('a',65)})
            Expect(() => LocalOcrLimits.ValidateLanguage(bad),LocalOcrError.UnsupportedLanguage);
        LocalOcrLimits.ValidateText("  原文\nHELLO  ");
        LocalOcrLimits.ValidateText(new string('x',20_000));
        Expect(() => LocalOcrLimits.ValidateText(new string('x',20_001)),LocalOcrError.OutputTooLarge);
        Expect(() => LocalOcrLimits.ValidateText(" \n\t"),LocalOcrError.EmptyText);
        Expect(() => LocalOcrLimits.ValidateText("bad\ud800"),LocalOcrError.Failed);
        return Task.CompletedTask;
    }
}
