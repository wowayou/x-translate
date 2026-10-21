using XTranslate.Core;

internal static class MouseSelectionTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Mouse gesture proposes bounded drag selections, not clicks or window identity changes", Drags),
        ("Mouse gesture supports bounded same-window double click without promoting a single click", DoubleClicks),
        ("Mouse foreground/injection/dismissal invalidate gestures while activation of the dragged window is allowed", Invalidation),
        ("Mouse gesture time and extreme physical coordinates reject stale/overflow cases", Boundaries),
        ("Mouse selection geometry requires both endpoints and rejects invalid/unbounded provider rectangles", Geometry),
        ("Mouse gesture proposals agree with an independent fixed-seed drag reference", Reference)
    };
    private static MouseSelectionInput E(MouseSelectionInputKind kind, int x = 10, int y = 20, long time = 100, long window = 42, long foreground = 42, bool injected = false) =>
        new(kind,window,foreground,new(x,y),time,injected);
    private static void Check(bool value) { if (!value) throw new Exception("Mouse selection contract failed"); }
    private static Task Drags()
    {
        var tracker = new MouseSelectionGestureTracker();
        Check(tracker.Feed(E(MouseSelectionInputKind.LeftUp)) is null);
        tracker.Feed(E(MouseSelectionInputKind.LeftDown));
        Check(tracker.Feed(E(MouseSelectionInputKind.LeftUp,x:17,time:110)) is null);
        tracker.Reset(); tracker.Feed(E(MouseSelectionInputKind.LeftDown));
        var drag = tracker.Feed(E(MouseSelectionInputKind.LeftUp,x:18,time:200));
        Check(drag == new MouseSelectionGesture(42,new(10,20),new(18,20),200));
        tracker.Feed(E(MouseSelectionInputKind.LeftDown));
        Check(tracker.Feed(E(MouseSelectionInputKind.LeftUp,x:100,time:200,window:43)) is null);
        tracker.Feed(E(MouseSelectionInputKind.LeftDown));
        Check(tracker.Feed(E(MouseSelectionInputKind.LeftUp,x:100,time:200,foreground:43)) is null);
        return Task.CompletedTask;
    }
    private static Task DoubleClicks()
    {
        foreach (var (interval, displacement, expected) in new[] {(500,4,true),(501,0,false),(100,5,false)})
        {
            var tracker = new MouseSelectionGestureTracker();
            tracker.Feed(E(MouseSelectionInputKind.LeftDown,time:0));
            Check(tracker.Feed(E(MouseSelectionInputKind.LeftUp,time:10)) is null);
            tracker.Feed(E(MouseSelectionInputKind.LeftDown,x:10+displacement,time:interval));
            Check((tracker.Feed(E(MouseSelectionInputKind.LeftUp,x:10+displacement,time:10+interval)) is not null) == expected);
        }
        var held = new MouseSelectionGestureTracker();
        held.Feed(E(MouseSelectionInputKind.LeftDown,time:0)); held.Feed(E(MouseSelectionInputKind.LeftUp,time:1000));
        held.Feed(E(MouseSelectionInputKind.LeftDown,time:1010));
        Check(held.Feed(E(MouseSelectionInputKind.LeftUp,time:1020)) is null);
        var reset = new MouseSelectionGestureTracker();
        reset.Feed(E(MouseSelectionInputKind.LeftDown)); reset.Feed(E(MouseSelectionInputKind.LeftUp,time:110)); reset.Reset();
        reset.Feed(E(MouseSelectionInputKind.LeftDown,time:120));
        Check(reset.Feed(E(MouseSelectionInputKind.LeftUp,time:130)) is null);
        return Task.CompletedTask;
    }
    private static Task Invalidation()
    {
        foreach (var input in new[] {E(MouseSelectionInputKind.Dismiss),E(MouseSelectionInputKind.ForegroundChanged,window:43),
            E(MouseSelectionInputKind.LeftUp,injected:true),E(MouseSelectionInputKind.LeftDown,window:0)})
        {
            var tracker = new MouseSelectionGestureTracker(); tracker.Feed(E(MouseSelectionInputKind.LeftDown)); tracker.Feed(input);
            Check(tracker.Feed(E(MouseSelectionInputKind.LeftUp,x:100,time:200)) is null);
        }
        var activation = new MouseSelectionGestureTracker();
        activation.Feed(E(MouseSelectionInputKind.LeftDown,foreground:41));
        activation.Feed(E(MouseSelectionInputKind.ForegroundChanged,time:101));
        Check(activation.Feed(E(MouseSelectionInputKind.LeftUp,x:100,time:200)) is not null);
        return Task.CompletedTask;
    }
    private static Task Boundaries()
    {
        foreach (var time in new long[] {-1,99,15_101})
        {
            var tracker = new MouseSelectionGestureTracker(); tracker.Feed(E(MouseSelectionInputKind.LeftDown));
            Check(tracker.Feed(E(MouseSelectionInputKind.LeftUp,x:100,time:time)) is null);
        }
        var extreme = new MouseSelectionGestureTracker();
        extreme.Feed(E(MouseSelectionInputKind.LeftDown,x:int.MinValue,y:int.MaxValue,time:long.MaxValue-1));
        Check(extreme.Feed(E(MouseSelectionInputKind.LeftUp,x:int.MaxValue,y:int.MinValue,time:long.MaxValue)) is not null);
        return Task.CompletedTask;
    }
    private static Task Geometry()
    {
        var gesture = new MouseSelectionGesture(42,new(-100,10),new(100,50),200);
        var rows = new[] {new SelectionRectangle(-110,0,100,20),new SelectionRectangle(0,40,110,20)};
        Check(MouseSelectionGeometry.CoversGesture(gesture,rows));
        Check(!MouseSelectionGeometry.CoversGesture(gesture,new[]{rows[0]}));
        foreach (var invalid in new[] {new SelectionRectangle(double.NaN,0,10,10),new SelectionRectangle(0,0,double.PositiveInfinity,10),
            new SelectionRectangle(0,0,0,10),new SelectionRectangle(0,0,-1,10),new SelectionRectangle(0,0,100001,10),
            new SelectionRectangle(int.MaxValue,0,1,10)})
            Check(!MouseSelectionGeometry.CoversGesture(gesture,rows.Append(invalid).ToArray()));
        Check(!MouseSelectionGeometry.CoversGesture(gesture,Array.Empty<SelectionRectangle>()));
        Check(!MouseSelectionGeometry.CoversGesture(gesture,Enumerable.Repeat(rows[0],257).ToArray()));
        return Task.CompletedTask;
    }
    private static Task Reference()
    {
        var random = new Random(81021);
        for (var i = 0; i < 1000; i++)
        {
            var x = random.Next(-20000,20000); var y = random.Next(-20000,20000);
            var endX = x + random.Next(-20,21); var endY = y + random.Next(-20,21);
            var tracker = new MouseSelectionGestureTracker();
            tracker.Feed(E(MouseSelectionInputKind.LeftDown,x,y));
            var actual = tracker.Feed(E(MouseSelectionInputKind.LeftUp,endX,endY,time:110));
            Check((actual is not null) == (Math.Abs(endX-x) >= 8 || Math.Abs(endY-y) >= 8));
        }
        return Task.CompletedTask;
    }
}
