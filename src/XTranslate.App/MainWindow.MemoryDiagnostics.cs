using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using XTranslate.Core;
using XTranslate.Diagnostics;

namespace XTranslate.App;

public partial class MainWindow
{
    private HttpClient? _memoryDiagnosticsHttpClient;

    // Short, phase-oriented diagnostics intended for repeatable VMMap/process
    // snapshots. It deliberately does not run GC, trim the working set, inspect
    // clipboard/user settings, or make network requests.
    internal async Task RunMemoryDiagnosticsAsync(string? output, double loadedMs)
    {
        output = Path.GetFullPath(output ?? Path.Combine(AppContext.BaseDirectory, "memory-diagnostics.json"));
        var phases = new List<object>();
        var currentPhase = "startup";
        var compactSamplesPath = Path.Combine(Path.GetDirectoryName(output) ?? AppContext.BaseDirectory, "memory-soak-samples.jsonl");
        try
        {
            if (App.DiagnosticCompactHolds && File.Exists(compactSamplesPath))
                throw new IOException($"Compact diagnostic sample file already exists: {compactSamplesPath}");
            using var sampler = new DiagnosticsSampler(includeModules: false);
            _settings = new UserSettings
            {
                Translation = new TranslationSettings
                {
                    Provider = ProviderKind.GoogleWeb,
                    TargetLanguage = "zh-CN"
                }
            };
            _memoryDiagnosticsHttpClient = new HttpClient(new MemoryDiagnosticsHandler());
            _service.Dispose();
            _service = new TranslationService(_memoryDiagnosticsHttpClient);

            var startup = new Dictionary<string, object?>
            {
                ["phase"] = "startup",
                ["processId"] = Environment.ProcessId,
                ["loadedMs"] = double.IsNaN(loadedMs) ? null : Math.Round(loadedMs, 3),
                ["contentRenderedMs"] = Math.Round(App.StartupStopwatch.Elapsed.TotalMilliseconds, 3),
                ["snapshot"] = sampler.Capture()
            };
            phases.Add(startup);
            WriteMemoryReport(output, false, "startup", phases, null);

            const string sample = "Memory diagnostics fixed local sample.";
            const string expected = "本地诊断译文。";
            currentPhase = "warmup";
            await RunShowHideBatchAsync(20);
            await ShowForDiagnosticsAsync();
            SourceBox.Text = sample;
            for (var i = 0; i < 10; i++) await RunFixedTranslationAsync(sample, expected);
            Hide();
            phases.Add(new Dictionary<string, object?>
            {
                ["phase"] = currentPhase,
                ["processId"] = Environment.ProcessId,
                ["showHideIterations"] = 20,
                ["translationIterations"] = 10,
                ["snapshot"] = sampler.Capture()
            });
            WriteMemoryReport(output, false, currentPhase, phases, null);

            currentPhase = "visible-idle";
            await ShowForDiagnosticsAsync();
            await CaptureIdlePhaseAsync(sampler, phases, output, currentPhase, App.DiagnosticIdleSeconds);
            Hide();
            currentPhase = "hidden-measurement";
            phases.Add(await CaptureHiddenMeasurementWindowAsync(sampler));
            currentPhase = "hidden-idle";
            await CaptureIdlePhaseAsync(sampler, phases, output, currentPhase, App.DiagnosticIdleSeconds);

            for (var batch = 1; batch <= App.DiagnosticBatches; batch++)
            {
                // Every batch starts and ends hidden so phase comparisons do not
                // accidentally compare a visible start with a hidden endpoint.
                Hide();
                currentPhase = $"batch-{batch:D2}";
                var batchStart = sampler.Capture();
                await RunShowHideBatchAsync(100);
                await ShowForDiagnosticsAsync();
                SourceBox.Text = sample;
                for (var i = 0; i < 20; i++) await RunFixedTranslationAsync(sample, expected);
                Hide();
                var batchRow = new Dictionary<string, object?>
                {
                    ["phase"] = currentPhase,
                    ["processId"] = Environment.ProcessId,
                    ["showHideIterations"] = 100,
                    ["translationIterations"] = 20,
                    ["startSnapshot"] = batchStart,
                    ["endSnapshot"] = sampler.Capture()
                };
                phases.Add(batchRow);
                WriteMemoryReport(output, false, currentPhase, phases, null);

                currentPhase = $"batch-{batch:D2}-hidden-idle";
                await CaptureIdlePhaseAsync(sampler, phases, output, currentPhase, App.DiagnosticIdleSeconds);
                Hide();
                if (App.DiagnosticHoldSeconds > 0)
                {
                    currentPhase = $"batch-{batch:D2}-hidden-hold";
                    if (App.DiagnosticCompactHolds)
                    {
                        phases.Add(await CaptureCompactHiddenHoldAsync(sampler, compactSamplesPath, currentPhase, App.DiagnosticHoldSeconds));
                        WriteMemoryReport(output, false, currentPhase, phases, null);
                    }
                    else
                    {
                        await CaptureIdlePhaseAsync(sampler, phases, output, currentPhase, App.DiagnosticHoldSeconds);
                    }
                    Hide();
                }
            }

            currentPhase = "hidden-recovery";
            await CaptureIdlePhaseAsync(sampler, phases, output, currentPhase, App.DiagnosticIdleSeconds);
            var modules = sampler.CaptureModules();
            WriteMemoryReport(output, true, "complete", phases, modules, null, sampler.LastModuleCaptureFailureReason);
            _allowClose = true;
            Close();
            Application.Current.Shutdown(0);
        }
        catch (Exception error)
        {
            TryWriteMemoryFailure(output, currentPhase, phases, error.ToString());
            _allowClose = true;
            Close();
            Application.Current.Shutdown(1);
        }
        finally
        {
            _memoryDiagnosticsHttpClient?.Dispose();
            _memoryDiagnosticsHttpClient = null;
        }
    }

