using System.Diagnostics;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    internal static string? CaptureProcessName(IntPtr window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var id);
        if (id is 0 or > int.MaxValue) return null;
        try { using var process = Process.GetProcessById((int)id); return process.ProcessName; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return null; }
    }

    private bool AllowAppCapture(Func<string?> identify, bool quiet = false)
    {
        if (!CaptureAppPolicy.TryParse(_settings.CaptureExcludedApps, out var policy))
        { if (!quiet) SetStatus("SettingsExcludedAppsError"); return false; }
        // Empty policy adds no process inspection or behavior change to the existing capture path.
        var decision = policy.Decide(policy.Apps.Count == 0 ? null : identify());
        if (decision == CaptureAppDecision.Allowed) return true;
        if (!quiet) SetStatus(decision == CaptureAppDecision.Excluded ? "CaptureAppExcluded" : "CaptureAppUnidentified");
        return false;
    }
}
