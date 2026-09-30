using System.Diagnostics;
using System.Text.Json;
using XTranslate.Diagnostics;

var checks = new List<(string Name, Action Check)>
{
    ("Shared bit 8 is decoded on both pointer widths", TestSharedBit),
    ("Private pages count only non-shared entries", TestPrivatePageCount),
    ("Capture exposes reasonable process and GC values", TestCapture),
    ("CaptureModules reports virtual module metadata", TestModules),
    ("Sampler disposal is enforced", TestDisposal),
    ("Window statistics use valid samples and odd median", TestWindowStatisticsOdd),
    ("Window statistics use even median and preserve missing PWS", TestWindowStatisticsEven),
    ("Window statistics do not let one low value skew median", TestWindowStatisticsRobustMedian),
    ("Window statistics reject invalid configured count", TestWindowStatisticsInvalidCount),
    ("Window statistics preserve empty and partial windows", TestWindowStatisticsBoundaries)
};

var failures = new List<string>();
foreach (var (name, check) in checks)
{
    try
    {
        check();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failures.Add($"{name}: {error.Message}");
        Console.WriteLine($"FAIL {name}: {error.Message}");
    }
}

if (failures.Count != 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine($"{checks.Count}/{checks.Count} diagnostics tests passed");
}

static void TestSharedBit()
{
    Assert(WorkingSetPageDecoder.IsShared(1UL << 8), "x64 Shared bit was not recognized");
    Assert(WorkingSetPageDecoder.IsShared((1UL << 8) | 0x1f, 32), "x86 Shared bit was not recognized");
    Assert(!WorkingSetPageDecoder.IsShared(1UL << 7), "ShareCount bit was mistaken for Shared");
    Assert(!WorkingSetPageDecoder.IsShared(1UL << 9), "Reserved bit was mistaken for Shared");
    AssertThrows<ArgumentOutOfRangeException>(() => WorkingSetPageDecoder.IsShared(0, 48));
}

static void TestPrivatePageCount()
{
    var flags = new[] { 0UL, 1UL << 8, 0x20UL, (1UL << 8) | 0x20 };
    Assert(WorkingSetPageDecoder.CountPrivatePages(flags) == 2, "Unexpected private page count");
}

static void TestCapture()
{
    using var sampler = new DiagnosticsSampler();
    var snapshot = sampler.Capture();
    Assert(snapshot.CapturedUtc <= DateTimeOffset.UtcNow, "Capture timestamp is in the future");
    Assert(snapshot.WorkingSetBytes > 0, "Working set should be positive");
    Assert(snapshot.PrivateBytes > 0, "Private bytes should be positive");
    Assert(snapshot.ManagedAllocatedEstimateBytes >= 0, "Managed estimate should be non-negative");
    Assert(snapshot.GcCommittedBytes >= 0 && snapshot.HeapSizeBytes >= 0 && snapshot.FragmentedBytes >= 0, "GC values should be non-negative");
    Assert(snapshot.GcIndex >= 0, "GC index should be non-negative");
    Assert(snapshot.Generation >= 0, "GC generation should be non-negative");
    Assert(snapshot.Gen0Collections >= 0 && snapshot.Gen1Collections >= 0 && snapshot.Gen2Collections >= 0, "GC counts should be non-negative");
    Assert(snapshot.Threads > 0 && snapshot.Handles > 0, "Thread/handle counts should be positive");
    if (snapshot.PrivateWorkingSetBytes is long privateWorkingSet)
    {
        Assert(privateWorkingSet > 0, "Private working set should be positive when available");
        var tolerance = 2L * 1024 * 1024;
        Assert(privateWorkingSet <= snapshot.WorkingSetBytes + tolerance, "Private working set exceeds working set beyond race tolerance");
        Assert(privateWorkingSet <= snapshot.PrivateBytes + tolerance, "Private working set exceeds private bytes beyond race tolerance");
        Assert(snapshot.PrivateWorkingSetBufferBytes is > 0, "Successful PWS capture should expose its buffer size");
    }
    else
    {
        Assert(!string.IsNullOrWhiteSpace(snapshot.PrivateWorkingSetFailureReason), "PWS failure must not be represented by zero without a reason");
        Assert(snapshot.PrivateWorkingSetBufferBytes is null, "Failed PWS capture should not claim a buffer size");
    }

    var json = JsonSerializer.Serialize(snapshot);
    Assert(json.Contains("WorkingSetBytes", StringComparison.Ordinal), "Snapshot JSON omitted WorkingSetBytes");
    Assert(json.Contains("PrivateWorkingSetBytes", StringComparison.Ordinal), "Snapshot JSON omitted PrivateWorkingSetBytes");
    Assert(json.Contains("GcSnapshotNote", StringComparison.Ordinal), "Snapshot JSON omitted GC snapshot semantics");
}

