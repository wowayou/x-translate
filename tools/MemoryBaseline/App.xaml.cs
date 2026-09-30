using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using XTranslate.Diagnostics;

namespace MemoryBaseline;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RenderOptions.ProcessRenderMode =
            e.Args.Contains("--hardware-rendering", StringComparer.OrdinalIgnoreCase)
                ? System.Windows.Interop.RenderMode.Default
                : System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        DiagnosticOptions options;
        try
        {
            options = DiagnosticOptions.Parse(e.Args);
        }
        catch (ArgumentException)
        {
            Shutdown(2);
            return;
        }
        DiagnosticRun.WriteProgress(options, "startup-entered");
        if (!options.Enabled)
        {
            Shutdown(2);
            return;
        }

        var window = new DiagnosticWindow();
        MainWindow = window;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;

        // This branch is intentionally the only call site that can JIT the
        // System.Windows.Forms tray helper. Blank mode never enters it.
        IDisposable? tray = options.Mode == DiagnosticMode.Tray
            ? TrayHostFactory.CreateNoInlining()
            : null;

        var started = 0;
        async void StartDiagnostic(object? sender, RoutedEventArgs args)
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            window.Loaded -= StartLoaded;
            DiagnosticRun.WriteProgress(options, "loaded-entry");
            try
            {
                await DiagnosticRun.ExecuteAsync(window, options, tray);
            }
            catch (Exception error)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(options.OutputPath))
                        DiagnosticRun.WriteFailure(options.OutputPath!, options, error);
                }
                catch { }
                tray?.Dispose();
                Shutdown(1);
            }
        }
        void StartLoaded(object? sender, RoutedEventArgs args) => StartDiagnostic(sender, args);
        // The baseline intentionally uses Loaded as its one-shot entry point.
        // Some off-screen sessions do not raise ContentRendered, so it is not
        // used as a startup timing signal here.
        window.Loaded += StartLoaded;
        DiagnosticRun.WriteProgress(options, "before-show");
        window.Show();
        DiagnosticRun.WriteProgress(options, "after-show");
    }
}

internal enum DiagnosticMode
{
    Blank,
    Tray
}

internal sealed class DiagnosticOptions
{
    public bool Enabled { get; private init; }
    public string OutputPath { get; private init; } = "";
    public DiagnosticMode Mode { get; private init; }
    public int Batches { get; private init; } = 5;
    public int IdleSeconds { get; private init; } = 5;
    public int SettleSeconds { get; private init; } = 3;
    public int WindowSeconds { get; private init; } = 30;
    public int HoldSeconds { get; private init; }

