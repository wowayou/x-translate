using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.InteropServices;

namespace XTranslate.PerformanceTools;

// Diagnostic exports/tools only. Never linked into the production application.
internal enum DiagnosticPhase { FirstShow, Show, RenderQueue, UpdateLayout, Continuation, Hide }
internal sealed record DiagnosticPhaseInterval(DiagnosticPhase Phase, int NativeThreadId, long Started, long Finished);
internal sealed record DiagnosticPhaseCycle(int Iteration, int NativeThreadId, long Started, long Shown,
    long RenderEntered, long LayoutUpdated, long Continued, long Hidden)
{
    internal DiagnosticPhaseInterval[] Intervals()
    {
        if (Iteration <= 0 || NativeThreadId <= 0 || Started < 0 || Shown < Started || RenderEntered < Shown ||
            LayoutUpdated < RenderEntered || Continued < LayoutUpdated || Hidden < Continued)
            throw new ArgumentException("Invalid diagnostic cycle order/thread/iteration.");
        return [new(DiagnosticPhase.Show, NativeThreadId, Started, Shown),
            new(DiagnosticPhase.RenderQueue, NativeThreadId, Shown, RenderEntered),
            new(DiagnosticPhase.UpdateLayout, NativeThreadId, RenderEntered, LayoutUpdated),
            new(DiagnosticPhase.Continuation, NativeThreadId, LayoutUpdated, Continued),
            new(DiagnosticPhase.Hide, NativeThreadId, Continued, Hidden)];
    }
}

internal sealed record DiagnosticPhaseEmission(DiagnosticPhaseCycle Cycle, long EmitBefore, long EmitAfter);

internal sealed class DiagnosticPhaseTimeline
{
    private readonly Dictionary<int, DiagnosticPhaseInterval[]> _threads;
    internal DiagnosticPhaseTimeline(IEnumerable<DiagnosticPhaseInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var rows = intervals.Take(5002).ToArray();
        if (rows.Length > 5001) throw new ArgumentException("Diagnostic phase interval limit exceeded.");
        foreach (var row in rows)
            if (row is null || !Enum.IsDefined(row.Phase) || row.NativeThreadId <= 0 || row.Started < 0 || row.Finished < row.Started)
                throw new ArgumentException("Invalid diagnostic interval.");
        _threads = rows.Where(row => row.Started != row.Finished).GroupBy(row => row.NativeThreadId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Started).ToArray());
        foreach (var thread in _threads.Values)
            for (var index = 1; index < thread.Length; index++)
                if (thread[index].Started < thread[index - 1].Finished)
                    throw new ArgumentException("Overlapping diagnostic phase intervals.");
    }

    // [start,end): the shared boundary belongs only to the following nonempty phase.
    internal DiagnosticPhase? Locate(int nativeThreadId, long timestamp)
    {
        if (!_threads.TryGetValue(nativeThreadId, out var rows)) return null;
        var left = 0; var right = rows.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (rows[middle].Started <= timestamp) left = middle + 1;
            else right = middle;
        }
        return left > 0 && timestamp < rows[left - 1].Finished ? rows[left - 1].Phase : null;
    }
}

[EventSource(Name = DiagnosticPhaseEvents.ProviderName)]
internal sealed class DiagnosticPhaseEvents : EventSource
{
    internal const string ProviderName = "XTranslate-Diagnostic-PhaseTrace";
    internal static readonly DiagnosticPhaseEvents Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Cycle(int iteration, int nativeThreadId, long started, long shown, long renderEntered,
        long layoutUpdated, long continued, long hidden, long frequency, long emitBefore) =>
        WriteEvent(1, new object?[] { iteration, nativeThreadId, started, shown, renderEntered, layoutUpdated, continued, hidden, frequency, emitBefore });

    // Write only after the cycle's measured end. Allocation/emission between cycles is still observer overhead.
    [NonEvent]
    internal static DiagnosticPhaseEmission Emit(DiagnosticPhaseCycle cycle)
    {
        _ = cycle.Intervals();
        var before = Stopwatch.GetTimestamp();
        Log.Cycle(cycle.Iteration, cycle.NativeThreadId, cycle.Started, cycle.Shown, cycle.RenderEntered,
            cycle.LayoutUpdated, cycle.Continued, cycle.Hidden, Stopwatch.Frequency, before);
        return new(cycle, before, Stopwatch.GetTimestamp());
    }

    [DllImport("kernel32.dll")]
    internal static extern int GetCurrentThreadId();
}
