using System.Windows.Threading;
using XTranslate.Core;

namespace XTranslate.App;

internal sealed class MouseSelectionPorts
{
    internal required Func<bool> Ready { get; init; }
    internal required Func<long> Foreground { get; init; }
    internal required Func<long, bool> Allowed { get; init; }
    internal required Func<MouseSelectionGesture, CancellationToken, Task<string?>> Read { get; init; }
    internal Func<MouseSelectionGesture, CancellationToken, Task<string?>>? Revalidate { get; init; }
    internal required Action<MouseSelectionGesture> Show { get; init; }
    internal required Action Hide { get; init; }
    internal required Action<string, MouseSelectionGesture> Accept { get; init; }
    internal required Func<PixelPoint, bool> OverIcon { get; init; }
    internal required Action Fault { get; init; }
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;
    internal Func<bool> Escape { get; init; } = () => (NativeMethods.GetAsyncKeyState(0x1b) & 0x8000) != 0;
}

/// <summary>UI-thread owner of one short-lived offer and at most one probe/revalidation. No HTTP or persistence.</summary>
internal sealed class MouseSelectionController : IDisposable
{
    private readonly MouseSelectionPorts _ports;
    private readonly MouseSelectionGestureTracker _gestures = new();
    private readonly DispatcherTimer _timer;
    private long _ingress;
    private long _generation;
    private CancellationTokenSource? _cancel;
    private bool _busy;
    private MouseSelectionGesture? _active;
    private bool _disposed;
    private (MouseSelectionGesture Gesture, string Text, long Ingress)? _offer;
    internal bool HasOffer => _offer is not null;
    internal bool Busy => _busy;
    internal bool TimerRunning => _timer.IsEnabled;
    internal Task Pending { get; private set; } = Task.CompletedTask;
    internal MouseSelectionController(MouseSelectionPorts ports, Dispatcher dispatcher)
    {
        _ports = ports;
        _timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Pulse();
    }
    // Called before queueing into the Dispatcher. Even a not-yet-delivered event invalidates old async work.
    internal long MarkInput() => Interlocked.Increment(ref _ingress);
    internal void Handle(MouseSelectionInput input, long ingress)
    {
        if (_disposed) return;
        if (!_ports.Ready()) { Reset(); return; }
        if (!input.Injected && _offer is { } offer && (input.Kind is MouseSelectionInputKind.LeftDown or MouseSelectionInputKind.LeftUp) &&
            input.Foreground == offer.Gesture.Window && _ports.OverIcon(input.Point))
        {
            _gestures.Reset(); _offer = (offer.Gesture, offer.Text, ingress); return;
        }
        CancelOffer();
        var gesture = _gestures.Feed(input);
        if (gesture is not null && ingress == Interlocked.Read(ref _ingress) && !_busy && Valid(gesture))
            Pending = ProbeAsync(gesture, ingress);
    }
    private bool Valid(MouseSelectionGesture gesture, bool checkPolicy = true)
    {
        var now = _ports.Clock();
        return !_disposed && _ports.Ready() && _ports.Foreground() == gesture.Window && now >= gesture.CompletedAtMs &&
            now - gesture.CompletedAtMs <= 5000 && (!checkPolicy || _ports.Allowed(gesture.Window));
    }
    private bool Current(MouseSelectionGesture gesture, long generation, long ingress, CancellationToken token) =>
        !token.IsCancellationRequested && generation == _generation && ingress == Interlocked.Read(ref _ingress) && Valid(gesture);
    internal void CancelOffer()
    {
        _generation++; _offer = null; _cancel?.Cancel(); _ports.Hide(); _timer.Stop();
    }
    internal void Reset() { _gestures.Reset(); CancelOffer(); }
    internal void Pulse()
    {
        if (_disposed) return;
        if (_ports.Escape() || !_ports.Ready() || (_offer is { } offer && !Valid(offer.Gesture,false)) ||
            (_active is { } active && !Valid(active,false))) Reset();
    }
    private async Task ProbeAsync(MouseSelectionGesture gesture, long ingress)
    {
        _busy = true; _active = gesture;
        using var cancel = new CancellationTokenSource(); _cancel = cancel;
        var generation = _generation; _timer.Start();
        try
        {
            await Task.Delay(120, cancel.Token);
            if (!Current(gesture,generation,ingress,cancel.Token)) return;
            var text = await _ports.Read(gesture,cancel.Token);
            if (!Current(gesture,generation,ingress,cancel.Token) || string.IsNullOrWhiteSpace(text) || text.Length > 3000) return;
            _offer = (gesture,text,ingress); _ports.Show(gesture);
        }
        catch (OperationCanceledException) { }
        catch { if (!_disposed && generation == _generation) { Reset(); _ports.Fault(); } }
        finally { if (_cancel == cancel) _cancel = null; _busy = false; _active = null; if (_offer is null) _timer.Stop(); }
    }
    internal Task ActivateAsync()
    {
        if (_disposed || _busy || _offer is not { } offer || offer.Ingress != Interlocked.Read(ref _ingress) || !Valid(offer.Gesture))
        { Reset(); return Task.CompletedTask; }
        return Pending = ActivateCoreAsync(offer);
    }
    private async Task ActivateCoreAsync((MouseSelectionGesture Gesture, string Text, long Ingress) offer)
    {
        _busy = true; _active = offer.Gesture; _offer = null; _ports.Hide();
        using var cancel = new CancellationTokenSource(); _cancel = cancel;
        var generation = _generation;
        try
        {
            var current = await (_ports.Revalidate ?? _ports.Read)(offer.Gesture,cancel.Token);
            if (!Current(offer.Gesture,generation,offer.Ingress,cancel.Token) || !string.Equals(current,offer.Text,StringComparison.Ordinal)) return;
            _ports.Accept(offer.Text,offer.Gesture);
        }
        catch (OperationCanceledException) { }
        catch { if (!_disposed && generation == _generation) _ports.Fault(); }
        finally { if (_cancel == cancel) _cancel = null; _busy = false; _active = null; _timer.Stop(); }
    }
    internal void Fault() { Reset(); _ports.Fault(); }
    public void Dispose() { if (_disposed) return; _disposed = true; Reset(); }
}
