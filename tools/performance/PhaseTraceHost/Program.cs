using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using XTranslate.PerformanceTools;

static string Local(string path)
{
    var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!;
    if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || new DriveInfo(root).DriveType == DriveType.Network)
        throw new ArgumentException("Trusted local drive required.");
    for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Diagnostic links/reparse paths refused.");
    return full;
}
static void Save(string path, object value)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
}
static string Sha(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
static long RawQpc(Microsoft.Diagnostics.Tracing.TraceEvent data)
{
    // TraceEvent discourages raw clocks for general consumers. This preflight specifically proves
    // raw identity against Stopwatch emission brackets; relative milliseconds cannot prove that.
    // Keep the exception at this one API boundary, with all other warnings still errors.
#pragma warning disable CS0618
    return data.TimeStampQPC;
#pragma warning restore CS0618
}

if (args.Length != 2 || args[0] is not ("self-test" or "fixture" or "capture-fixture"))
    throw new ArgumentException("self-test <fresh report> | fixture <fresh report> | capture-fixture <fresh directory>; no product capture in this preflight.");
var target = Local(args[1]);
if (Path.Exists(target) || !Directory.Exists(Path.GetDirectoryName(target))) throw new IOException("Fresh local evidence required.");
if (args[0] == "self-test") return PhaseTraceSelfTests.Run(target);
if (args[0] == "fixture")
{
    var thread = DiagnosticPhaseEvents.GetCurrentThreadId();
    var rows = new List<DiagnosticPhaseEmission>();
    for (var iteration = 1; iteration <= 10; iteration++)
    {
        var started = Stopwatch.GetTimestamp(); Thread.Sleep(4);
        var shown = Stopwatch.GetTimestamp(); Thread.Sleep(4);
        var entered = Stopwatch.GetTimestamp(); Thread.Sleep(4);
        var updated = Stopwatch.GetTimestamp(); Thread.Sleep(4);
        var continued = Stopwatch.GetTimestamp(); Thread.Sleep(4);
        var hidden = Stopwatch.GetTimestamp();
        rows.Add(DiagnosticPhaseEvents.Emit(new(iteration, thread, started, shown, entered, updated, continued, hidden)));
    }
    Save(target, new { success = true, fixtureOnly = true, processId = Environment.ProcessId,
        completedUtc = DateTimeOffset.UtcNow, stopwatchFrequency = Stopwatch.Frequency, nativeThreadId = thread,
        rows, productProcessesStarted = 0, scope = "Controlled console sleeps; no WPF/product, CPU or performance acceptance." });
    return 0;
}

Directory.CreateDirectory(target);
var executable = Local(Environment.ProcessPath ?? throw new IOException("Apphost required."));
Require(Path.GetFileName(executable).Equals("PhaseTraceHost.exe", StringComparison.OrdinalIgnoreCase), "Use the compiled local apphost.");
var reportPath = Path.Combine(target, "fixture.json");
var tracePath = Path.Combine(target, "profile.nettrace");
var before = Sha(executable);
var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
start.ArgumentList.Add("fixture"); start.ArgumentList.Add(reportPath);
start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
start.Environment["DOTNET_ROOT"] = @"D:\Dev\x-translate\.tools\dotnet";
start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
start.Environment.Remove("DOTNET_DiagnosticPorts");
var providers = new[] {
    new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational, 0),
    new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x19),
    new EventPipeProvider(DiagnosticPhaseEvents.ProviderName, EventLevel.Informational, -1)
};
Save(Path.Combine(target, "config.json"), new { fixtureOnly = true, executable, executableSha256 = before,
    arguments = start.ArgumentList.ToArray(), timeoutSeconds = 60, attachTimeoutSeconds = 10,
    traceMaximumBytes = 67108864, runtimeBufferMiB = 16, productProcessesStarted = 0,
    downloads = false, symbols = false, userProcesses = false, releaseAccepted = false });