    private async Task RunShowHideBatchAsync(int iterations)
    {
        Hide();
        for (var i = 0; i < iterations; i++)
        {
            await ShowForDiagnosticsAsync();
            Hide();
        }
    }

    private async Task ShowForDiagnosticsAsync()
    {
        if (!IsVisible) Show();
        await WaitForLayoutAsync();
    }

    private async Task RunFixedTranslationAsync(string sample, string expected)
    {
        if (!string.Equals(SourceBox.Text, sample, StringComparison.Ordinal)) SourceBox.Text = sample;
        await TranslateNowAsync();
        if (!string.Equals(ResultBox.Text, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Memory diagnostic fixed translation did not complete successfully.");
    }

    private async Task CaptureIdlePhaseAsync(DiagnosticsSampler sampler, List<object> phases, string output, string phase, int seconds)
    {
        if (!IsVisible)
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var row = new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["processId"] = Environment.ProcessId,
            ["isVisible"] = IsVisible,
            ["sampleIntervalMs"] = 1000,
            ["durationSeconds"] = seconds,
            ["checkpoints"] = new List<object> { new Dictionary<string, object?> { ["atMs"] = 0, ["snapshot"] = sampler.Capture() } }
        };
        phases.Add(row);
        var checkpoints = (List<object>)row["checkpoints"]!;
        var watch = Stopwatch.StartNew();
        for (var i = 1; i <= seconds; i++)
        {
            await Task.Delay(1000);
            checkpoints.Add(new Dictionary<string, object?>
            {
                ["atMs"] = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
                ["snapshot"] = sampler.Capture()
            });
        }
        row["endSnapshot"] = sampler.Capture();
        row["durationMs"] = Math.Round(watch.Elapsed.TotalMilliseconds, 3);
        WriteMemoryReport(output, false, phase, phases, null);
    }

    private async Task<object> CaptureHiddenMeasurementWindowAsync(DiagnosticsSampler sampler)
    {
        Hide();
        var settleWatch = Stopwatch.StartNew();
        if (App.DiagnosticSettleSeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(App.DiagnosticSettleSeconds));
        settleWatch.Stop();
        var samples = new List<DiagnosticsSnapshot>(App.DiagnosticWindowSeconds);
        var rows = new List<object>(App.DiagnosticWindowSeconds);
        var watch = Stopwatch.StartNew();
        for (var second = 1; second <= App.DiagnosticWindowSeconds; second++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var snapshot = sampler.Capture();
            samples.Add(snapshot);
            rows.Add(new { second, atMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3), snapshot });
        }
        watch.Stop();
        var result = new Dictionary<string, object?>
        {
            ["phase"] = "hidden-measurement",
            ["isVisible"] = IsVisible,
            ["configuredSettleSeconds"] = App.DiagnosticSettleSeconds,
            ["actualSettleMs"] = Math.Round(settleWatch.Elapsed.TotalMilliseconds, 3),
            ["configuredSampleCount"] = App.DiagnosticWindowSeconds,
            ["actualSampleCount"] = samples.Count,
            ["actualDurationMs"] = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
            ["sampleIntervalSeconds"] = 1,
            ["samples"] = rows,
            ["statistics"] = MemoryWindowStatistics.Compute(samples, App.DiagnosticWindowSeconds),
            ["targetStatus"] = "not-evaluated"
        };
        return result;
    }

