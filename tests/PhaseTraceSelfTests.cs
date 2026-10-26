using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Text.Json;
using XTranslate.PerformanceTools;

internal static class PhaseTraceSelfTests
{
    private sealed class Listener : EventListener
    {
        internal readonly List<EventWrittenEventArgs> Rows = new();
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == DiagnosticPhaseEvents.ProviderName) EnableEvents(source, EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data) => Rows?.Add(data);
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Phase trace assertion failed."); }
    internal static int Run(string report)
    {
        var rows = new List<object>(); var failed = 0;
        void Check(string name, Action action)
        {
            try { action(); rows.Add(new { name, passed = true, error = (string?)null }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; rows.Add(new { name, passed = false, error = error.ToString() }); Console.WriteLine("FAIL " + name); }
        }
        void Reject(string name, Action action) => Check(name, () => {
            try { action(); } catch (ArgumentException) { return; }
            throw new InvalidOperationException("Invalid phase interval accepted.");
        });
        var cycle = new DiagnosticPhaseCycle(1, 11, 100, 110, 120, 120, 130, 140);
        var timeline = new DiagnosticPhaseTimeline(cycle.Intervals());
        foreach (var (tick, phase) in new (long, DiagnosticPhase?)[] {
            (99, null), (100, DiagnosticPhase.Show), (109, DiagnosticPhase.Show), (110, DiagnosticPhase.RenderQueue),
            (119, DiagnosticPhase.RenderQueue), (120, DiagnosticPhase.Continuation), (129, DiagnosticPhase.Continuation),
            (130, DiagnosticPhase.Hide), (139, DiagnosticPhase.Hide), (140, null) })
            Check("Half-open boundary " + tick, () => Require(timeline.Locate(11, tick) == phase));
        Check("Wrong native thread is never assigned", () => Require(timeline.Locate(12, 105) is null));
        Check("Independent threads may overlap in time", () => {
            var separate = new DiagnosticPhaseTimeline([new(DiagnosticPhase.Show, 11, 100, 120), new(DiagnosticPhase.Hide, 12, 100, 120)]);
            Require(separate.Locate(11, 110) == DiagnosticPhase.Show && separate.Locate(12, 110) == DiagnosticPhase.Hide);
        });
        Check("Adjacent cycles have one owner at the shared boundary", () => {
            var next = new DiagnosticPhaseCycle(2, 11, 140, 150, 160, 170, 180, 190);
            var adjacent = new DiagnosticPhaseTimeline(cycle.Intervals().Concat(next.Intervals()));
            Require(adjacent.Locate(11, 140) == DiagnosticPhase.Show && adjacent.Locate(11, 190) is null);
        });
        Check("Gap and negative sample ticks remain unassigned", () => {
            var gap = new DiagnosticPhaseTimeline([new(DiagnosticPhase.Show, 11, 100, 110), new(DiagnosticPhase.Hide, 11, 120, 130)]);
            Require(gap.Locate(11, 115) is null && gap.Locate(11, -1) is null);
        });
        Check("Zero-length intervals are empty, not ambiguous", () => {
            var empty = new DiagnosticPhaseTimeline([new(DiagnosticPhase.UpdateLayout, 11, 105, 105), new(DiagnosticPhase.Show, 11, 100, 110)]);
            Require(empty.Locate(11, 105) == DiagnosticPhase.Show);
        });
        Check("Large integer timestamps are not rounded through double", () => {
            const long baseline = 9_007_199_254_740_993;
            var large = new DiagnosticPhaseTimeline([new(DiagnosticPhase.Show, 11, baseline, baseline + 2)]);
            Require(large.Locate(11, baseline + 1) == DiagnosticPhase.Show && large.Locate(11, baseline + 2) is null);
        });
        Check("Unsorted valid intervals are indexed correctly", () => {
            var sorted = new DiagnosticPhaseTimeline(cycle.Intervals().Reverse());
            Require(sorted.Locate(11, 100) == DiagnosticPhase.Show && sorted.Locate(11, 130) == DiagnosticPhase.Hide);
        });
        Reject("Same-thread overlaps are refused", () => _ = new DiagnosticPhaseTimeline([new(DiagnosticPhase.Show, 11, 100, 120), new(DiagnosticPhase.Hide, 11, 110, 130)]));
        Reject("Negative interval start refused", () => _ = new DiagnosticPhaseTimeline([new(DiagnosticPhase.Show, 11, -1, 1)]));
        Reject("Reversed interval refused", () => _ = new DiagnosticPhaseTimeline([new(DiagnosticPhase.Show, 11, 101, 100)]));
        Reject("Zero native thread refused", () => _ = new DiagnosticPhaseTimeline([new(DiagnosticPhase.Show, 0, 100, 110)]));
        Reject("Unknown phase refused", () => _ = new DiagnosticPhaseTimeline([new((DiagnosticPhase)99, 11, 100, 110)]));
        Reject("More than 5001 intervals refused", () => _ = new DiagnosticPhaseTimeline(Enumerable.Range(0, 5002).Select(i => new DiagnosticPhaseInterval(DiagnosticPhase.Show, 11, i * 2, i * 2 + 1))));
        Reject("Missing cycle iteration refused", () => _ = (cycle with { Iteration = 0 }).Intervals());
        Reject("Backward raw cycle boundary refused", () => _ = (cycle with { Continued = 119 }).Intervals());
        Reject("Wrong cycle thread refused", () => _ = (cycle with { NativeThreadId = -1 }).Intervals());
        Check("Typed event payload preserves all raw fields", () => {
            _ = DiagnosticPhaseEvents.Log;
            using var listener = new Listener();
            var emitted = DiagnosticPhaseEvents.Emit(cycle);
            var data = listener.Rows.Single(row => row.EventId == 1);
            var payload = data.Payload ?? throw new InvalidOperationException("Missing event payload.");
            var names = data.PayloadNames ?? throw new InvalidOperationException("Missing event field names.");
            Require(data.EventName == "Cycle" && payload.Count == 10);
            Require(names.SequenceEqual(new[] { "iteration", "nativeThreadId", "started", "shown", "renderEntered", "layoutUpdated", "continued", "hidden", "frequency", "emitBefore" }));
            Require(payload[0] is int iteration && iteration == 1 && payload[1] is int thread && thread == 11);
            var expected = new[] { 100L, 110L, 120L, 120L, 130L, 140L, Stopwatch.Frequency, emitted.EmitBefore };
            for (var i = 0; i < expected.Length; i++) Require(payload[i + 2] is long raw && raw == expected[i]);
            Require(emitted.EmitAfter >= emitted.EmitBefore && listener.Rows.All(row => row.EventId != 0));
        });
        using var stream = new System.IO.FileStream(report, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write);
        JsonSerializer.Serialize(stream, new { success = failed == 0, count = rows.Count, failed, checks = rows,
            productProcessesStarted = 0, networkRequests = 0 }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"{rows.Count - failed}/{rows.Count} phase trace checks passed");
        return failed == 0 ? 0 : 1;
    }
}
