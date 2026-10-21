using System.Globalization;
using XTranslate.Core;

internal static class CaptureAppPolicyTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("Capture exclusions normalize executable names without paths, regex or partial matches", ExactNames),
        ("Nonempty capture exclusions reject unknown identities; empty policy preserves existing behavior", UnknownIdentity),
        ("Invalid capture rules and count/text/name limits fail as one list, never partial success", Limits),
        ("Capture exclusions are immutable and ordinal across UI cultures", CultureAndIsolation)
    };
    private static void Check(bool condition) { if (!condition) throw new Exception("Capture policy contract failed"); }
    private static Task ExactNames()
    {
        Check(CaptureAppPolicy.TryParse(" Chrome.EXE \r\n\nchrome\n Code - Insiders.exe\nfoo.exe.exe\n中文.exe",out var policy));
        Check(policy.Apps.SequenceEqual(new[] {"chrome.exe","code - insiders.exe","foo.exe.exe","中文.exe"}));
        foreach (var name in new[] {"Chrome","CHROME","Code - Insiders","foo.exe","中文"}) Check(policy.Decide(name) == CaptureAppDecision.Excluded);
        foreach (var name in new[] {"chromedriver","mychrome","code","foo","chrome.exe","chrome "}) Check(policy.Decide(name) == CaptureAppDecision.Allowed);
        Check(CaptureAppPolicy.TryParse(policy.ToSettingsText(),out var again) && again.Apps.SequenceEqual(policy.Apps));
        return Task.CompletedTask;
    }
    private static Task UnknownIdentity()
    {
        Check(CaptureAppPolicy.TryParse("",out var empty));
        Check(empty.Decide(null) == CaptureAppDecision.Allowed && empty.Decide("chrome") == CaptureAppDecision.Allowed);
        Check(CaptureAppPolicy.TryParse("chrome.exe",out var policy));
        foreach (var name in new string?[] {null,""," "}) Check(policy.Decide(name) == CaptureAppDecision.Unidentified);
        return Task.CompletedTask;
    }
    private static Task Limits()
    {
        foreach (var invalid in new string?[] {null,".exe","..","C:\\tools\\chrome.exe","../chrome.exe","*.exe","chrome?.exe",
            "one.exe\ninvalid/other.exe","chrome\0.exe","foo\tbar.exe","foo\rbar.exe","chrome\".exe","chrome:app.exe","bad\ud800.exe",
            new string('x',252),new string('x',252)+".exe",new string('x',CaptureAppPolicy.MaxSettingsChars+1),
            string.Join("\n",Enumerable.Repeat("chrome.exe",129))}) Check(!CaptureAppPolicy.TryParse(invalid,out _));
        Check(CaptureAppPolicy.TryParse(new string('x',251)+".exe",out _));
        Check(CaptureAppPolicy.TryParse(string.Join("\n",Enumerable.Range(0,128).Select(i => $"app{i}.exe")),out var limit) && limit.Apps.Count == 128);
        return Task.CompletedTask;
    }
    private static Task CultureAndIsolation()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Check(CaptureAppPolicy.TryParse("IDE.exe\n𐐀.exe",out var policy));
            Check(policy.Decide("ide") == CaptureAppDecision.Excluded && policy.Decide("ıde") == CaptureAppDecision.Allowed &&
                policy.Decide("𐐨") == CaptureAppDecision.Excluded);
            if (policy.Apps is IList<string> list)
            {
                try { list[0] = "allowed.exe"; throw new Exception("Policy is mutable"); }
                catch (NotSupportedException) { }
            }
            Check(policy.Decide("ide") == CaptureAppDecision.Excluded);
        }
        finally { CultureInfo.CurrentCulture = previous; }
        return Task.CompletedTask;
    }
}
