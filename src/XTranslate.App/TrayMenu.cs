using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace XTranslate.App;

/// <summary>
/// One small, theme-aware WPF menu shared with the rest of the app. NotifyIcon still owns the
/// Windows tray icon and Explorer-restart handling; it no longer draws a separate WinForms menu.
/// Creating this class does not show an icon, register a hotkey or access a user's clipboard.
/// </summary>
internal sealed class TrayMenu : IDisposable
{
    internal ContextMenu Menu { get; } = new() { Placement = PlacementMode.MousePoint };
    internal MenuItem Open { get; } = new() { FontWeight = FontWeights.SemiBold };
    internal MenuItem Settings { get; } = new();
    internal MenuItem Hotkeys { get; } = new() { IsCheckable = true };
    internal MenuItem About { get; } = new();
    internal MenuItem Exit { get; } = new();
    private bool _disposed;

    public TrayMenu(Action open, Action settings, Action<bool> setHotkeys, Action about, Action exit)
    {
        Menu.Items.Add(Open);
        Menu.Items.Add(new Separator());
        Menu.Items.Add(Hotkeys);
        Menu.Items.Add(Settings);
        Menu.Items.Add(About);
        Menu.Items.Add(new Separator());
        Menu.Items.Add(Exit);
        AutomationProperties.SetAutomationId(Menu, "TrayMenu");
        Wire(Open, "TrayOpen", "\uE8A7", open);
        Wire(Settings, "TraySettings", "\uE713", settings);
        Wire(About, "TrayAbout", "\uE946", about);
        Wire(Exit, "TrayExit", "\uE8BB", exit);
        AutomationProperties.SetAutomationId(Hotkeys, "TrayHotkeys");
        Hotkeys.Click += (_, _) => setHotkeys(Hotkeys.IsChecked);
        Menu.Opened += (_, _) => Menu.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!Menu.IsOpen || _disposed) return;
            FocusManager.SetFocusedElement(Menu, Open);
            System.Windows.Input.Keyboard.Focus(Open);
        }), DispatcherPriority.Input);
    }

    private static void Wire(MenuItem item, string automationId, string symbol, Action command)
    {
        AutomationProperties.SetAutomationId(item, automationId);
        item.Icon = new TextBlock { Text = symbol, FontSize = 16,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
        item.Click += (_, _) => command();
    }

    public void Refresh(bool enabled, bool registered, string hotkey)
    {
        Open.Header = UiLocalization.Get("TrayOpen");
        Open.InputGestureText = AppUpdates.Current.ToString();
        Settings.Header = UiLocalization.Get("TraySettings");
        About.Header = UiLocalization.Get("SettingsTabAbout");
        Hotkeys.Header = UiLocalization.Get("TrayHotkeys");
        Hotkeys.IsChecked = enabled;
        Hotkeys.InputGestureText = enabled ? hotkey : "";
        Hotkeys.ToolTip = UiLocalization.Get(!enabled ? "StatusHotkeyDisabled" : registered ? "SettingsHotkeyHelp" : "StatusHotkeyUnavailable");
        Exit.Header = UiLocalization.Get("TrayExit");
    }

    public void Show(Window owner)
    {
        if (_disposed || Menu.IsOpen) return;
        // Taking foreground before the popup gives Windows its usual outside-click / Escape dismissal.
        // Does not Show/Activate the hidden translation window or perform selection capture.
        var handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        NativeMethods.SetForegroundWindow(handle);
        Menu.PlacementTarget = owner;
        Menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Menu.IsOpen = false;
        Menu.PlacementTarget = null;
    }
}
