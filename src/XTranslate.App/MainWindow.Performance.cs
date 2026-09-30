using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private int _benchmarkStarted;
    private HttpClient? _benchmarkHttpClient;

    // This is deliberately a local WPF benchmark. It never reads clipboard,
    // selection, user source text, settings credentials, or sends a request to
    // the network. The handler below ignores request contents entirely.
    internal async Task RunBenchmarkAsync(string? output, double loadedMs)
    {
        if (Interlocked.Exchange(ref _benchmarkStarted, 1) != 0) return;
        output = Path.GetFullPath(output ?? Path.Combine(AppContext.BaseDirectory, "benchmark-result.json"));
        try
        {
            var contentRenderedMs = App.StartupStopwatch.Elapsed.TotalMilliseconds;
            _settings = new UserSettings
            {
                Translation = new TranslationSettings
                {
                    Provider = ProviderKind.GoogleWeb,
                    TargetLanguage = "zh-CN"
                }
            };
            _benchmarkHttpClient = new HttpClient(new BenchmarkHandler());
            _service.Dispose();
            _service = new TranslationService(_benchmarkHttpClient);

            // The initial window is already shown off-screen by App. This wait is
            // DispatcherPriority.Render + UpdateLayout: a layout completion point,
            // not a compositor frame and not a hotkey-to-popup latency.
            await WaitForLayoutAsync();
            var visibleIdle = await MeasureIdleAsync("visible");

            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            var hiddenIdle = await MeasureIdleAsync("hidden");

            const int popupIterations = 1000;
            var popupBefore = ReadResources();
            var popupDurations = new List<double>(popupIterations);
            for (var i = 0; i < popupIterations; i++)
            {
                var watch = Stopwatch.StartNew();
                Show();
                await WaitForLayoutAsync();
                Hide();
                watch.Stop();
                popupDurations.Add(watch.Elapsed.TotalMilliseconds);
            }
            var popupAfter = ReadResources();

            const int translationIterations = 200;
            const string sample = "Select, translate, and keep working without leaving your current window.";
            const string expectedTranslation = "本地基准译文。";
            Show();
            await WaitForLayoutAsync();
            SourceBox.Text = sample;
            var translationBefore = ReadResources();
            var translationDurations = new List<double>(translationIterations);
            var successfulTranslations = 0;
            for (var i = 0; i < translationIterations; i++)
            {
                var watch = Stopwatch.StartNew();
                await TranslateNowAsync();
                watch.Stop();
                translationDurations.Add(watch.Elapsed.TotalMilliseconds);
                if (!string.Equals(ResultBox.Text, expectedTranslation, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Deterministic translation failed at iteration {i + 1}: {StatusLabel.Text}");
                successfulTranslations++;
            }
            var translationAfter = ReadResources();
            var recoveryIdle = await MeasureIdleAsync("visible_after_translation");

            var report = new
            {
                success = true,
                completedUtc = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId,
                startup = new
                {
                    loadedMs = RoundOrNull(loadedMs),
                    contentRenderedMs = Math.Round(contentRenderedMs, 3),
                    origin = "App.StartupStopwatch starts when the App type is loaded; CLR host/process work before that is excluded.",
                    contentRenderedOneShot = true
                },
                machine = new
                {
                    os = Environment.OSVersion.VersionString,
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    dotnet = Environment.Version.ToString(),
                    osArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    processorCount = Environment.ProcessorCount,
                    stopwatchFrequency = Stopwatch.Frequency
                },
                parameters = new
                {
                    idleSeconds = 5,
                    idleSampleIntervalMs = 200,
                    popupIterations,
                    translationIterations,
                    translationSuccesses = successfulTranslations,
                    benchmarkData = "isolated benchmark-data directory",
                    popupTimingScope = "off-screen Show/Hide plus DispatcherPriority.Render + UpdateLayout layout completion; no compositor, screen, or hotkey latency",
                    network = "none: injected HttpClient handler ignores request contents and returns a fixed local response",
                    forcedCollection = false,
                    workingSetTrim = false,
                    processRenderMode = System.Windows.Media.RenderOptions.ProcessRenderMode.ToString()
                },
                idle = new { visible = visibleIdle, hidden = hiddenIdle, recovery = recoveryIdle },
                popup = new { latencyMs = Summarize(popupDurations), resources = ResourceChange(popupBefore, popupAfter) },
                translation = new
                {
                    latencyMs = Summarize(translationDurations),
                    resources = ResourceChange(translationBefore, translationAfter),
                    successfulTranslations
                },
                note = "This report does not claim network performance, real foreground hotkey latency, screen rendering latency, OCR, or an eight-hour soak."
            };
            WriteReport(output, report);
            _allowClose = true;
            Close();
            Application.Current.Shutdown(0);
        }
        catch (Exception error)
        {
            try { WriteReport(output, new { success = false, completedUtc = DateTimeOffset.UtcNow, error = error.ToString() }); }
            catch { /* Preserve the original benchmark failure when output is unavailable. */ }
            _allowClose = true;
            Close();
            Application.Current.Shutdown(1);
        }
        finally
        {
            _benchmarkHttpClient?.Dispose();
            _benchmarkHttpClient = null;
        }
    }

    private async Task WaitForLayoutAsync()
    {
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
    }

    private async Task<BenchmarkIdleResult> MeasureIdleAsync(string state)
    {
        using var process = Process.GetCurrentProcess();
        var start = ReadResources(process);
        var samples = new List<BenchmarkSample>();
        var watch = Stopwatch.StartNew();
        var previousCpu = process.TotalProcessorTime;
        var previousAt = watch.Elapsed;
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(200);
            process.Refresh();
            var now = watch.Elapsed;
            var cpu = process.TotalProcessorTime;
            var elapsedMs = Math.Max(0.001, (now - previousAt).TotalMilliseconds);
            var cpuMs = Math.Max(0, (cpu - previousCpu).TotalMilliseconds);
            var cpuPercent = cpuMs / elapsedMs / Math.Max(1, Environment.ProcessorCount) * 100d;
            samples.Add(new BenchmarkSample(now.TotalMilliseconds, ReadResources(process), Math.Round(cpuPercent, 3)));
            previousAt = now;
            previousCpu = cpu;
        }
        var end = ReadResources(process);
        return new BenchmarkIdleResult
        {
            State = state,
            DurationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
            Samples = samples,
            Start = start,
            End = end,
            Delta = ResourceChange(start, end)
        };
    }

    private static BenchmarkResource ReadResources()
    {
        using var process = Process.GetCurrentProcess();
        return ReadResources(process);
    }

    private static BenchmarkResource ReadResources(Process process)
    {
        process.Refresh();
        return new BenchmarkResource
        {
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            ManagedHeapBytes = GC.GetTotalMemory(false),
            Threads = process.Threads.Count,
            Handles = process.HandleCount,
            GdiObjects = GetGuiResources(process.Handle, 0),
            UserObjects = GetGuiResources(process.Handle, 1)
        };
    }

    private static object ResourceChange(BenchmarkResource before, BenchmarkResource after) => new
    {
        before,
        after,
        delta = new
        {
            workingSetBytes = after.WorkingSetBytes - before.WorkingSetBytes,
            privateBytes = after.PrivateBytes - before.PrivateBytes,
            managedHeapBytes = after.ManagedHeapBytes - before.ManagedHeapBytes,
            threads = after.Threads - before.Threads,
            handles = after.Handles - before.Handles,
            gdiObjects = after.GdiObjects - before.GdiObjects,
            userObjects = after.UserObjects - before.UserObjects
        }
    };

    private static object Summarize(List<double> values)
    {
        if (values.Count == 0) return new { count = 0 };
        var sorted = values.OrderBy(x => x).ToArray();
        static double Quantile(double[] sorted, double p)
        {
            var position = (sorted.Length - 1) * p;
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper) return sorted[lower];
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        }
        return new
        {
            count = sorted.Length,
            p50 = Math.Round(Quantile(sorted, .50), 3),
            p90 = Math.Round(Quantile(sorted, .90), 3),
            p95 = Math.Round(Quantile(sorted, .95), 3),
            p99 = Math.Round(Quantile(sorted, .99), 3),
            max = Math.Round(sorted[^1], 3)
        };
    }

    private static double? RoundOrNull(double value) => double.IsNaN(value) ? null : Math.Round(value, 3);

    private static void WriteReport(string path, object report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class BenchmarkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string body = "[[[\"本地基准译文。\",\"source\",null,null]],null,\"en\"]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class BenchmarkResource
    {
        public long WorkingSetBytes { get; init; }
        public long PrivateBytes { get; init; }
        public long ManagedHeapBytes { get; init; }
        public int Threads { get; init; }
        public int Handles { get; init; }
        public int GdiObjects { get; init; }
        public int UserObjects { get; init; }
    }

    private sealed class BenchmarkSample
    {
        public BenchmarkSample(double elapsedMs, BenchmarkResource resources, double cpuPercent)
        {
            ElapsedMs = elapsedMs;
            Resources = resources;
            CpuPercent = cpuPercent;
        }
        public double ElapsedMs { get; }
        public BenchmarkResource Resources { get; }
        public double CpuPercent { get; }
    }

    private sealed class BenchmarkIdleResult
    {
        public string State { get; init; } = "";
        public double DurationMs { get; init; }
        public List<BenchmarkSample> Samples { get; init; } = new();
        public BenchmarkResource Start { get; init; } = new();
        public BenchmarkResource End { get; init; } = new();
        public object Delta { get; init; } = new { };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetGuiResources(IntPtr process, uint flags);
}
