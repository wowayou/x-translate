using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using Forms = System.Windows.Forms;

namespace MemoryBaseline;

internal static class TrayHostFactory
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IDisposable CreateNoInlining() => new TrayHost();
}

internal sealed class TrayHost : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayHost()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "xtranslate.ico");
        _icon = new Icon(iconPath, 16, 16);
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(new Forms.ToolStripMenuItem("Open"));
        _menu.Items.Add(new Forms.ToolStripMenuItem("Settings"));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("Exit"));
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "XTranslate",
            ContextMenuStrip = _menu,
            // Keep the production tray path real; the process itself remains
            // off-screen and never activates a user window.
            Visible = true
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
