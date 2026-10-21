using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private async Task RunMouseSelectionAcceptanceAsync(List<string> checks, string output, Func<int> requests)
    {
        var original=_settings.Copy(); var language=UiLocalization.Preference; var theme=UiAppearance.Preference; var reviewRequested=ReviewRequested;
        var initialRequests=requests(); var originalService=_service;
        using var mouseHandler=new SmokeHandler(); using var mouseClient=new System.Net.Http.HttpClient(mouseHandler);
        _service=new TranslationService(mouseClient); // Fresh mock: an old cooldown must not hide accidental auto-sending.
        try
        {
            RequireStream(!_settings.MouseSelectionEnabled && _mouseSession is null,"Mouse capture was enabled by default or installed hooks in smoke mode");
            var api=new MouseHookFixture(); _mouseApiOverride=api;
            await RunSettingsDialogAsync(dialog =>
            {
                RequireStream(dialog.MouseSelectionBox.IsChecked == false,"Mouse setting did not default off");
                dialog.MouseSelectionBox.IsChecked=true;
                foreach (var lang in new[] {UiLanguage.SimplifiedChinese,UiLanguage.English})
                {
                    UiLocalization.SetPreference(lang); UiAppearance.Apply(lang == UiLanguage.English ? UiTheme.Dark : UiTheme.Light);
                    dialog.Width=dialog.MinWidth; dialog.Height=dialog.MinHeight;
                    dialog.Reveal(dialog.MouseSelectionHelp); dialog.UpdateLayout();
                    RequireStream(dialog.MouseSelectionBox.IsChecked == true && dialog.MouseSelectionHelp.Text.Length > 80 &&
                        dialog.SaveButton.ActualHeight >= 30,"Mouse settings lost privacy help/state/actions");
                    RenderWindowTo(dialog,Path.ChangeExtension(output,lang == UiLanguage.English ? ".mouse-settings.en.dark.png" : ".mouse-settings.zh.light.png"));
                }
                dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            RequireStream(_settings.MouseSelectionEnabled && _storage.Load().MouseSelectionEnabled && _mouseSession?.Input.Running == true && api.AddedMouse == 1,
                "Saved mouse setting did not start the injected hook boundary");
            await RunSettingsDialogAsync(dialog => { dialog.MouseSelectionBox.IsChecked=false; CancelStreamingSettingsDialog(dialog); });
            RequireStream(_settings.MouseSelectionEnabled && _storage.Load().MouseSelectionEnabled,"Settings Cancel changed the mouse preference");
            var path=Path.Combine(_storage.DirectoryPath,"settings.json"); var bytes=File.ReadAllBytes(path);
            using (var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                SetMouseSelectionEnabled(false);
                RequireStream(_settings.MouseSelectionEnabled && _trayMenu!.MouseSelection.IsChecked && File.ReadAllBytes(path).SequenceEqual(bytes) &&
                    _statusKey == "StatusSaveFailed","Failed tray save lost active mouse preference");
            }
            _trayMenu!.MouseSelection.IsChecked=false;
            _trayMenu.MouseSelection.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            RequireStream(!_settings.MouseSelectionEnabled && !_storage.Load().MouseSelectionEnabled && _mouseSession is null &&
                api.RemovedMouse == api.AddedMouse && api.RemovedForeground == api.AddedForeground,"Tray pause leaked hooks or did not persist");
            var fixturePath=Path.Combine(Path.GetDirectoryName(output)!,"mouse-config"); Directory.CreateDirectory(fixturePath);
            var badJson=System.Text.Json.JsonSerializer.Serialize(new { MouseSelectionEnabled=true,CaptureExcludedApps="*.exe" });
            File.WriteAllText(Path.Combine(fixturePath,"settings.json"),badJson);
            RequireStream(!new AppStorage(fixturePath).Load().MouseSelectionEnabled,"Invalid app rules did not pause persisted mouse capture");
            _mouseApiOverride=null;
            checks.Add("Mouse setting defaults off, saves/reopens/cancels with bilingual privacy help, and tray pause/save-failure rollback owns hook lifetime without real hooks or HTTP");

            long moveAllocated=0; double moveMs=0;
            using (var f=new MouseScenario(Dispatcher))
            {
                var hooksApi=new MouseHookFixture(); var hooks=new MouseInputHooks(Dispatcher,f.Controller,hooksApi);
                RequireStream(hooks.Start(),"Injected hook installation failed");
                hooksApi.Mouse!(0,new IntPtr(0x200),IntPtr.Zero); // Warm the exact callback branch before allocation measurement.
                var moveWatch=System.Diagnostics.Stopwatch.StartNew(); var allocatedBefore=GC.GetAllocatedBytesForCurrentThread();
                for (var i=0; i<10_000; i++) RequireStream(hooksApi.Mouse!(0,new IntPtr(0x200),IntPtr.Zero) == new IntPtr(77),"Mouse move was consumed");
                moveAllocated=GC.GetAllocatedBytesForCurrentThread()-allocatedBefore; moveMs=moveWatch.Elapsed.TotalMilliseconds;
                RequireStream(moveAllocated < 64*1024,"Ignored moves allocated per-event work");
                RequireStream(hooks.Queued == 0 && hooksApi.TargetReads == 0 && f.Reads == 0,"Moves queued work or inspected target");
                for (var i=0; i<40; i++) hooksApi.Emit(0x20a);
                RequireStream(hooks.Queued <= 8,"Hook mailbox grew without a bound");
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                RequireStream(f.Reads == 0 && hooks.TryStop() && hooks.TryStop() && !hooks.CleanupPending && hooksApi.RemovedMouse == 1 &&
                    hooksApi.RemovedForeground == 1,"Hook drain/cleanup read text or double-released handles");
                var failedApi=new MouseHookFixture { FailForeground=true,FailRemoval=true };
                var failed=new MouseInputHooks(Dispatcher,f.Controller,failedApi);
                RequireStream(!failed.Start() && failed.CleanupPending && !failed.Running && !failed.Start() && failedApi.AddedMouse == 1,
                    "Partial registration failure hid cleanup or repeated registration");
                failedApi.FailRemoval=false;
                RequireStream(failed.TryStop() && !failed.CleanupPending,"Explicit release retry did not clean partial hooks");
                var injectedApi=new MouseHookFixture(); var injected=new MouseInputHooks(Dispatcher,f.Controller,injectedApi);
                RequireStream(injected.Start(),"Injected test hook did not start");
                injectedApi.Emit(0x201); injectedApi.Emit(0x202,flags:1);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                RequireStream(f.Reads == 0 && !f.Controller.HasOffer && injected.TryStop(),"Injected input reached UIA or left an offer");
            }
            checks.Add($"Hook adapter ignores 10000 moves without target inspection ({moveAllocated} managed bytes; {moveMs:F3}ms with mock next-hook), bounds mailbox, chains input, rejects injection and reports/retries partial cleanup without duplicate registration");

            foreach (var sample in new string?[] {null,"   ",new string('x',3001)})
            {
                using var f=new MouseScenario(Dispatcher) { Read=_ => Task.FromResult(sample) };
                f.Drag(); await f.Controller.Pending;
                RequireStream(f.Reads == 1 && !f.Controller.HasOffer && !f.Icon.IsVisible && !f.Controller.TimerRunning,"Empty/oversize probe became an offer");
            }
            using (var f=new MouseScenario(Dispatcher) { Allowed=false })
            {
                f.Drag(); await f.Controller.Pending;
                RequireStream(f.Reads == 0 && !f.Controller.TimerRunning,"Excluded target was read");
                f.Allowed=true; f.Ready=false; f.Drag(); await f.Controller.Pending;
                RequireStream(f.Reads == 0,"Suspended/main-visible state read a selection");
            }
            RequireStream(!MouseAppAllowed(0) && !MouseAppAllowed(_handle.ToInt64()),"Unknown/self window allowed automatic reading");
            checks.Add("Mouse probes reject excluded/unknown/self or suspended contexts before reading; empty/unsupported/over-3000 results show no icon and leave no timer");

            foreach (var reason in new[] {"expire","escape","foreground","policy","changed","click"})
            {
                using var f=new MouseScenario(Dispatcher);
                f.Drag(); await f.Controller.Pending;
                RequireStream(f.Controller.HasOffer && f.Icon.IsVisible && f.Controller.TimerRunning,"Local probe did not show icon");
                switch (reason)
                {
                    case "expire": f.Now+=5001; f.Controller.Pulse(); break;
                    case "escape": f.Escape=true; f.Controller.Pulse(); break;
                    case "foreground": f.Foreground=43; f.Controller.Pulse(); break;
                    case "policy": f.Allowed=false; await f.Controller.ActivateAsync(); break;
                    case "changed": f.Read=_ => Task.FromResult<string?>("different selection"); await f.Controller.ActivateAsync(); break;
                    case "click": f.Send(MouseSelectionInputKind.LeftDown,new(10,10)); break;
                }
                RequireStream(!f.Controller.HasOffer && !f.Icon.IsVisible && f.Accepted.Count == 0 && !f.Controller.TimerRunning,
                    reason+" kept stale selection or accepted it");
            }
            checks.Add("Five-second expiry, Escape, foreground/policy change, altered selection and outside click discard the offer; click revalidation never imports stale text");

            using (var f=new MouseScenario(Dispatcher))
            {
                var late=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Read=_ => late.Task;
                f.Drag(); await WaitForStreamingUiAsync(() => f.Reads == 1);
                f.Controller.MarkInput(); // Input has arrived but Dispatcher delivery has not happened yet.
                late.SetResult("old private fixture"); await f.Controller.Pending;
                RequireStream(!f.Controller.HasOffer && !f.Controller.TimerRunning,"Undelivered input allowed late UIA result");
                var closing=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Read=_ => closing.Task; f.Drag(); await WaitForStreamingUiAsync(() => f.Reads == 2);
                f.Controller.Dispose(); closing.SetResult("closed fixture"); await f.Controller.Pending;
                RequireStream(!f.Icon.IsVisible && !f.Controller.HasOffer && f.Faults == 0,"Disposed session resurrected an offer or reported a stale fault");
            }
            checks.Add("Ingress barrier rejects UIA arriving before queued invalidation is drained; dispose cancels pending work and late reads cannot resurrect the icon");

            SelectProvider(ProviderKind.GoogleWeb);
            foreach (var review in new[] {false,true})
            {
                using var f=new MouseScenario(Dispatcher);
                _settings.AutoTranslateSelection=true; ReviewSourceBox.IsChecked=review;
                f.Accept=(text,gesture) => AcceptMouseSelection(text,gesture);
                f.Drag(); await f.Controller.Pending;
                var foreground=NativeMethods.GetForegroundWindow();
                var handle=new WindowInteropHelper(f.Icon).Handle;
                RequireStream((NativeMethods.GetWindowLong(handle,-20) & 0x08000000) != 0 && !f.Icon.ShowActivated,
                    "Icon lacks no-activate native styles");
                RequireStream(NativeMethods.GetForegroundWindow() == foreground,"Reading icon changed focus");
                var r=new NativeMethods.Rect(); NativeMethods.GetWindowRect(handle,out r);
                var iconPoint=new PixelPoint(r.Left+4,r.Top+4);
                f.Send(MouseSelectionInputKind.LeftDown,iconPoint,handle.ToInt64());
                f.Send(MouseSelectionInputKind.LeftUp,iconPoint,handle.ToInt64());
                RequireStream(f.Controller.HasOffer,"Clicking icon dismissed its offer before activation");
                var peer=new ButtonAutomationPeer(f.Icon.OpenButton);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await WaitForStreamingUiAsync(() => f.Accepted.Count == 1);
                RequireStream(f.Reads == 2 && SourceBox.Text == f.Text && _statusKey == "StatusMouseSelectionManual" &&
                    requests() == initialRequests && mouseHandler.RequestCount == 0 && !f.Icon.IsVisible && !f.Controller.TimerRunning,"Icon imported wrong text or auto-translated");
            }
            checks.Add("Real icon button preserves no-activate style, rechecks selection before import, preserves exact source and never auto-sends even with hotkey auto-translate or source review enabled");

            var clickApi=new MouseHookFixture();
            using (var f=new MouseScenario(Dispatcher,clickApi))
            {
                var hooks=f.Session.Input;
                RequireStream(hooks.Start(),"Click ordering fixture could not start");
                try
                {
                    f.Drag(); await f.Controller.Pending;
                    NativeMethods.GetWindowRect(new WindowInteropHelper(f.Icon).Handle,out var rect);
                    var point=new PixelPoint(rect.Left+4,rect.Top+4);
                    clickApi.Emit(0x201,point:point); clickApi.Emit(0x202,point:point);
                    RequireStream(hooks.Queued == 2,"Click ordering fixture accidentally drained early");
                    f.Icon.OpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await f.Controller.Pending;
                    RequireStream(f.Accepted.Count == 1 && f.Reads == 2,"Legitimate click was rejected before queued hook notifications were reconciled");
                    f.Drag(); await f.Controller.Pending;
                    clickApi.Emit(0x201,point:new PixelPoint(-20000,-20000));
                    f.Icon.OpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await f.Controller.Pending;
                    RequireStream(f.Accepted.Count == 1 && f.Reads == 3,"Flushing outside click admitted a stale offer");
                }
                finally { RequireStream(hooks.TryStop(),"Click fixture did not release hooks"); }
            }
            checks.Add("Button activation flushes already-arrived hook events: icon down/up survives Dispatcher ordering while queued outside click revokes the offer before any re-read");

            using (var f=new MouseScenario(Dispatcher))
            {
                f.Drag(); await f.Controller.Pending;
                foreach (var lang in new[] {UiLanguage.SimplifiedChinese,UiLanguage.English})
                {
                    UiLocalization.SetPreference(lang); UiAppearance.Apply(lang == UiLanguage.English ? UiTheme.Dark : UiTheme.Light);
                    f.Icon.UpdateLayout();
                    RequireStream(f.Icon.OpenButton.Content?.ToString() == UiLocalization.Get("MouseSelectionOpen") && f.Reads == 1,
                        "Icon localization reread text or lost accessible action");
                    foreach (var scale in new[] {1d,1.5,2d}) RenderWindowTo(f.Icon,Path.ChangeExtension(output,$".mouse-icon.{lang}.{scale*100:0}.png"),scale);
                }
                var info=new NativeMethods.MonitorInfo { Size=Marshal.SizeOf<NativeMethods.MonitorInfo>() };
                RequireStream(NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromPoint(new() {X=300,Y=300},2),ref info),"No test work area");
                foreach (var point in new[] {new PixelPoint(info.Work.Left,info.Work.Top),new PixelPoint(info.Work.Right-1,info.Work.Bottom-1)})
                {
                    var beforeForeground=NativeMethods.GetForegroundWindow(); f.Icon.ShowNear(point);
                    NativeMethods.GetWindowRect(new WindowInteropHelper(f.Icon).Handle,out var bounds);
                    RequireStream(bounds.Left >= info.Work.Left && bounds.Top >= info.Work.Top && bounds.Right <= info.Work.Right && bounds.Bottom <= info.Work.Bottom &&
                        NativeMethods.GetForegroundWindow() == beforeForeground,"Icon placement escaped work area or stole focus");
                }
                _mouseSession=f.Session;
                await RunSettingsDialogAsync(CancelStreamingSettingsDialog);
                RequireStream(!f.Controller.HasOffer && !f.Icon.IsVisible,"Opening Settings failed to revoke the mouse offer");
                _mouseSession=null;
            }
            checks.Add("Icon zh/light and en/dark renders at 100/150/200%, stays in current monitor work area without activation, and Settings revokes offers without HTTP");

            using (var entered=new ManualResetEventSlim())
            using (var release=new ManualResetEventSlim())
            using (var finished=new ManualResetEventSlim())
            {
                var reader=new SelectionCapture(); var workAfterTimeout=false;
                var slow=reader.RunAutomationAsync(token =>
                {
                    try
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Owned UIA timeout fixture stalled");
                        token.ThrowIfCancellationRequested(); workAfterTimeout=true;
                        return new("late fixture","UI Automation",null);
                    }
                    finally { finished.Set(); }
                },CancellationToken.None);
                try
                {
                    RequireStream(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))),"UIA fixture worker did not start");
                    RequireStream((await slow).Text is null,"Timed-out UIA returned selected text");
                    for (var i=0; i<20; i++)
                        RequireStream((await reader.RunAutomationAsync(_ => throw new Exception("Busy UIA started another worker"),CancellationToken.None)).Text is null,
                            "Busy UIA admitted another read");
                }
                finally { release.Set(); }
                RequireStream(await Task.Run(() => finished.Wait(TimeSpan.FromSeconds(2))) && !workAfterTimeout,
                    "UIA timeout did not cancel follow-up work when provider returned");
            }
            checks.Add("UIA local deadline returns no text, admits no extra workers while a call is blocked, and cancels follow-up reads after that call returns");

            await RunMouseAutomationFixtureAsync(checks);
            if (Environment.GetCommandLineArgs().Contains("--native-checks"))
            {
                using var f=new MouseScenario(Dispatcher) { Ready=false };
                var real=new MouseInputHooks(Dispatcher,f.Controller);
                try { RequireStream(real.Start(),"Real mouse/foreground hooks could not be installed"); await Task.Delay(80); }
                finally { RequireStream(real.TryStop() && !real.CleanupPending,"Real hooks did not release"); }
                RequireStream(f.Reads == 0,"Real hook lifecycle fixture read external text");
                checks.Add("Native opt-in check installs/releases real mouse and foreground hooks while selection reading is disabled; no external text, clipboard or synthesized input");
            }
            RequireStream(requests() == initialRequests && mouseHandler.RequestCount == 0,"Mouse feature issued HTTP without an explicit translation action");
            var log=File.ReadAllText(Path.Combine(_storage.DirectoryPath,"diagnostics.jsonl"));
            RequireStream(!log.Contains("selected fixture text",StringComparison.Ordinal) && !log.Contains("fixed-test-password",StringComparison.Ordinal),
                "Mouse source/password fixture entered diagnostics");
        }
        finally
        {
            StopMouseSelection(); _mouseApiOverride=null; _service.Dispose(); _service=originalService; RestoreSmokeSettings(original);
            ReviewSourceBox.IsChecked=reviewRequested;
            UiLocalization.SetPreference(language); UiAppearance.Apply(theme); Localize(); _storage.Save(_settings); ShowForUser(false); SourceBox.Clear();
        }
    }

    private async Task RunMouseAutomationFixtureAsync(List<string> checks)
    {
        var text=new TextBox { Text="controlled selected words",Margin=new Thickness(20),FontSize=20,AcceptsReturn=true };
        var password=new PasswordBox { Password="fixed-test-password",Margin=new Thickness(20) };
        var panel=new StackPanel(); panel.Children.Add(text); panel.Children.Add(password);
        AutomationProperties.SetAutomationId(text,"MouseProbeText"); AutomationProperties.SetAutomationId(password,"MouseProbePassword");
        var window=new Window { Width=500,Height=260,Content=panel,ShowInTaskbar=false,ShowActivated=false };
        try
        {
            window.Show(); window.UpdateLayout(); text.Select(11,14);
            var handle=new WindowInteropHelper(window).Handle;
            var result=await Task.Run(() =>
            {
                var root=AutomationElement.FromHandle(handle);
                var element=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"MouseProbeText"));
                var pattern=(TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
                var bounds=pattern.GetSelection().Single().GetBoundingRectangles();
                RequireStream(bounds.Length >= 1,"Fixture selection did not expose rectangles");
                var start=new PixelPoint((int)bounds[0].Left+2,(int)bounds[0].Top+2);
                var end=new PixelPoint((int)bounds[0].Right-2,(int)bounds[0].Bottom-2);
                var gesture=new MouseSelectionGesture(handle.ToInt64(),start,end,1000);
                var read=SelectionCapture.ReadMouseElement(element,(uint)Environment.ProcessId,gesture,CancellationToken.None,() => true);
                var moved=SelectionCapture.ReadMouseElement(element,(uint)Environment.ProcessId,gesture with {Start=new(-20000,-20000)},CancellationToken.None,() => true);
                var protectedElement=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"MouseProbePassword"));
                var blocked=SelectionCapture.ReadMouseElement(protectedElement,(uint)Environment.ProcessId,gesture,CancellationToken.None,() => true);
                return (read,moved,blocked,gesture);
            }).WaitAsync(TimeSpan.FromSeconds(3));
            RequireStream(result.read.Text == "selected words" && result.moved.Text is null && result.blocked.Text is null &&
                result.blocked.Code == CaptureErrorCode.ProtectedInput,"Real UIA fixture ignored selection bounds/password rejection");
            window.Activate(); text.Focus();
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (NativeMethods.GetForegroundWindow() == handle)
            {
                var capture=new SelectionCapture();
                var gesture=result.gesture with { CompletedAtMs=Environment.TickCount64 };
                RequireStream((await capture.ProbeMouseAsync(gesture,CancellationToken.None)).Text == "selected words",
                    "Real mouse point probe did not read the owned selection");
                // The pointer may be over the popup, not the text at activation. Focus stays with the
                // original app; revalidation must use that app's focused selection, never popup contents.
                using var iconSession=new MouseScenario(Dispatcher);
                iconSession.Icon.ShowNear(gesture.End);
                var iconHandle=new WindowInteropHelper(iconSession.Icon).Handle;
                NativeMethods.SetWindowPos(iconHandle,new IntPtr(-1),gesture.End.X-4,gesture.End.Y-4,0,0,0x0011);
                RequireStream((await capture.RevalidateMouseAsync(gesture,CancellationToken.None)).Text == "selected words",
                    "Focused selection revalidation failed while the icon occupied the gesture point");
                checks.Add("Real foreground point probe and focused-selection revalidation work on owned fixed text even while the no-activate icon overlaps the old gesture point");
            }
            else checks.Add("Owned UIA selection geometry checked; Windows refused test-window foreground, so full point/focused mouse revalidation remains unverified in this run");
            text.Select(0,0);
            var empty=await Task.Run(() =>
            {
                var root=AutomationElement.FromHandle(handle);
                var element=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"MouseProbeText"));
                return SelectionCapture.ReadMouseElement(element,(uint)Environment.ProcessId,new(handle.ToInt64(),new(0,0),new(10,0),1000),CancellationToken.None,() => true);
            }).WaitAsync(TimeSpan.FromSeconds(3));
            RequireStream(empty.Text is null,"Degenerate UIA caret was treated as selected text");
            checks.Add("Real UI Automation reads only fixed owned-window selection, rejects mismatched gesture geometry/password controls/empty caret; no clipboard or external application text");
        }
        finally { window.Close(); }
    }

    private sealed class MouseScenario : IDisposable
    {
        internal readonly MouseSelectionSession Session;
        internal MouseSelectionController Controller => Session.Controller;
        internal MouseSelectionIcon Icon => Session.Icon;
        internal bool Ready=true,Allowed=true,Escape;
        internal long Now=1000,Foreground=42;
        internal int Reads,Faults;
        internal string Text="  selected fixture text  ";
        internal Func<CancellationToken,Task<string?>>? Read;
        internal Action<string,MouseSelectionGesture>? Accept;
        internal List<string> Accepted { get; }=new();
        internal MouseScenario(Dispatcher dispatcher, IMouseHookApi? api = null)
        {
            Session=new MouseSelectionSession(dispatcher,icon => new()
            {
                Ready=() => Ready, Foreground=() => Foreground, Allowed=_ => Allowed, Clock=() => Now, Escape=() => Escape,
                Read=(_,token) => { Reads++; return Read?.Invoke(token) ?? Task.FromResult<string?>(Text); },
                Show=g => icon.ShowNear(g.End),Hide=() => { if (icon.IsVisible) icon.Hide(); },OverIcon=icon.Contains,
                Accept=(text,g) => { Accepted.Add(text); Accept?.Invoke(text,g); },Fault=() => Faults++
            },api);
        }
        internal void Send(MouseSelectionInputKind kind, PixelPoint point, long window=42)
        { Controller.Handle(new(kind,window,Foreground,point,Now),Controller.MarkInput()); }
        internal void Drag() { Send(MouseSelectionInputKind.LeftDown,new(200,200)); Send(MouseSelectionInputKind.LeftUp,new(300,200)); }
        public void Dispose() => Session.Dispose();
    }
    private sealed class MouseHookFixture : IMouseHookApi
    {
        internal NativeMethods.MouseHookProc? Mouse;
        internal int AddedMouse,AddedForeground,RemovedMouse,RemovedForeground,TargetReads;
        internal bool FailForeground,FailRemoval;
        public IntPtr AddMouse(NativeMethods.MouseHookProc callback) { AddedMouse++; Mouse=callback; return new(11); }
        public IntPtr AddForeground(NativeMethods.ForegroundHookProc callback) { AddedForeground++; return FailForeground ? IntPtr.Zero : new(12); }
        public bool RemoveMouse(IntPtr hook) { if (FailRemoval) return false; RemovedMouse++; return true; }
        public bool RemoveForeground(IntPtr hook) { if (FailRemoval) return false; RemovedForeground++; return true; }
        public IntPtr Next(int code,IntPtr message,IntPtr data) => new(77);
        public long WindowAt(PixelPoint point) { TargetReads++; return 42; }
        public long Foreground() => 42;
        public long Clock() => 1000;
        internal void Emit(int message,uint flags=0,PixelPoint? point=null)
        {
            var data=Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.LowLevelMouse>());
            try
            {
                var position=point ?? new PixelPoint(200,200);
                Marshal.StructureToPtr(new NativeMethods.LowLevelMouse { Point=new() {X=position.X,Y=position.Y},Flags=flags },data,false);
                RequireStream(Mouse!(0,new(message),data) == new IntPtr(77),"Hook swallowed input");
            }
            finally { Marshal.FreeHGlobal(data); }
        }
    }
}
