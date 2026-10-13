using System.Text;
using System.Windows.Threading;
using XTranslate.Core;

namespace XTranslate.App;

/// <summary>
/// Coalesces worker-thread deltas without posting a Dispatcher operation per token.
/// Created and disposed on the UI thread for one request; never runs while idle.
/// </summary>
internal sealed class StreamingTranslationPreview : IProgress<TranslationProgress>, IDisposable
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly DispatcherTimer _timer;
    private Action<TranslationProgress>? _render;
    private string _model = "";
    private bool _disposed;

    public StreamingTranslationPreview(Dispatcher dispatcher, Action<TranslationProgress> render)
    {
        _render = render;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Report(TranslationProgress value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _text.Append(value.TextDelta);
            _model = value.Model;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        TranslationProgress update;
        lock (_gate)
        {
            if (_disposed || _text.Length == 0) return;
            update = new TranslationProgress(_text.ToString(), _model);
            _text.Clear();
        }
        _render?.Invoke(update);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _text.Clear();
        }
        _timer.Stop();
        _timer.Tick -= OnTick;
        _render = null;
    }
}
