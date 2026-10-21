using System.Windows;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private MouseSelectionSession? _mouseSession;
    private IMouseHookApi? _mouseApiOverride; // Fixed, scoped smoke boundary. Never creates real hooks in smoke/benchmark.

    private void RefreshTrayState() => _trayMenu?.Refresh(_settings.CaptureHotkeyEnabled,_hotkeyRegistered,_hotkeyText,
        _settings.MouseSelectionEnabled,_mouseSession?.Input.Running == true);
    private bool MouseReady => _settings.MouseSelectionEnabled && _mouseSession?.Input.Running == true && !IsVisible &&
        !_settingsOpen && !_exiting && !_capturing && _pending is null && _capturePending is null && _trayMenu?.Menu.IsOpen != true;
    private bool MouseAppAllowed(long window)
    {
        NativeMethods.GetWindowThreadProcessId(new IntPtr(window),out var pid);
        return pid != 0 && pid != Environment.ProcessId && AllowAppCapture(() => CaptureProcessName(new IntPtr(window)),quiet:true);
    }
    private void ResetMouseSelection() => _mouseSession?.Controller.Reset();
    private bool StopMouseSelection()
    {
        ResetMouseSelection();
        if (_mouseSession is not null && !_mouseSession.TryStop())
        { SetStatus("MouseHookCleanupPending"); RefreshTrayState(); return false; }
        _mouseSession=null; RefreshTrayState(); return true;
    }
    private void ConfigureMouseSelection()
    {
        if (!StopMouseSelection()) return;
        if (!_settings.MouseSelectionEnabled || (App.IsSmokeTest || App.IsBenchmark) && _mouseApiOverride is null) return;
        if (!CaptureAppPolicy.TryParse(_settings.CaptureExcludedApps,out _)) { SetStatus("SettingsExcludedAppsError"); return; }
        _mouseSession=new MouseSelectionSession(Dispatcher,icon => new()
        {
            Ready=() => MouseReady, Foreground=() => NativeMethods.GetForegroundWindow().ToInt64(), Allowed=MouseAppAllowed,
            Read=async (gesture,token) => (await _capture.ProbeMouseAsync(gesture,token)).Text,
            Revalidate=async (gesture,token) => (await _capture.RevalidateMouseAsync(gesture,token)).Text,
            Show=gesture => icon.ShowNear(gesture.End), Hide=() => { if (icon.IsVisible) icon.Hide(); },
            OverIcon=icon.Contains, Accept=AcceptMouseSelection,
            Fault=() => { SetStatus("MouseSelectionUnavailable"); _storage.Log("mouse","capture","UIAutomation",0,"unavailable"); RefreshTrayState(); }
        },_mouseApiOverride);
        if (!_mouseSession.Input.Start()) SetStatus(_mouseSession.Input.CleanupPending ? "MouseHookCleanupPending" : "MouseSelectionUnavailable");
        RefreshTrayState();
    }
    private void AcceptMouseSelection(string text, MouseSelectionGesture gesture)
    {
        ShowForUser(true); NativeMethods.PositionNearCursor(_handle);
        SourceBox.Text=text; TranslateButton.Focus();
        SetStatus("StatusMouseSelectionManual"); // Deliberately never calls TranslateNowAsync, regardless of hotkey auto-send.
        _storage.Log("mouse","capture","UIAutomation",Math.Max(0,Environment.TickCount64-gesture.CompletedAtMs),"accepted_manual");
    }
    private void SetMouseSelectionEnabled(bool enabled)
    {
        if (_settingsOpen || _settings.MouseSelectionEnabled == enabled) return;
        var previous=_settings.MouseSelectionEnabled; _settings.MouseSelectionEnabled=enabled;
        try { _storage.Save(_settings); }
        catch { _settings.MouseSelectionEnabled=previous; SetStatus("StatusSaveFailed"); RefreshTrayState(); return; }
        ConfigureMouseSelection();
    }
}
