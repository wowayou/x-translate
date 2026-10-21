using System.Windows.Threading;

namespace XTranslate.App;

/// <summary>One lifetime and one activation wiring for the controller, native input and non-activating icon.</summary>
internal sealed class MouseSelectionSession : IDisposable
{
    internal MouseSelectionIcon Icon { get; }
    internal MouseSelectionController Controller { get; }
    internal MouseInputHooks Input { get; }
    private bool _closed;
    internal MouseSelectionSession(Dispatcher dispatcher, Func<MouseSelectionIcon,MouseSelectionPorts> ports, IMouseHookApi? api = null)
    {
        MouseSelectionController? controller=null; MouseInputHooks? input=null;
        Icon=new MouseSelectionIcon(() => { input?.Flush(); return controller!.ActivateAsync(); });
        Controller=controller=new MouseSelectionController(ports(Icon),dispatcher);
        Input=input=new MouseInputHooks(dispatcher,Controller,api);
    }
    internal bool TryStop()
    {
        var released=Input.TryStop(); Controller.Dispose();
        if (!_closed) { Icon.Close(); _closed=true; }
        return released; // Keep this session until native release is confirmed; callbacks remain rooted on failure.
    }
    public void Dispose()
    {
        if (!TryStop()) throw new InvalidOperationException("Mouse hook cleanup remains pending.");
    }
}
