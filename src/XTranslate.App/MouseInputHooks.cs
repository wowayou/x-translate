using System.Runtime.InteropServices;
using System.Windows.Threading;
using XTranslate.Core;

namespace XTranslate.App;

internal interface IMouseHookApi
{
    IntPtr AddMouse(NativeMethods.MouseHookProc callback);
    IntPtr AddForeground(NativeMethods.ForegroundHookProc callback);
    bool RemoveMouse(IntPtr hook);
    bool RemoveForeground(IntPtr hook);
    IntPtr Next(int code, IntPtr message, IntPtr data);
    long WindowAt(PixelPoint point);
    long Foreground();
    long Clock();
}
internal sealed class Win32MouseHookApi : IMouseHookApi
{
    public IntPtr AddMouse(NativeMethods.MouseHookProc callback) => NativeMethods.SetWindowsHookEx(14,callback,NativeMethods.GetModuleHandle(null),0);
    public IntPtr AddForeground(NativeMethods.ForegroundHookProc callback) => NativeMethods.SetWinEventHook(3,3,IntPtr.Zero,callback,0,0,0);
    public bool RemoveMouse(IntPtr hook) => NativeMethods.UnhookWindowsHookEx(hook) || Marshal.GetLastWin32Error() == 1404;
    public bool RemoveForeground(IntPtr hook) => NativeMethods.UnhookWinEvent(hook) || Marshal.GetLastWin32Error() == 1404;
    public IntPtr Next(int code, IntPtr message, IntPtr data) => NativeMethods.CallNextHookEx(IntPtr.Zero,code,message,data);
    public long WindowAt(PixelPoint point) => NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new() { X=point.X,Y=point.Y }),2).ToInt64();
    public long Foreground() => NativeMethods.GetForegroundWindow().ToInt64();
    public long Clock() => Environment.TickCount64;
}

/// <summary>
/// Owned UI-thread hooks. Moves allocate/queue nothing. Other events use an eight-entry mailbox, one scheduled
/// drain, and an ingress barrier. No UIA, process-name inspection, files, network or synthesized input here.
/// </summary>
internal sealed class MouseInputHooks
{
    private readonly Dispatcher _dispatcher;
    private readonly MouseSelectionController _controller;
    private readonly IMouseHookApi _api;
    private readonly NativeMethods.MouseHookProc _mouseCallback;
    private readonly NativeMethods.ForegroundHookProc _foregroundCallback;
    private readonly Queue<(MouseSelectionInput Input, long Ingress)> _queue = new();
    private DispatcherOperation? _drain;
    private IntPtr _mouse, _foreground;
    private GCHandle _self;
    private bool _running, _fault;
    internal bool Running => _running;
    internal bool CleanupPending => _mouse != IntPtr.Zero || _foreground != IntPtr.Zero;
    internal int Queued => _queue.Count;
    internal MouseInputHooks(Dispatcher dispatcher, MouseSelectionController controller, IMouseHookApi? api = null)
    {
        _dispatcher=dispatcher; _controller=controller; _api=api ?? new Win32MouseHookApi();
        _mouseCallback=OnMouse; _foregroundCallback=OnForeground;
    }
    internal bool Start()
    {
        if (_running) return true;
        if (CleanupPending) return false;
        _fault=false; _self=GCHandle.Alloc(this); // Keep callbacks alive even if native removal later fails.
        try
        {
            _mouse=_api.AddMouse(_mouseCallback);
            if (_mouse != IntPtr.Zero) _foreground=_api.AddForeground(_foregroundCallback);
            if (_mouse != IntPtr.Zero && _foreground != IntPtr.Zero) return _running=true;
        }
        catch { _fault=true; }
        TryStop(); return false;
    }
    internal bool TryStop()
    {
        _running=false; _controller.Reset(); _queue.Clear(); _drain?.Abort(); _drain=null;
        try { if (_mouse != IntPtr.Zero && _api.RemoveMouse(_mouse)) _mouse=IntPtr.Zero; }
        catch { _fault=true; }
        try { if (_foreground != IntPtr.Zero && _api.RemoveForeground(_foreground)) _foreground=IntPtr.Zero; }
        catch { _fault=true; }
        if (!CleanupPending && _self.IsAllocated) _self.Free();
        return !CleanupPending;
    }
    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        var msg=message.ToInt64();
        if (code >= 0 && _running && (msg is 0x201 or 0x202 or 0x204 or 0x207 or 0x20a or 0x20b or 0x20e))
        {
            try
            {
                var sample=Marshal.PtrToStructure<NativeMethods.LowLevelMouse>(data);
                var point=new PixelPoint(sample.Point.X,sample.Point.Y);
                var kind=msg == 0x201 ? MouseSelectionInputKind.LeftDown : msg == 0x202 ? MouseSelectionInputKind.LeftUp : MouseSelectionInputKind.Dismiss;
                var injected=(sample.Flags & 3) != 0;
                var window=injected || kind == MouseSelectionInputKind.Dismiss ? 0 : _api.WindowAt(point);
                Enqueue(new(kind,window,_api.Foreground(),point,_api.Clock(),injected));
            }
            catch { FailCallback(); }
        }
        return _api.Next(code,message,data); // Never consume input, including injected or unsupported events.
    }
    private void OnForeground(IntPtr hook, uint evt, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (!_running) return;
        try { Enqueue(new(MouseSelectionInputKind.ForegroundChanged,window.ToInt64(),window.ToInt64(),default,_api.Clock())); }
        catch { FailCallback(); }
    }
    private void FailCallback()
    {
        _fault=true; _running=false; _controller.MarkInput();
        Schedule(); // Reporting/cleanup is deferred; no logging from an unmanaged callback.
    }
    private void Enqueue(MouseSelectionInput input)
    {
        var ingress=_controller.MarkInput();
        if (_queue.Count == 8) { _queue.Clear(); input=input with { Kind=MouseSelectionInputKind.Dismiss }; }
        _queue.Enqueue((input,ingress)); Schedule();
    }
    private void Schedule()
    {
        if (_drain is not null || _dispatcher.HasShutdownStarted) return;
        try { _drain=_dispatcher.BeginInvoke(new Action(Drain),DispatcherPriority.Input); }
        catch (InvalidOperationException) { _fault=true; _running=false; }
    }
    internal void Flush()
    {
        _dispatcher.VerifyAccess(); _drain?.Abort(); _drain=null; Drain();
    }
    private void Drain()
    {
        _drain=null;
        if (_fault) { _queue.Clear(); _controller.Fault(); return; }
        while (_running && _queue.TryDequeue(out var item)) _controller.Handle(item.Input,item.Ingress);
    }
}