    private async Task<object> CaptureCompactHiddenHoldAsync(DiagnosticsSampler sampler, string samplesPath, string phase, int seconds)
    {
        Hide();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var samples = new List<DiagnosticsSnapshot>(seconds + 1);
        var watch = Stopwatch.StartNew();
        await using var stream = new FileStream(samplesPath, FileMode.Append, FileAccess.Write, FileShare.Read, 16 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        async Task CaptureAsync(int second)
        {
            var snapshot = sampler.Capture();
            samples.Add(snapshot);
            var line = JsonSerializer.Serialize(new
            {
                phase,
                second,
                atMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
                snapshot
            });
            await writer.WriteLineAsync(line);
            // Preserve the current phase's completed seconds if the process or
            // host is interrupted during a long hold.
            await writer.FlushAsync();
        }

        await CaptureAsync(0);
        for (var second = 1; second <= seconds; second++)
        {
            await Task.Delay(1000);
            await CaptureAsync(second);
        }
        watch.Stop();
        return new
        {
            phase,
            isVisible = IsVisible,
            durationSeconds = seconds,
            durationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
            sampleIntervalSeconds = 1,
            rawSamplesFile = Path.GetFileName(samplesPath),
            rawSampleCount = samples.Count,
            firstSecond = 0,
            lastSecond = seconds,
            startSnapshot = samples[0],
            endSnapshot = samples[^1],
            statistics = MemoryWindowStatistics.Compute(samples, seconds + 1)
        };
    }

    private static void WriteMemoryReport(string path, bool success, string phase, List<object> phases, object? modules, string? error = null, string? moduleCaptureFailureReason = null)
    {
        object? windowDpi = null;
        if (Application.Current?.MainWindow is MainWindow mainWindow && mainWindow.IsLoaded)
        {
            var dpi = VisualTreeHelper.GetDpi(mainWindow);
            windowDpi = new { dpiX = 96d * dpi.DpiScaleX, dpiY = 96d * dpi.DpiScaleY };
        }
        var report = new Dictionary<string, object?>
        {
            ["success"] = success,
            ["phase"] = phase,
            ["processId"] = Environment.ProcessId,
            ["updatedUtc"] = DateTimeOffset.UtcNow,
            ["configuration"] = new
            {
                batches = App.DiagnosticBatches,
                idleSeconds = App.DiagnosticIdleSeconds,
                holdSeconds = App.DiagnosticHoldSeconds,
                settleSeconds = App.DiagnosticSettleSeconds,
                windowSeconds = App.DiagnosticWindowSeconds,
                compactHolds = App.DiagnosticCompactHolds,
                targetStatus = "not-evaluated",
                processRenderMode = RenderOptions.ProcessRenderMode.ToString(),
                mode = "memory-diagnostics",
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = Environment.OSVersion.VersionString,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                gcServer = System.Runtime.GCSettings.IsServerGC,
                gcLatencyMode = System.Runtime.GCSettings.LatencyMode.ToString(),
                network = "none: fixed in-process HttpMessageHandler",
                clipboard = false,
                userData = false,
                forcedCollection = false,
                workingSetTrim = false
            },
            ["startupStopwatch"] = "App.StartupStopwatch starts when App is loaded; CLR host work before App type load is excluded.",
            ["samplingSemantics"] = "DiagnosticsSampler captures point-in-time process/GC metrics without forcing GC or trimming the working set. Idle checkpoints are one-second samples; hidden-measurement waits for the configured hidden settle, then samples at t=1..N and reports raw-byte window statistics without evaluating a memory target. Compact long holds stream raw samples to a JSONL sidecar and retain only statistics plus endpoints in the process report.",
            ["gcIndexSemantics"] = "DiagnosticsSnapshot.GcIndex 0 can mean there is no valid recent GC event; it must not be interpreted as zero committed bytes. Use GcCommittedBytes, HeapSizeBytes, and FragmentedBytes for memory values.",
            ["windowDpi"] = windowDpi,
            ["phases"] = phases
        };
        if (modules is not null) report["modules"] = modules;
        if (modules is not null) report["moduleCaptureFailureReason"] = moduleCaptureFailureReason;
        if (error is not null) report["error"] = error;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TryWriteMemoryFailure(string path, string phase, List<object> phases, string error)
    {
        try { WriteMemoryReport(path, false, phase, phases, null, error); }
        catch
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonSerializer.Serialize(new { success = false, phase, processId = Environment.ProcessId, error }));
            }
            catch { }
        }
    }

    private sealed class MemoryDiagnosticsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string body = "[[[\"本地诊断译文。\",\"source\",null,null]],null,\"en\"]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