    public static DiagnosticOptions Parse(string[] args)
    {
        var output = GetValue(args, "--benchmark-output");
        var modeText = GetValue(args, "--diagnostic-mode") ?? "blank";
        var mode = modeText.Equals("tray", StringComparison.OrdinalIgnoreCase)
            ? DiagnosticMode.Tray
            : DiagnosticMode.Blank;
        if (!args.Contains("--memory-diagnostics", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("--memory-diagnostics is required.");
        if (!modeText.Equals("blank", StringComparison.OrdinalIgnoreCase) &&
            !modeText.Equals("tray", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--diagnostic-mode must be blank or tray.");
        return new DiagnosticOptions
        {
            Enabled = true,
            OutputPath = string.IsNullOrWhiteSpace(output) ? Path.Combine(AppContext.BaseDirectory, "memory-diagnostics.json") : Path.GetFullPath(output),
            Mode = mode,
            Batches = ParseInt(args, "--diagnostic-batches", 5, 1, 100),
            IdleSeconds = ParseInt(args, "--diagnostic-idle-seconds", 5, 1, 600),
            HoldSeconds = ParseInt(args, "--diagnostic-hold-seconds", 0, 0, 600),
            SettleSeconds = ParseInt(args, "--diagnostic-settle-seconds", 3, 0, 600),
            WindowSeconds = ParseInt(args, "--diagnostic-window-seconds", 30, 1, 600)
        };
    }

    private static string? GetValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int ParseInt(string[] args, string name, int fallback, int min, int max)
    {
        var value = GetValue(args, name);
        if (value is null && HasArgument(args, name))
            throw new ArgumentException($"{name} requires an integer in [{min}, {max}].");
        if (value is null) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed < min || parsed > max)
            throw new ArgumentException($"{name} must be an integer in [{min}, {max}].");
        return parsed;
    }

    private static bool HasArgument(string[] args, string name) => args.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
}

internal static class DiagnosticRun
{
    public static async Task ExecuteAsync(DiagnosticWindow window, DiagnosticOptions options, IDisposable? tray)
    {
        WriteProgress(options, "run-start");
        using var sampler = new DiagnosticsSampler(includeModules: false);
        var phaseResults = new List<object>();
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.Delay(250);
        phaseResults.Add(new
        {
            phase = "startup",
            isVisible = window.IsVisible,
            samplingIntervalSeconds = 0,
            endSnapshot = sampler.Capture(),
            checkpoints = Array.Empty<object>()
        });
        WriteProgress(options, "startup-sampled");

        const int warmupIterations = 20;
        const int batchTransitions = 100;
        for (var i = 0; i < warmupIterations; i++)
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
            window.Hide();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        WriteProgress(options, "warmup-transitions-complete");
        phaseResults.Add(await CaptureIdlePhaseAsync(sampler, window, "warmup", options.IdleSeconds, iterations: warmupIterations));
        WriteProgress(options, "warmup-sampled");

        window.Show();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
        phaseResults.Add(await CaptureIdlePhaseAsync(sampler, window, "visible-idle", options.IdleSeconds));
        WriteProgress(options, "visible-idle-sampled");

        window.Hide();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        phaseResults.Add(await CaptureHiddenMeasurementWindowAsync(sampler, window, options));
        WriteProgress(options, "hidden-measurement-sampled");

        phaseResults.Add(await CaptureIdlePhaseAsync(sampler, window, "hidden-idle", options.IdleSeconds));
        WriteProgress(options, "hidden-idle-sampled");

        WriteProgress(options, "batches-start");
        for (var batch = 1; batch <= options.Batches; batch++)
        {
            WriteProgress(options, $"batch-{batch:D2}-transitions-start");
            for (var i = 0; i < batchTransitions; i++)
            {
                window.Show();
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                window.Hide();
            }
            // The hidden settle is unconditional and matches the production
            // diagnostic checkpoint after every transition batch.
            phaseResults.Add(new
            {
                phase = $"batch-{batch:D2}",
                isVisible = window.IsVisible,
                batch,
                transitions = batchTransitions,
                samplingIntervalSeconds = 0,
                endSnapshot = sampler.Capture(),
                checkpoints = Array.Empty<object>()
            });
            phaseResults.Add(await CaptureIdlePhaseAsync(sampler, window, $"batch-{batch:D2}-hidden-idle", options.IdleSeconds, batch: batch, transitions: batchTransitions));
            WriteProgress(options, $"batch-{batch:D2}-sampled");
        }

        window.Hide();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        phaseResults.Add(await CaptureIdlePhaseAsync(sampler, window, "hidden-recovery", options.IdleSeconds));
        WriteProgress(options, "hidden-recovery-sampled");

        object? loadedModules = null;
        string? moduleCaptureFailureReason = null;
        WriteProgress(options, "modules-start");
        try { loadedModules = sampler.CaptureModules(); moduleCaptureFailureReason = sampler.LastModuleCaptureFailureReason; }
        catch (Exception error) { moduleCaptureFailureReason = error.Message; }
        WriteProgress(options, "modules-finished");

        var report = new
        {
            success = true,
            completedUtc = DateTimeOffset.UtcNow,
            processId = Environment.ProcessId,
            mode = options.Mode.ToString().ToLowerInvariant(),
            parameters = new
            {
                diagnosticBatches = options.Batches,
                diagnosticIdleSeconds = options.IdleSeconds,
                diagnosticHoldSeconds = options.HoldSeconds,
                diagnosticSettleSeconds = options.SettleSeconds,
                diagnosticWindowSeconds = options.WindowSeconds,
                targetStatus = "not-evaluated",
                processRenderMode = RenderOptions.ProcessRenderMode.ToString(),
                warmupIterations,
                batchTransitions,
                windowWidth = 650,
                windowHeight = 740,
                showActivated = false,
                showInTaskbar = false,
                forcedCollection = false,
                workingSetTrim = false,
                trayMenuItemCount = options.Mode == DiagnosticMode.Tray ? 4 : 0
            },
            machine = new
            {
                os = Environment.OSVersion.VersionString,
                framework = RuntimeInformation.FrameworkDescription,
                dotnet = Environment.Version.ToString(),
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processorCount = Environment.ProcessorCount,
                stopwatchFrequency = Stopwatch.Frequency,
                dpi = new { x = Math.Round(VisualTreeHelper.GetDpi(window).DpiScaleX * 96, 2), y = Math.Round(VisualTreeHelper.GetDpi(window).DpiScaleY * 96, 2) }
            },
            loadedModules,
            moduleCaptureFailureReason,
            phases = phaseResults,
            note = "Snapshots use the shared DiagnosticsSampler. This is an off-screen baseline and does not claim taskbar, external-app, or foreground hotkey latency."
        };
        WriteReport(options.OutputPath, report);
        WriteProgress(options, "report-written");

        if (options.HoldSeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds));
        tray?.Dispose();
        window.Close();
        System.Windows.Application.Current.Shutdown(0);
    }

    private static async Task<object> CaptureIdlePhaseAsync(DiagnosticsSampler sampler, DiagnosticWindow window, string phase, int idleSeconds, int? iterations = null, int? batch = null, int? transitions = null)
    {
        var checkpoints = new List<object>(idleSeconds);
        object? endSnapshot = null;
        for (var second = 1; second <= idleSeconds; second++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var snapshot = sampler.Capture();
            endSnapshot = snapshot;
            checkpoints.Add(new
            {
                second,
                capturedUtc = DateTimeOffset.UtcNow,
                snapshot
            });
        }
        return new
        {
            phase,
            isVisible = window.IsVisible,
            iterations,
            batch,
            transitions,
            samplingIntervalSeconds = 1,
            endSnapshot,
            checkpoints
        };
    }

    private static async Task<object> CaptureHiddenMeasurementWindowAsync(DiagnosticsSampler sampler, DiagnosticWindow window, DiagnosticOptions options)
    {
        window.Hide();
        var settleWatch = Stopwatch.StartNew();
        if (options.SettleSeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(options.SettleSeconds));
        settleWatch.Stop();
        var samples = new List<DiagnosticsSnapshot>(options.WindowSeconds);
        var rows = new List<object>(options.WindowSeconds);
        var watch = Stopwatch.StartNew();
        for (var second = 1; second <= options.WindowSeconds; second++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var snapshot = sampler.Capture();
            samples.Add(snapshot);
            rows.Add(new { second, atMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3), snapshot });
        }
        watch.Stop();
        return new
        {
            phase = "hidden-measurement",
            isVisible = window.IsVisible,
            configuredSettleSeconds = options.SettleSeconds,
            actualSettleMs = Math.Round(settleWatch.Elapsed.TotalMilliseconds, 3),
            configuredSampleCount = options.WindowSeconds,
            actualSampleCount = samples.Count,
            actualDurationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
            sampleIntervalSeconds = 1,
            samples = rows,
            statistics = MemoryWindowStatistics.Compute(samples, options.WindowSeconds),
            targetStatus = "not-evaluated"
        };
    }

    public static void WriteFailure(string path, DiagnosticOptions options, Exception error)
    {
        WriteReport(path, new
        {
            success = false,
            completedUtc = DateTimeOffset.UtcNow,
            processId = Environment.ProcessId,
            mode = options.Mode.ToString().ToLowerInvariant(),
            error = error.ToString()
        });
    }

    public static void WriteProgress(DiagnosticOptions options, string phase)
    {
        try
        {
            var path = options.OutputPath + ".progress.log";
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}\t{phase}\tpid={Environment.ProcessId}{Environment.NewLine}");
        }
        catch { }
    }

    private static void WriteReport(string path, object report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
