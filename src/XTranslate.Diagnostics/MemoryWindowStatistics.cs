namespace XTranslate.Diagnostics;

/// <summary>Statistics for one independent, time ordered hidden measurement window.</summary>
public sealed record MemoryMetricStatistics(
    int ValidSamples,
    int MissingSamples,
    long? MinBytes,
    decimal? MedianBytes,
    long? MaxBytes);

public sealed record MemoryWindowStatistics(
    int ConfiguredSampleCount,
    int ActualSampleCount,
    bool IsComplete,
    bool MeasurementComplete,
    MemoryMetricStatistics WorkingSet,
    MemoryMetricStatistics PrivateBytes,
    MemoryMetricStatistics PrivateWorkingSet,
    long? PrivateBytesStartBytes,
    long? PrivateBytesEndBytes,
    long? PrivateBytesDeltaBytes,
    string TargetStatus = "not-evaluated")
{
    public static MemoryWindowStatistics Compute(
        IReadOnlyList<DiagnosticsSnapshot> samples,
        int configuredSampleCount)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (configuredSampleCount < 1) throw new ArgumentOutOfRangeException(nameof(configuredSampleCount));

        var ws = Summarize(samples.Select(x => (long?)x.WorkingSetBytes));
        var privateBytes = Summarize(samples.Select(x => (long?)x.PrivateBytes));
        var pws = Summarize(samples.Select(x => x.PrivateWorkingSetBytes));
        long? start = samples.Count == 0 ? null : samples[0].PrivateBytes;
        long? end = samples.Count == 0 ? null : samples[^1].PrivateBytes;
        long? delta = start is long first && end is long last ? last - first : null;

        return new MemoryWindowStatistics(
            configuredSampleCount,
            samples.Count,
            samples.Count == configuredSampleCount,
            samples.Count == configuredSampleCount && pws.ValidSamples == configuredSampleCount,
            ws,
            privateBytes,
            pws,
            start,
            end,
            delta);
    }

    private static MemoryMetricStatistics Summarize(IEnumerable<long?> values)
    {
        var all = values.ToArray();
        var valid = all.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToArray();
        decimal? median = valid.Length == 0
            ? null
            : valid.Length % 2 == 1
                ? (decimal)valid[valid.Length / 2]
                : ((decimal)valid[(valid.Length / 2) - 1] + valid[valid.Length / 2]) / 2m;
        return new MemoryMetricStatistics(
            valid.Length,
            all.Length - valid.Length,
            valid.Length == 0 ? null : valid[0],
            median,
            valid.Length == 0 ? null : valid[^1]);
    }
}
