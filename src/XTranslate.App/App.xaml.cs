using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using XTranslate.Core;

namespace XTranslate.App;

public partial class App : Application
{
    // This starts when the App type is loaded, before WPF enters OnStartup. The
    // report explicitly calls this a process-startup approximation: CLR host work
    // that happens before App is loaded is outside this stopwatch.
    internal static readonly Stopwatch StartupStopwatch = Stopwatch.StartNew();
    private Mutex? _instance;
    private bool _ownsMutex;
    public static bool IsSmokeTest { get; private set; }
    public static UiTheme? DiagnosticTheme { get; private set; }
    public static string? SmokeOutput { get; private set; }
    public static bool IsBenchmark { get; private set; }
    public static bool IsMemoryDiagnostics { get; private set; }
    public static string? BenchmarkOutput { get; private set; }
    public static string? BenchmarkMarker { get; private set; }
    public static int DiagnosticBatches { get; private set; } = 5;
    public static int DiagnosticIdleSeconds { get; private set; } = 10;
    public static int DiagnosticSettleSeconds { get; private set; } = 3;
    public static int DiagnosticWindowSeconds { get; private set; } = 30;
    public static int DiagnosticHoldSeconds { get; private set; }
    public static bool DiagnosticCompactHolds { get; private set; }
    public static string? DiagnosticConfigError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Select before creating any window; scoped to this process only. This
        // small, mostly-static UI uses software rendering to avoid retaining the
        // much larger hybrid-GPU driver working set observed in acceptance runs.
        System.Windows.Media.RenderOptions.ProcessRenderMode =
            e.Args.Contains("--hardware-rendering", StringComparer.OrdinalIgnoreCase)
                ? RenderMode.Default
                : RenderMode.SoftwareOnly;
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            // Graceful request only: the running instance finishes capture cleanup and saves nothing new.
            NativeMethods.PostMessage(new IntPtr(0xffff), NativeMethods.ExitMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown(0);
            return;
        }
        var applyIndex = Array.IndexOf(e.Args, "--apply-update");
        var onlineIndex = Array.IndexOf(e.Args, "--update-online");
        if (applyIndex >= 0 || onlineIndex >= 0)
        {
            // Headless updates for scripts and end-to-end tests: no window, no single-instance lock, JSON report.
            var reportIndex = applyIndex >= 0 ? applyIndex + 2 : onlineIndex + 1;
            if (reportIndex >= e.Args.Length) { Shutdown(2); return; }
            var report = Path.GetFullPath(e.Args[reportIndex]);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(async () => Shutdown(await AppUpdates.HeadlessAsync(report, async () =>
            {
                if (onlineIndex >= 0)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    return await AppUpdates.UpdateOnlineAsync(timeout.Token);
                }
                var zip = Path.GetFullPath(e.Args[applyIndex + 1]);
                var installed = await AppUpdates.ApplyPackageAsync(zip, AppUpdates.ReadSidecarSha256(zip));
                return new { success = true, from = AppUpdates.Current.ToString(), to = installed.ToString() };
            })));
            return;
        }
        if (e.Args.Contains("--cleanup-only", StringComparer.OrdinalIgnoreCase))
        {
            AppUpdates.AfterStart(e.Args); // Removes *.old from a previous update, then exits (scripts/tests).
            Shutdown(0);
            return;
        }
        var liveAiIndex = Array.IndexOf(e.Args, "--live-ai-check");
        if (liveAiIndex >= 0)
        {
            // Headless, one request, never opens windows, registers hotkeys or saves settings.
            if (liveAiIndex + 2 >= e.Args.Length) { Shutdown(2); return; }
            var settingsDirectory = Path.GetFullPath(e.Args[liveAiIndex + 1]);
            var reportPath = Path.GetFullPath(e.Args[liveAiIndex + 2]);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var serviceName = liveAiIndex + 3 < e.Args.Length ? e.Args[liveAiIndex + 3] : null;
            _ = Dispatcher.InvokeAsync(async () => Shutdown(await LiveAiCheck.RunAsync(settingsDirectory, reportPath, serviceName)));
            return;
        }
        IsSmokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        IsMemoryDiagnostics = e.Args.Contains("--memory-diagnostics", StringComparer.OrdinalIgnoreCase);
        IsBenchmark = IsMemoryDiagnostics || e.Args.Contains("--benchmark", StringComparer.OrdinalIgnoreCase);
        var themeIndex = Array.IndexOf(e.Args, "--theme");
        if ((IsSmokeTest || IsBenchmark) && themeIndex >= 0)
        {
            if (themeIndex + 1 >= e.Args.Length || !Enum.TryParse<UiTheme>(e.Args[themeIndex + 1], true, out var theme) || !Enum.IsDefined(theme))
            { Shutdown(2); return; }
            DiagnosticTheme = theme;
        }
        var smokeOutputIndex = Array.IndexOf(e.Args, "--smoke-output");
        if (smokeOutputIndex >= 0 && smokeOutputIndex + 1 < e.Args.Length)
            SmokeOutput = Path.GetFullPath(e.Args[smokeOutputIndex + 1]);
        var benchmarkOutputIndex = Array.IndexOf(e.Args, "--benchmark-output");
        if (benchmarkOutputIndex >= 0 && benchmarkOutputIndex + 1 < e.Args.Length)
            BenchmarkOutput = Path.GetFullPath(e.Args[benchmarkOutputIndex + 1]);
        var benchmarkMarkerIndex = Array.IndexOf(e.Args, "--benchmark-marker");
        if (benchmarkMarkerIndex >= 0 && benchmarkMarkerIndex + 1 < e.Args.Length)
            BenchmarkMarker = Path.GetFullPath(e.Args[benchmarkMarkerIndex + 1]);
        if (IsMemoryDiagnostics)
        {
            DiagnosticCompactHolds = e.Args.Contains("--diagnostic-compact-holds", StringComparer.OrdinalIgnoreCase);
            if (IsSmokeTest)
            {
                DiagnosticConfigError = "--memory-diagnostics cannot be combined with --smoke-test.";
            }
            var batches = DiagnosticBatches;
            var idle = DiagnosticIdleSeconds;
            var hold = DiagnosticHoldSeconds;
            var settle = DiagnosticSettleSeconds;
            var windowSeconds = DiagnosticWindowSeconds;
            string? error;
            if (DiagnosticConfigError is null && !TryReadIntArgument(e.Args, "--diagnostic-batches", 1, 100, batches, out batches, out error)) DiagnosticConfigError = error;
            if (DiagnosticConfigError is null && !TryReadIntArgument(e.Args, "--diagnostic-idle-seconds", 1, 600, idle, out idle, out error)) DiagnosticConfigError = error;
            if (DiagnosticConfigError is null && !TryReadIntArgument(e.Args, "--diagnostic-hold-seconds", 0, 600, hold, out hold, out error)) DiagnosticConfigError = error;
            if (DiagnosticConfigError is null && !TryReadIntArgument(e.Args, "--diagnostic-settle-seconds", 0, 600, settle, out settle, out error)) DiagnosticConfigError = error;
            if (DiagnosticConfigError is null && !TryReadIntArgument(e.Args, "--diagnostic-window-seconds", 1, 600, windowSeconds, out windowSeconds, out error)) DiagnosticConfigError = error;
            DiagnosticBatches = batches;
            DiagnosticIdleSeconds = idle;
            DiagnosticHoldSeconds = hold;
            DiagnosticSettleSeconds = settle;
            DiagnosticWindowSeconds = windowSeconds;
        }

        base.OnStartup(e);
        if (!IsSmokeTest && !IsBenchmark) AppUpdates.AfterStart(e.Args);
        var mutexName = IsMemoryDiagnostics
            ? "Local\\XTranslate.MemoryDiagnostics.v01"
            : IsBenchmark
            ? "Local\\XTranslate.Benchmark.v01"
            : IsSmokeTest ? "Local\\XTranslate.Smoke" : "Local\\XTranslate.v01";
        _instance = new Mutex(true, mutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (!IsBenchmark)
                NativeMethods.PostMessage(new IntPtr(0xffff), NativeMethods.ShowMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown(IsBenchmark ? 2 : 0);
            return;
        }

        if (IsMemoryDiagnostics && DiagnosticConfigError is not null)
        {
            WriteDiagnosticFailure(BenchmarkOutput, DiagnosticConfigError);
            Shutdown(2);
            return;
        }

        var benchmarkData = IsMemoryDiagnostics
            ? Path.Combine(Path.GetDirectoryName(BenchmarkOutput ?? Path.Combine(AppContext.BaseDirectory, "memory-diagnostics.json")) ?? AppContext.BaseDirectory, "memory-diagnostics-data")
            : IsBenchmark
            ? Path.Combine(Path.GetDirectoryName(BenchmarkOutput ?? Path.Combine(AppContext.BaseDirectory, "benchmark-result.json")) ?? AppContext.BaseDirectory, "benchmark-data")
            : null;
        var storage = new AppStorage(IsSmokeTest ? Path.Combine(AppContext.BaseDirectory, "smoke-data") : benchmarkData);
        var window = new MainWindow(storage);
        MainWindow = window;

        // Attach lifecycle probes before Show so the first Loaded/ContentRendered
        // events are deterministic and no first-frame event is missed.
        if (IsBenchmark)
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            // Keep the benchmark window out of the user's visible desktop. The
            // popup loop measures WPF Show/Hide and layout completion only.
            window.Left = -10000;
            window.Top = -10000;
            var loadedMs = double.NaN;
            window.Loaded += (_, _) => loadedMs = StartupStopwatch.Elapsed.TotalMilliseconds;
            EventHandler? contentRendered = null;
            contentRendered = (_, _) =>
            {
                // ContentRendered can fire again after a later Show/Hide cycle;
                // benchmark startup must be a one-shot process milestone.
                window.ContentRendered -= contentRendered;
                if (!string.IsNullOrWhiteSpace(BenchmarkMarker))
                {
                    try
                    {
                        var markerDirectory = Path.GetDirectoryName(BenchmarkMarker);
                        if (!string.IsNullOrWhiteSpace(markerDirectory)) Directory.CreateDirectory(markerDirectory);
                        File.WriteAllText(BenchmarkMarker, $"content-rendered\t{DateTimeOffset.UtcNow:O}\tpid={Environment.ProcessId}");
                    }
                    catch { /* Marker is an optional external timing aid. */ }
                }
                _ = IsMemoryDiagnostics
                    ? window.RunMemoryDiagnosticsAsync(BenchmarkOutput, loadedMs)
                    : window.RunBenchmarkAsync(BenchmarkOutput, loadedMs);
            };
            window.ContentRendered += contentRendered;
        }
        if (IsSmokeTest) window.Loaded += async (_, _) => await window.RunSmokeTestAsync(SmokeOutput);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static bool TryReadIntArgument(string[] args, string name, int minimum, int maximum, int fallback, out int value, out string? error)
    {
        value = fallback;
        error = null;
        var index = Array.IndexOf(args, name);
        if (index < 0) return true;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            error = $"{name} requires an integer from {minimum} to {maximum}.";
            return false;
        }
        if (value < minimum || value > maximum)
        {
            error = $"{name} must be between {minimum} and {maximum}.";
            return false;
        }
        return true;
    }

    private static void WriteDiagnosticFailure(string? output, string error)
    {
        try
        {
            var path = Path.GetFullPath(output ?? Path.Combine(AppContext.BaseDirectory, "memory-diagnostics.json"));
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                success = false,
                processId = Environment.ProcessId,
                phase = "argument-validation",
                error,
                completedUtc = DateTimeOffset.UtcNow
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
