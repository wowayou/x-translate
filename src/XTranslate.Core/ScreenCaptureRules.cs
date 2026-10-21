namespace XTranslate.Core;

/// <summary>Physical screen pixels; exclusive right/bottom. Never WPF device-independent units.</summary>
public readonly record struct ScreenRectangle(int Left,int Top,int Right,int Bottom)
{
    public long Width => (long)Right-Left;
    public long Height => (long)Bottom-Top;
    public bool IsValid => Width>0 && Height>0;
    public bool Contains(ScreenRectangle other) => IsValid && other.IsValid && other.Left>=Left && other.Top>=Top && other.Right<=Right && other.Bottom<=Bottom;
    public bool Intersects(ScreenRectangle other) => IsValid && other.IsValid && other.Left<Right && other.Right>Left && other.Top<Bottom && other.Bottom>Top;
    public static ScreenRectangle FromPoints(PixelPoint a,PixelPoint b) => new(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
}

public enum ScreenCaptureError { Unavailable, InvalidRegion, TooLarge, ExcludedApplication, UnidentifiedApplication, WindowChanged, Failed, CleanupPending }
public sealed class ScreenCaptureException(ScreenCaptureError code) : Exception($"Screen capture failed: {code}.")
{
    public ScreenCaptureError Code { get; }=code;
}
public sealed record CaptureWindow(long Handle,uint ProcessId,ScreenRectangle Bounds,string? ProcessName);

public static class ScreenCaptureRules
{
    public const int MaxWindows=512;
    public const int MaxExaminedWindows=4096;
    public static void Validate(ScreenRectangle region,ScreenRectangle monitor)
    {
        if (!monitor.Contains(region) || region.Width<4 || region.Height<4) throw new ScreenCaptureException(ScreenCaptureError.InvalidRegion);
        if (region.Width>LocalOcrLimits.MaxDimension || region.Height>LocalOcrLimits.MaxDimension || region.Width*region.Height>LocalOcrLimits.MaxPixels)
            throw new ScreenCaptureException(ScreenCaptureError.TooLarge);
    }
    // Conservative: even an excluded window behind another intersecting window blocks the capture.
    // Window rectangles/process names are convenience filtering, not a security sandbox or atomic desktop snapshot.
    public static void CheckApplications(CaptureAppPolicy policy,IReadOnlyList<CaptureWindow> windows)
    {
        if (windows.Count>MaxWindows) throw new ScreenCaptureException(ScreenCaptureError.UnidentifiedApplication);
        foreach(var window in windows)
        {
            if (window.Handle==0 || window.ProcessId==0 || !window.Bounds.IsValid) throw new ScreenCaptureException(ScreenCaptureError.UnidentifiedApplication);
            var decision=policy.Decide(window.ProcessName);
            if(decision==CaptureAppDecision.Excluded) throw new ScreenCaptureException(ScreenCaptureError.ExcludedApplication);
            if(decision==CaptureAppDecision.Unidentified) throw new ScreenCaptureException(ScreenCaptureError.UnidentifiedApplication);
        }
    }
    public static bool SameWindows(IReadOnlyList<CaptureWindow> before,IReadOnlyList<CaptureWindow> after) =>
        before.Count==after.Count && before.OrderBy(w => w.Handle).SequenceEqual(after.OrderBy(w => w.Handle));
}
