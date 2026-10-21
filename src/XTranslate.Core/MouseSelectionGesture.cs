namespace XTranslate.Core;

public readonly record struct PixelPoint(int X, int Y);
public enum MouseSelectionInputKind { LeftDown, LeftUp, Dismiss, ForegroundChanged }
public readonly record struct MouseSelectionInput(MouseSelectionInputKind Kind, long Window, long Foreground, PixelPoint Point, long TimeMs, bool Injected = false);
public sealed record MouseSelectionGesture(long Window, PixelPoint Start, PixelPoint End, long CompletedAtMs);

/// <summary>Only proposes a gesture. A proposal is never proof of selected text or permission to read/send it.</summary>
public sealed class MouseSelectionGestureTracker
{
    private MouseSelectionInput? _down;
    private MouseSelectionInput? _tap;
    public void Reset() { _down = null; _tap = null; }
    public MouseSelectionGesture? Feed(MouseSelectionInput input)
    {
        if (input.Injected || input.TimeMs < 0 || input.Kind == MouseSelectionInputKind.Dismiss) { Reset(); return null; }
        if (input.Kind == MouseSelectionInputKind.ForegroundChanged)
        {
            // Clicking an inactive window can activate the very window in which the drag just began.
            if (_down?.Window != input.Window) _down = null;
            _tap = null; return null;
        }
        if (input.Window == 0) { Reset(); return null; }
        if (input.Kind == MouseSelectionInputKind.LeftDown) { _down = input; return null; }
        if (input.Kind != MouseSelectionInputKind.LeftUp) { Reset(); return null; }
        var down = _down; _down = null;
        if (down is null || down.Value.Window != input.Window || input.Foreground != input.Window ||
            input.TimeMs < down.Value.TimeMs || input.TimeMs - down.Value.TimeMs > 15_000) { Reset(); return null; }
        if (!Near(down.Value.Point, input.Point, 7))
        {
            _tap = null; return new(input.Window, down.Value.Point, input.Point, input.TimeMs);
        }
        if (input.TimeMs - down.Value.TimeMs > 500) { _tap = null; return null; }
        var tap = _tap; _tap = input;
        if (tap is not null && tap.Value.Window == input.Window && input.TimeMs >= tap.Value.TimeMs &&
            input.TimeMs - tap.Value.TimeMs <= 500 && Near(tap.Value.Point, input.Point, 4) &&
            input.TimeMs - down.Value.TimeMs <= 500)
        {
            _tap = null; return new(input.Window, input.Point, input.Point, input.TimeMs);
        }
        return null;
    }
    private static bool Near(PixelPoint a, PixelPoint b, int distance) =>
        Math.Abs((long)a.X - b.X) <= distance && Math.Abs((long)a.Y - b.Y) <= distance;
}

public readonly record struct SelectionRectangle(double Left, double Top, double Width, double Height)
{
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0 && Height > 0 && Width <= 100_000 && Height <= 100_000 &&
        Left >= int.MinValue && Top >= int.MinValue && Left + Width <= int.MaxValue && Top + Height <= int.MaxValue;
    public bool Contains(PixelPoint point) => IsValid && point.X >= Left - 12 && point.X <= Left + Width + 12 &&
        point.Y >= Top - 12 && point.Y <= Top + Height + 12;
}

public static class MouseSelectionGeometry
{
    public const int MaxRectangles = 256;
    public static bool CoversGesture(MouseSelectionGesture gesture, IReadOnlyList<SelectionRectangle> rectangles)
    {
        if (rectangles.Count is < 1 or > MaxRectangles || rectangles.Any(r => !r.IsValid)) return false;
        return rectangles.Any(r => r.Contains(gesture.Start)) && rectangles.Any(r => r.Contains(gesture.End));
    }
}