static void TestModules()
{
    using var sampler = new DiagnosticsSampler(includeModules: true);
    var modules = sampler.CaptureModules()!;
    if (modules.Count > 0)
    {
        Assert(modules.Any(module => !string.IsNullOrWhiteSpace(module.Name)), "Loaded modules should have names");
        Assert(modules.All(module => module.SizeOfImageBytes >= 0), "Module image sizes should be non-negative");
    }
}

static void TestDisposal()
{
    var sampler = new DiagnosticsSampler();
    sampler.Dispose();
    AssertThrows<ObjectDisposedException>(() => sampler.Capture());
    AssertThrows<ObjectDisposedException>(() => sampler.CaptureModules());
}

static void TestWindowStatisticsOdd()
{
    var samples = new[] { Snapshot(10, 100, 50), Snapshot(20, 200, 60), Snapshot(30, 300, null) };
    var stats = MemoryWindowStatistics.Compute(samples, 3);
    Assert(stats.IsComplete, "Complete window was marked partial");
    Assert(stats.WorkingSet.MedianBytes == 20, "Odd median was not selected");
    Assert(stats.PrivateWorkingSet.ValidSamples == 2 && stats.PrivateWorkingSet.MissingSamples == 1, "Missing PWS was not counted");
    Assert(stats.PrivateBytesDeltaBytes == 200, "PrivateBytes delta is not end minus start");
}

static void TestWindowStatisticsEven()
{
    var samples = new[] { Snapshot(1, 100, null), Snapshot(2, 200, 1), Snapshot(3, 300, 2), Snapshot(4, 400, null) };
    var stats = MemoryWindowStatistics.Compute(samples, 4);
    Assert(stats.PrivateWorkingSet.MedianBytes == 1.5m, "Even median was not averaged exactly");
    Assert(stats.PrivateWorkingSet.MinBytes == 1 && stats.PrivateWorkingSet.MaxBytes == 2, "PWS extrema include missing values");
}

static void TestWindowStatisticsRobustMedian()
{
    var samples = Enumerable.Range(0, 5).Select((_, i) => Snapshot(i, 100, i == 0 ? 1 : 100)).ToArray();
    var stats = MemoryWindowStatistics.Compute(samples, 5);
    Assert(stats.PrivateWorkingSet.MedianBytes == 100, "One low sample incorrectly lowered the median");
}

static void TestWindowStatisticsInvalidCount()
{
    AssertThrows<ArgumentOutOfRangeException>(() => MemoryWindowStatistics.Compute(Array.Empty<DiagnosticsSnapshot>(), 0));
}

static void TestWindowStatisticsBoundaries()
{
    var empty = MemoryWindowStatistics.Compute(Array.Empty<DiagnosticsSnapshot>(), 2);
    Assert(!empty.IsComplete && !empty.MeasurementComplete, "Empty window was marked complete");
    Assert(empty.ActualSampleCount == 0 && empty.PrivateWorkingSet.MissingSamples == 0, "Empty window counts are incorrect");
    Assert(empty.TargetStatus == "not-evaluated", "Empty window changed target status");

    var allMissing = MemoryWindowStatistics.Compute(new[] { Snapshot(1, 10, null), Snapshot(2, 20, null) }, 2);
    Assert(allMissing.IsComplete && !allMissing.MeasurementComplete, "All-missing PWS was marked measurement complete");
    Assert(allMissing.PrivateWorkingSet.ValidSamples == 0 && allMissing.PrivateWorkingSet.MissingSamples == 2, "All-missing PWS counts are incorrect");

    var partial = MemoryWindowStatistics.Compute(new[] { Snapshot(1, 10, 1) }, 2);
    Assert(!partial.IsComplete && !partial.MeasurementComplete, "Partial window was marked complete");

    var over = MemoryWindowStatistics.Compute(new[] { Snapshot(1, 10, 1), Snapshot(2, 20, 2), Snapshot(3, 30, 3) }, 2);
    Assert(!over.IsComplete && !over.MeasurementComplete, "Oversized window was marked complete");
}

static DiagnosticsSnapshot Snapshot(long workingSet, long privateBytes, long? privateWorkingSet) => new(
    DateTimeOffset.UtcNow,
    workingSet,
    privateBytes,
    privateWorkingSet,
    privateWorkingSet is null ? "missing for test" : null,
    ManagedAllocatedEstimateBytes: 0,
    GcCommittedBytes: 0,
    HeapSizeBytes: 0,
    FragmentedBytes: 0,
    GcIndex: 0,
    Generation: 0,
    Gen0Collections: 0,
    Gen1Collections: 0,
    Gen2Collections: 0,
    Threads: 1,
    Handles: 1,
    GdiObjects: null,
    UserObjects: null,
    GuiResourceFailureReason: null,
    Modules: null,
    PrivateWorkingSetMethod: "test",
    PrivateWorkingSetBufferBytes: null,
    GcSnapshotNote: "test",
    ModuleCaptureFailureReason: null);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}