var watch = Stopwatch.StartNew(); var startedUtc = DateTimeOffset.UtcNow;
using var child = Process.Start(start) ?? throw new IOException("Fixture child missing.");
var childId = child.Id; EventPipeSession? session = null;
try
{
    // The suspended child is already ours: even failure to persist its receipt must run cleanup.
    Save(Path.Combine(target, "owned-child.json"), new { processId = childId, executable, startedUtc,
        childStartUtc = child.StartTime.ToUniversalTime(), fixtureOnly = true });
    var remaining = TimeSpan.FromSeconds(60) - watch.Elapsed;
    Require(remaining > TimeSpan.Zero, "Fixture process creation exhausted deadline.");
    using var deadline = new CancellationTokenSource(remaining);
    var client = new DiagnosticsClient(childId);
    using (var attach = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
    {
        attach.CancelAfter(TimeSpan.FromSeconds(10));
        session = await client.StartEventPipeSessionAsync(providers, requestRundown: true, circularBufferMB: 16, attach.Token);
        client.ResumeRuntime();
    }
    var copy = Task.Run(async () => {
        await using var stream = new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var buffer = new byte[65536]; long bytes = 0;
        while (true)
        {
            var count = await session.EventStream.ReadAsync(buffer, deadline.Token);
            if (count == 0) break;
            if ((bytes += count) > 67108864) throw new IOException("Trace byte cap exceeded.");
            await stream.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
        }
    }, deadline.Token);
    var exit = child.WaitForExitAsync(deadline.Token);
    var first = await Task.WhenAny(copy, exit);
    if (first == copy) await copy;
    await exit; await copy;
    Require(child.ExitCode == 0 && watch.Elapsed.TotalSeconds < 60, "Fixture failed or exceeded deadline.");
    Require(Sha(executable) == before, "Fixture apphost changed.");
    Save(Path.Combine(target, "execution.json"), new { processId = childId, startedUtc, finishedUtc = DateTimeOffset.UtcNow,
        exitCode = child.ExitCode, elapsedSeconds = watch.Elapsed.TotalSeconds, traceSha256 = Sha(tracePath), reportSha256 = Sha(reportPath),
        fixtureOnly = true, productProcessesStarted = 0, releaseAccepted = false });
}
catch (Exception error)
{
    Save(Path.Combine(target, "capture-failure.json"), new { fixtureOnly = true, processId = childId, error = error.ToString(), retry = false });
    throw;
}
finally
{
    if (!child.HasExited) { child.Kill(); if (!child.WaitForExit(5000)) throw new IOException("Owned fixture did not exit after one kill."); }
    session?.Dispose();
}

var etlxPath = Path.Combine(target, "profile.etlx");
using (var conversion = new StreamWriter(Path.Combine(target, "conversion.log")))
    TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath, new TraceLogOptions { ConversionLog = conversion });
Require(new FileInfo(etlxPath).Length <= 67108864, "Converted trace byte cap exceeded.");
using var trace = new TraceLog(etlxPath);
using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
var report = document.RootElement;
Require(report.GetProperty("success").GetBoolean() && report.GetProperty("fixtureOnly").GetBoolean() &&
    report.GetProperty("processId").GetInt32() == childId && report.GetProperty("rows").GetArrayLength() == 10, "Fixture report differs.");
