using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace XTranslate.PerformanceTools;

// Linked only into isolated diagnostics and their tests, never the production application.
internal static class DiagnosticPreWait
{
    internal const int MinimumMilliseconds = 5000;
    internal const int MaximumDelayCalls = 16;
    internal sealed record Measurement(long ElapsedTicks, double ElapsedMs, int DelayCalls);

    internal static Task<Measurement> WaitAsync() =>
        WaitAsync(Stopwatch.GetTimestamp, Stopwatch.Frequency, static milliseconds => Task.Delay(milliseconds));

    // Injectable clock/delay let the regression tests reproduce an early timer deterministically.
    internal static async Task<Measurement> WaitAsync(Func<long> getTimestamp, long frequency, Func<int, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(getTimestamp);
        ArgumentNullException.ThrowIfNull(delay);
        if (frequency <= 0 || frequency > long.MaxValue / 5)
            throw new ArgumentOutOfRangeException(nameof(frequency));

        var requiredTicks = checked(5 * frequency);
        var started = getTimestamp();
        long elapsedTicks = 0;
        var calls = 0;
        while (elapsedTicks < requiredTicks)
        {
            if (calls == MaximumDelayCalls)
                throw new TimeoutException("Diagnostic pre-wait did not reach its minimum within the delay-call limit.");

            // A timer can return early. Re-arm only the remaining interval, always rounded up;
            // acceptance uses raw ticks, never a rounded millisecond value or a tolerance.
            var remainingMs = checked((int)decimal.Ceiling((requiredTicks - elapsedTicks) * 1000m / frequency));
            await delay(remainingMs);
            calls++;
            var observedTicks = checked(getTimestamp() - started);
            if (observedTicks < elapsedTicks)
                throw new InvalidOperationException("Diagnostic clock moved backwards.");
            elapsedTicks = observedTicks;
        }
        return new Measurement(elapsedTicks, (double)(elapsedTicks * 1000m / frequency), calls);
    }
}
