using XTranslate.Core;

internal static class ScreenCaptureTests
{
    internal static IEnumerable<(string Name,Func<Task> Test)> All => new (string,Func<Task>)[]
    {
        ("Screen capture uses physical negative-origin bounds and rejects cross-monitor/oversized regions", Geometry),
        ("Screen application rules conservatively reject excluded/unknown owners, including occluded windows", Applications),
        ("Screen capture window identity snapshots detect process/move/new-window changes independent of order", Snapshots)
    };
    private static void Check(bool value) {if(!value) throw new Exception("Screen capture rule failed");}
    private static void Expect(Action run,ScreenCaptureError code)
    {
        try {run();} catch(ScreenCaptureException e) {Check(e.Code==code && e.InnerException is null);return;}
        throw new Exception("Expected capture failure "+code);
    }
    private static Task Geometry()
    {
        var monitor=new ScreenRectangle(-1920,-200,0,880);
        var region=ScreenRectangle.FromPoints(new(-20,200),new(-800,-100));
        Check(region==new ScreenRectangle(-800,-100,-20,200));ScreenCaptureRules.Validate(region,monitor);
        foreach(var bad in new[]{new ScreenRectangle(-800,0,1,100),new(-2000,0,-1800,100),new(-100,0,-99,100),new(-100,0,-100,100)})
            Expect(() => ScreenCaptureRules.Validate(bad,monitor),ScreenCaptureError.InvalidRegion);
        Expect(() => ScreenCaptureRules.Validate(new(0,0,4001,2000),new(0,0,5000,3000)),ScreenCaptureError.TooLarge);
        Check(new ScreenRectangle(0,0,10,10).Intersects(new(9,9,20,20)) && !new ScreenRectangle(0,0,10,10).Intersects(new(10,0,20,10)));
        return Task.CompletedTask;
    }
    private static Task Applications()
    {
        Check(CaptureAppPolicy.TryParse("secret.exe",out var rules));
        var safe=new CaptureWindow(1,2,new(0,0,50,50),"safe");
        ScreenCaptureRules.CheckApplications(rules,new[]{safe});
        Expect(() => ScreenCaptureRules.CheckApplications(rules,new[]{safe,new CaptureWindow(2,3,new(0,0,50,50),"SECRET")}),ScreenCaptureError.ExcludedApplication);
        Expect(() => ScreenCaptureRules.CheckApplications(rules,new[]{safe with{ProcessName=null}}),ScreenCaptureError.UnidentifiedApplication);
        Expect(() => ScreenCaptureRules.CheckApplications(rules,Enumerable.Repeat(safe,513).ToArray()),ScreenCaptureError.UnidentifiedApplication);
        Check(CaptureAppPolicy.TryParse("",out var empty));ScreenCaptureRules.CheckApplications(empty,new[]{safe with{ProcessName=null}});
        return Task.CompletedTask;
    }
    private static Task Snapshots()
    {
        var a=new CaptureWindow(1,2,new(0,0,100,100),"safe");var b=new CaptureWindow(2,3,new(100,0,200,100),"other");
        Check(ScreenCaptureRules.SameWindows(new[]{a,b},new[]{b,a}));
        foreach(var changed in new[]{a with{ProcessId=4},a with{Bounds=new(1,0,101,100)},a with{ProcessName="new"},a with{Handle=5}})
            Check(!ScreenCaptureRules.SameWindows(new[]{a},new[]{changed}));
        Check(!ScreenCaptureRules.SameWindows(new[]{a},new[]{a,b}));return Task.CompletedTask;
    }
}