var frequency = report.GetProperty("stopwatchFrequency").GetInt64();
var nativeThreadId = report.GetProperty("nativeThreadId").GetInt32();
Require(frequency > 0 && nativeThreadId > 0, "Fixture clock/thread metadata invalid.");
var emissions = report.GetProperty("rows").EnumerateArray().ToDictionary(row => row.GetProperty("Cycle").GetProperty("Iteration").GetInt32(), row => row);
var markerRows = new List<object>(); var phases = new List<DiagnosticPhaseInterval>();
var seen = new HashSet<int>(); var samples = new List<object>();
long? firstQpc = null; double? firstRelativeMs = null;
foreach (var data in trace.Events)
{
    if (data.ProcessID != childId || data.ProviderName != DiagnosticPhaseEvents.ProviderName || data.EventName != "Cycle") continue;
    var iteration = Convert.ToInt32(data.PayloadByName("iteration"), CultureInfo.InvariantCulture);
    Require(seen.Add(iteration) && emissions.ContainsKey(iteration), "Missing/duplicate/unexpected phase event.");
    var emission = emissions[iteration]; var cycle = emission.GetProperty("Cycle");
    var names = new[] { "started", "shown", "renderEntered", "layoutUpdated", "continued", "hidden" };
    var fields = new[] { "Started", "Shown", "RenderEntered", "LayoutUpdated", "Continued", "Hidden" };
    var raw = names.Select(name => Convert.ToInt64(data.PayloadByName(name), CultureInfo.InvariantCulture)).ToArray();
    for (var index = 0; index < raw.Length; index++) Require(raw[index] == cycle.GetProperty(fields[index]).GetInt64(), "Trace/JSON raw boundary mismatch.");
    Require(data.ThreadID == nativeThreadId && cycle.GetProperty("NativeThreadId").GetInt32() == nativeThreadId &&
        Convert.ToInt32(data.PayloadByName("nativeThreadId"), CultureInfo.InvariantCulture) == nativeThreadId &&
        Convert.ToInt64(data.PayloadByName("frequency"), CultureInfo.InvariantCulture) == frequency, "Trace native thread/frequency mismatch.");
    var emitBefore = emission.GetProperty("EmitBefore").GetInt64(); var emitAfter = emission.GetProperty("EmitAfter").GetInt64();
    Require(Convert.ToInt64(data.PayloadByName("emitBefore"), CultureInfo.InvariantCulture) == emitBefore &&
        RawQpc(data) >= emitBefore && RawQpc(data) <= emitAfter, "Trace QPC is outside raw emission bracket; stop, no clock-offset fallback.");
    firstQpc ??= RawQpc(data); firstRelativeMs ??= data.TimeStampRelativeMSec;
    var rawDifferenceMs = (RawQpc(data) - firstQpc.Value) * 1000d / frequency;
    Require(Math.Abs(rawDifferenceMs - (data.TimeStampRelativeMSec - firstRelativeMs.Value)) < 0.000001, "Trace clock frequency/relative time mismatch.");
    phases.AddRange(new DiagnosticPhaseCycle(iteration, nativeThreadId, raw[0], raw[1], raw[2], raw[3], raw[4], raw[5]).Intervals());
    markerRows.Add(new { iteration, nativeThreadId, rawBoundaries = raw, eventQpc = RawQpc(data),
        eventRelativeMs = data.TimeStampRelativeMSec, emitBefore, emitAfter, rawClockBracketPassed = true });
}
Require(seen.SetEquals(Enumerable.Range(1, 10)) && trace.EventsLost == 0 && !trace.Truncated, "Incomplete marker/trace coverage.");
var timeline = new DiagnosticPhaseTimeline(phases);
var groups = new Dictionary<string, int>(); var inclusive = new Dictionary<string, Dictionary<string, int>>();
foreach (var data in trace.Events)
{
    if (data.ProcessID != childId || data.ProviderName != "Microsoft-DotNETCore-SampleProfiler") continue;
    Require(samples.Count < 100000, "Sample event cap exceeded.");
    var phase = timeline.Locate(data.ThreadID, RawQpc(data))?.ToString() ?? "Other";
    var type = string.Join(",", data.PayloadNames.Select((name, index) => name + "=" + data.PayloadValue(index)));
    var frames = new List<string>(); var stack = trace.GetCallStackForEvent(data);
    while (stack is not null && frames.Count < 128)
    {
        var address = stack.CodeAddress;
        frames.Add(string.IsNullOrEmpty(address.FullMethodName) ? address.ModuleName + "!0x" + address.Address.ToString("X") : address.FullMethodName);
        stack = stack.Caller;
    }
    var group = phase + "/" + type; groups[group] = groups.GetValueOrDefault(group) + 1;
    if (!inclusive.TryGetValue(group, out var counts)) inclusive[group] = counts = new();
    foreach (var frame in frames.Distinct()) counts[frame] = counts.GetValueOrDefault(frame) + 1;
    samples.Add(new { timestampQpc = RawQpc(data), nativeThreadId = data.ThreadID, phase, sampleType = type, frames });
}
Require(samples.Count > 0 && new[] { "Show/", "RenderQueue/", "UpdateLayout/", "Continuation/", "Hide/" }
    .All(prefix => groups.Any(group => group.Key.StartsWith(prefix, StringComparison.Ordinal) && group.Value > 0)), "Missing controlled-phase sample positive control.");
Save(Path.Combine(target, "analysis.json"), new { fixtureOnly = true, clockIdentityValidated = true, exactPhaseAssociationValidated = true,
    processId = childId, frequency, nativeThreadId, eventsLost = trace.EventsLost, truncated = trace.Truncated,
    sampleProfileIntervalMs = trace.SampleProfileInterval.TotalMilliseconds, markers = markerRows, samples, groups,
    inclusive = inclusive.ToDictionary(group => group.Key, group => group.Value.OrderByDescending(item => item.Value).Take(40).ToArray()),
    symbolsDownloaded = false, productProcessesStarted = 0, performanceNoRegressionAccepted = false, releaseAccepted = false,
    limitations = "Controlled console only. Half-open raw tick intervals/native threads are exact; sampling/observer lag still exists. Sample counts/types are not CPU milliseconds or cause attribution." });
Console.WriteLine($"PASS fixture trace: 10 raw-clock brackets, 50 exact phase intervals, {samples.Count} samples; no product/CPU/release acceptance");
return 0;
