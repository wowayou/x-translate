using System.Diagnostics;
using System.Text.Json;
using XTranslate.PerformanceTools;

if (args.Length != 2 || args[1] is not ("synthetic-only" or "with-system-waits"))
    throw new ArgumentException("Require a fresh report path and an explicit local test mode.");
var output = Path.GetFullPath(args[0]);
if (File.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output)))
    throw new IOException("Fresh test evidence required.");

var checks = new List<object>();
var failures = 0;
var realWaits = new List<object>();
async Task Check(string name, Func<Task> action)
{
    try
    {
        await action();
        checks.Add(new { name, passed = true, error = (string?)null });
        Console.WriteLine("PASS " + name);
    }
    catch (Exception error)
    {
        failures++;
        checks.Add(new { name, passed = false, error = error.ToString() });
        Console.WriteLine("FAIL " + name + ": " + error.Message);
    }
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static async Task<DiagnosticPreWait.Measurement> Schedule(long frequency, long baseline, long[] elapsed,
    int[] expectedDelays)
{
    var clock = baseline;
    var calls = new List<int>();
    var result = await DiagnosticPreWait.WaitAsync(() => clock, frequency, milliseconds =>
    {
        calls.Add(milliseconds);
        Require(calls.Count <= elapsed.Length, "Unexpected delay allocation.");
        clock = checked(baseline + elapsed[calls.Count - 1]);
        return Task.CompletedTask;
    });
    Require(calls.SequenceEqual(expectedDelays), "Remaining delay was not rounded up/rechecked.");
    Require(result.DelayCalls == calls.Count && result.ElapsedTicks == elapsed[calls.Count - 1], "Raw measurement changed.");
    Require(result.ElapsedTicks >= checked(5 * frequency) && result.ElapsedMs >= 5000,
        "Wait returned before the unchanged five-second minimum.");
    Require(result.ElapsedMs == result.ElapsedTicks * 1000d / frequency, "Reported duration is not the observed raw clock.");
    return result;
}

const long frequency = 10_000_000;
const long minimumTicks = 5 * frequency;
await Check("Recorded 4999.7219ms early return is rearmed, not accepted", async () =>
    await Schedule(frequency, 0, [49_997_219, minimumTicks], [5000, 1]));
await Check("Both visible and hidden waits independently enforce the minimum", async () =>
{
    for (var phase = 0; phase < 2; phase++)
        await Schedule(frequency, phase * 100_000_000L, [49_997_219, minimumTicks], [5000, 1]);
});
await Check("Exact 5000ms completes without an extra delay", async () =>
    await Schedule(frequency, 0, [minimumTicks], [5000]));
await Check("Overshoot remains raw rather than clamped to 5000ms", async () =>
{
    var result = await Schedule(frequency, 0, [50_012_500], [5000]);
    Require(result.ElapsedMs == 5001.25, "Overshoot concealed.");
});
await Check("One tick below the threshold needs a positive one-millisecond delay", async () =>
    await Schedule(frequency, 0, [minimumTicks - 1, minimumTicks], [5000, 1]));
await Check("Repeated early returns use the remaining interval, not another five seconds", async () =>
    await Schedule(frequency, 0, [10_000_000, 40_000_000, 49_997_219, minimumTicks], [5000, 4000, 1000, 1]));
await Check("Low-frequency clock rounds a fractional remaining millisecond upwards", async () =>
    await Schedule(3, 0, [14, 15], [5000, 334]));
await Check("Non-decimal clock frequency preserves the raw tick threshold", async () =>
    await Schedule(1001, 0, [5004, 5005], [5000, 1]));
await Check("Large timestamp baseline does not affect elapsed duration", async () =>
    await Schedule(frequency, 1_000_000_000_000_000, [minimumTicks - 1, minimumTicks], [5000, 1]));
await Check("Clock that never advances fails after the declared delay-call cap", async () =>
{
    var calls = 0;
    await Reject<TimeoutException>(async () => await DiagnosticPreWait.WaitAsync(() => 0, frequency, _ =>
    { calls++; return Task.CompletedTask; }));
    Require(calls == DiagnosticPreWait.MaximumDelayCalls, "Wait loop was unbounded or cap changed.");
});
await Check("Completion on the final permitted delay is accepted", async () =>
{
    var elapsed = Enumerable.Repeat(0L, DiagnosticPreWait.MaximumDelayCalls).ToArray();
    elapsed[^1] = minimumTicks;
    await Schedule(frequency, 0, elapsed, Enumerable.Repeat(5000, elapsed.Length).ToArray());
});
await Check("Initial delay failure propagates without a retry", async () =>
{
    var calls = 0;
    await Reject<IOException>(async () => await DiagnosticPreWait.WaitAsync(() => 0, frequency, _ =>
    { calls++; return Task.FromException(new IOException("controlled delay failure")); }));
    Require(calls == 1, "Failed delay was retried.");
});
await Check("Rearmed delay failure propagates without claiming completion", async () =>
{
    long clock = 0;
    var calls = 0;
    await Reject<IOException>(async () => await DiagnosticPreWait.WaitAsync(() => clock, frequency, _ =>
    {
        calls++;
        clock = minimumTicks - 1;
        return calls == 1 ? Task.CompletedTask : Task.FromException(new IOException("controlled rearm failure"));
    }));
    Require(calls == 2, "Rearm failure was concealed/retried.");
});
await Check("Cancelled delay propagates without retry", async () =>
{
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var calls = 0;
    await Reject<OperationCanceledException>(async () => await DiagnosticPreWait.WaitAsync(() => 0, frequency, _ =>
    { calls++; return Task.FromCanceled(cancelled.Token); }));
    Require(calls == 1, "Cancellation was retried.");
});
await Check("Clock moving backwards from the start is rejected", async () =>
{
    long clock = 1000;
    await Reject<InvalidOperationException>(async () => await DiagnosticPreWait.WaitAsync(() => clock, frequency, _ =>
    { clock = 999; return Task.CompletedTask; }));
});
await Check("Clock moving backwards after partial progress is rejected", async () =>
{
    long clock = 0;
    var calls = 0;
    await Reject<InvalidOperationException>(async () => await DiagnosticPreWait.WaitAsync(() => clock, frequency, _ =>
    { clock = minimumTicks - ++calls; return Task.CompletedTask; }));
});
await Check("Elapsed subtraction overflow is rejected", async () =>
{
    var clock = long.MinValue;
    await Reject<OverflowException>(async () => await DiagnosticPreWait.WaitAsync(() => clock, frequency, _ =>
    { clock = long.MaxValue; return Task.CompletedTask; }));
});
foreach (var invalid in new[] { 0L, -1L, long.MaxValue / 5 + 1 })
    await Check("Invalid frequency " + invalid + " fails before allocating a delay", async () =>
    {
        var allocated = false;
        await Reject<ArgumentOutOfRangeException>(async () => await DiagnosticPreWait.WaitAsync(() => 0, invalid, _ =>
        { allocated = true; return Task.CompletedTask; }));
        Require(!allocated, "Invalid clock allocated a delay.");
    });
await Check("Null clock is rejected", async () =>
    await Reject<ArgumentNullException>(async () => await DiagnosticPreWait.WaitAsync(null!, frequency, _ => Task.CompletedTask)));
await Check("Null delay is rejected", async () =>
    await Reject<ArgumentNullException>(async () => await DiagnosticPreWait.WaitAsync(() => 0, frequency, null!)));
await Check("Pending asynchronous delay is awaited without polling/busy-spinning", async () =>
{
    long clock = 0;
    var calls = 0;
    var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var wait = DiagnosticPreWait.WaitAsync(() => clock, frequency, _ => { calls++; return pending.Task; });
    Require(!wait.IsCompleted && calls == 1, "Pending timer was not awaited.");
    clock = minimumTicks;
    pending.SetResult();
    var result = await wait;
    Require(result.ElapsedTicks == minimumTicks && result.DelayCalls == 1 && calls == 1, "Async completion changed measurement.");
});

if (args[1] == "with-system-waits")
    for (var phase = 0; phase < 2; phase++)
        await Check("Real Stopwatch/Task.Delay wait " + (phase + 1), async () =>
        {
            var started = Stopwatch.GetTimestamp();
            var result = await DiagnosticPreWait.WaitAsync();
            var externalTicks = Stopwatch.GetTimestamp() - started;
            Require(result.ElapsedTicks >= 5 * Stopwatch.Frequency && result.ElapsedMs >= 5000 &&
                result.DelayCalls is >= 1 and <= DiagnosticPreWait.MaximumDelayCalls && externalTicks >= result.ElapsedTicks &&
                result.ElapsedMs == result.ElapsedTicks * 1000d / Stopwatch.Frequency, "Real pre-wait contract failed.");
            realWaits.Add(new { result, externalTicks, stopwatchFrequency = Stopwatch.Frequency });
        });

using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
    JsonSerializer.Serialize(stream, new { success = failures == 0, count = checks.Count, failed = failures, checks, realWaits,
        mode = args[1], productProcessesStarted = 0, networkRequests = 0 }, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"{checks.Count - failures}/{checks.Count} diagnostic pre-wait checks passed");
return failures == 0 ? 0 : 1;
