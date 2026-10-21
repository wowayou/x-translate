using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using XTranslate.Core;

namespace XTranslate.App;

internal enum CaptureErrorCode
{
    None, ProtectedInput, Unavailable, Terminal, TargetExited, ReleaseKeys,
    ForegroundChanged, ClipboardBusy, ClipboardUnsupported, ClipboardBackupFailed,
    ClipboardLockFailed, ClipboardChanged, CopyFailed, OtherClipboardChanged,
    RestoreFailed, RestoreChanged, WindowChanged, NoText, NoResponse, Unknown
}

internal sealed record CaptureResult(string? Text, string Method, string? Error, CaptureErrorCode Code = CaptureErrorCode.None);

internal sealed class SelectionCapture
{
    private int _uiaBusy;

    public async Task<CaptureResult> CaptureAsync(IntPtr foreground, IntPtr owner, bool allowCopy, CancellationToken token)
    {
        var uia = await ReadAutomationAsync(foreground, token);
        if (uia.Code == CaptureErrorCode.ProtectedInput) return new(null, "UI Automation", null, CaptureErrorCode.ProtectedInput);
        if (!string.IsNullOrWhiteSpace(uia.Text)) return uia;
        token.ThrowIfCancellationRequested();
        if (!allowCopy) return new(null, "UI Automation", null, CaptureErrorCode.Unavailable);
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (new[] { "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "OpenConsole", "mintty", "wezterm-gui", "alacritty", "Code", "Code - Insiders", "devenv", "idea64", "pycharm64" }.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                return new(null, "兼容取词", null, CaptureErrorCode.Terminal);
        }
        catch { return new(null, "兼容取词", null, CaptureErrorCode.TargetExited); }
        return await ReadViaCopyAsync(foreground, owner, token);
    }

    private Task<CaptureResult> ReadAutomationAsync(IntPtr foreground, CancellationToken token) => RunAutomationAsync(workToken =>
    {
        var focused = AutomationElement.FocusedElement;
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (focused is null || focused.Current.ProcessId != processId) return new(null, "UI Automation", null);
        if (focused.Current.IsPassword) return new(null, "UI Automation", null, CaptureErrorCode.ProtectedInput);
        var element = focused;
        for (var depth = 0; depth < 5 && element is not null; depth++)
        {
            workToken.ThrowIfCancellationRequested();
            if (element.Current.ProcessId != processId) break;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            {
                var ranges = ((TextPattern)pattern).GetSelection();
                var text = string.Join(Environment.NewLine, ranges.Select(r => r.GetText(3001)));
                if (!string.IsNullOrWhiteSpace(text)) return new(text, "UI Automation", null);
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return new(null, "UI Automation", null);
    }, token);

    // Mouse probes never use Ctrl+C or the clipboard, even when compatibility hotkey capture is enabled.
    internal Task<CaptureResult> ProbeMouseAsync(MouseSelectionGesture gesture, CancellationToken token) => RunAutomationAsync(workToken =>
    {
        var foreground = new IntPtr(gesture.Window);
        if (NativeMethods.GetForegroundWindow() != foreground) return new(null, "UI Automation", null);
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0) return new(null, "UI Automation", null);
        var focused = AutomationElement.FocusedElement;
        if (focused is not null && focused.Current.ProcessId == processId && focused.Current.IsPassword)
            return new(null, "UI Automation", null, CaptureErrorCode.ProtectedInput);
        var element = AutomationElement.FromPoint(new System.Windows.Point(gesture.End.X, gesture.End.Y));
        return ReadMouseElement(element, processId, gesture, workToken, () => NativeMethods.GetForegroundWindow() == foreground);
    }, token);

    internal Task<CaptureResult> RevalidateMouseAsync(MouseSelectionGesture gesture, CancellationToken token) => RunAutomationAsync(workToken =>
    {
        var foreground = new IntPtr(gesture.Window);
        if (NativeMethods.GetForegroundWindow() != foreground) return new(null, "UI Automation", null);
        NativeMethods.GetWindowThreadProcessId(foreground,out var processId);
        if (processId == 0) return new(null, "UI Automation", null);
        var element = AutomationElement.FocusedElement;
        if (element is null || element.Current.ProcessId != processId) return new(null, "UI Automation", null);
        return ReadMouseElement(element,processId,gesture,workToken,() => NativeMethods.GetForegroundWindow() == foreground);
    },token);

    internal static CaptureResult ReadMouseElement(AutomationElement? element, uint processId, MouseSelectionGesture gesture,
        CancellationToken token, Func<bool> current)
    {
        for (var depth = 0; depth < 5 && element is not null; depth++)
        {
            token.ThrowIfCancellationRequested();
            if (!current() || element.Current.ProcessId != processId) break;
            if (element.Current.IsPassword) return new(null, "UI Automation", null, CaptureErrorCode.ProtectedInput);
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var raw))
            {
                var ranges = ((TextPattern)raw).GetSelection();
                if (ranges.Length is < 1 or > 4) break;
                var rectangles = new List<SelectionRectangle>();
                foreach (var range in ranges)
                {
                    token.ThrowIfCancellationRequested();
                    if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0)
                        return new(null, "UI Automation", null);
                    var bounds = range.GetBoundingRectangles();
                    if (bounds.Length == 0 || bounds.Length > MouseSelectionGeometry.MaxRectangles - rectangles.Count)
                        return new(null, "UI Automation", null);
                    foreach (var bound in bounds) rectangles.Add(new(bound.Left,bound.Top,bound.Width,bound.Height));
                }
                if (!MouseSelectionGeometry.CoversGesture(gesture, rectangles)) break;
                var text = new StringBuilder();
                foreach (var range in ranges)
                {
                    token.ThrowIfCancellationRequested();
                    if (!current()) return new(null, "UI Automation", null);
                    if (text.Length > 0) text.Append(Environment.NewLine);
                    if (text.Length > 3000) return new(null, "UI Automation", null);
                    text.Append(range.GetText(3001 - text.Length));
                    if (text.Length > 3000) return new(null, "UI Automation", null);
                }
                return string.IsNullOrWhiteSpace(text.ToString()) ? new(null, "UI Automation", null) : new(text.ToString(), "UI Automation", null);
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return new(null, "UI Automation", null);
    }

    internal async Task<CaptureResult> RunAutomationAsync(Func<CancellationToken, CaptureResult> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // A stuck cross-process UIA call must not cause unbounded thread creation. The local deadline
        // cancels further work as soon as a blocked provider returns; it cannot forcibly abort that call.
        if (Interlocked.CompareExchange(ref _uiaBusy, 1, 0) != 0) return new(null, "UI Automation", null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(650));
        var workToken = deadline.Token;
        var completion = new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { workToken.ThrowIfCancellationRequested(); completion.TrySetResult(read(workToken)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(workToken); }
            catch { completion.TrySetResult(new(null, "UI Automation", null)); }
            finally { Interlocked.Exchange(ref _uiaBusy, 0); }
        }) { IsBackground = true, Name = "XTranslate.Selection.UIA" };
        try { worker.SetApartmentState(ApartmentState.MTA); worker.Start(); }
        catch { Interlocked.Exchange(ref _uiaBusy, 0); throw; }
        try { return await completion.Task.WaitAsync(workToken); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null, "UI Automation", null); }
    }

    private static async Task<CaptureResult> ReadViaCopyAsync(IntPtr foreground, IntPtr owner, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        // Wait for the triggering shortcut to be released before synthesizing Ctrl+C.
        while (new[] { 0x11, 0x12, 0x10, 0x51, 0x5B, 0x5C }.Any(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) != 0))
        {
            if (timer.ElapsedMilliseconds > 900) return new(null, "兼容取词", null, CaptureErrorCode.ReleaseKeys);
            await Task.Delay(20, token);
        }
        token.ThrowIfCancellationRequested();
        if (NativeMethods.GetForegroundWindow() != foreground) return new(null, "兼容取词", null, CaptureErrorCode.ForegroundChanged);
        var snapshot = ClipboardSnapshot.TryRead(owner, out var code);
        if (snapshot is null) return new(null, "兼容取词", null, code);
        // A change after the snapshot means we must not overwrite the new clipboard.
        if (NativeMethods.GetClipboardSequenceNumber() != snapshot.Sequence) return new(null, "兼容取词", null, CaptureErrorCode.ClipboardChanged);
        if (!NativeMethods.SendCopy()) return new(null, "兼容取词", null, CaptureErrorCode.CopyFailed);

        // After injection, complete cleanup even if the user cancels the capture.
        // Cancellation does not bypass restoration of a clipboard change we observed.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(20);
            if (NativeMethods.GetClipboardSequenceNumber() == snapshot.Sequence) continue;
            var clipboardOwner = NativeMethods.GetClipboardOwner();
            NativeMethods.GetWindowThreadProcessId(foreground, out var sourceProcess);
            NativeMethods.GetWindowThreadProcessId(clipboardOwner, out var clipboardProcess);
            if (sourceProcess != clipboardProcess)
                return new(null, "兼容取词", null, CaptureErrorCode.OtherClipboardChanged);
            if (!NativeMethods.OpenClipboard(owner)) continue;
            string? text = null;
            uint copiedSequence;
            try
            {
                // Recheck while the clipboard is locked: an unrelated writer may have
                // won the race between the owner check and OpenClipboard.
                NativeMethods.GetWindowThreadProcessId(NativeMethods.GetClipboardOwner(), out var lockedOwnerProcess);
                if (sourceProcess != lockedOwnerProcess)
                    return new(null, "兼容取词", null, CaptureErrorCode.OtherClipboardChanged);
                var memory = NativeMethods.GetClipboardData(13);
                if (memory != IntPtr.Zero)
                {
                    var pointer = NativeMethods.GlobalLock(memory);
                    if (pointer != IntPtr.Zero)
                    {
                        try
                        {
                            var chars = Math.Min((long)NativeMethods.GlobalSize(memory) / 2, 3002);
                            text = Marshal.PtrToStringUni(pointer, (int)chars)?.Split('\0')[0];
                        }
                        finally { NativeMethods.GlobalUnlock(memory); }
                    }
                }
                // Delayed rendering may advance the sequence during GetClipboardData.
                // Capture it after materialization, while unrelated writers are excluded.
                copiedSequence = NativeMethods.GetClipboardSequenceNumber();
            }
            finally { NativeMethods.CloseClipboard(); }
            var restored = await snapshot.RestoreAsync(owner, copiedSequence);
            token.ThrowIfCancellationRequested();
            if (restored != ClipboardRestoreResult.Restored)
                return new(null, "兼容取词", null, restored == ClipboardRestoreResult.Failed
                    ? CaptureErrorCode.RestoreFailed : CaptureErrorCode.RestoreChanged);
            if (NativeMethods.GetForegroundWindow() != foreground)
                return new(null, "兼容取词", null, CaptureErrorCode.WindowChanged);
            return string.IsNullOrWhiteSpace(text)
                ? new(null, "兼容取词", null, CaptureErrorCode.NoText)
                : new(text, "兼容取词（已恢复剪贴板）", null);
        }
        return new(null, "兼容取词", null, CaptureErrorCode.NoResponse);
    }
}

internal enum ClipboardRestoreResult { Restored, Changed, Busy, Failed }

internal sealed class ClipboardSnapshot
{
    private readonly List<(uint Format, byte[] Bytes)> _formats = new();
    public uint Sequence { get; private set; }

    public static ClipboardSnapshot? TryRead(IntPtr owner, out CaptureErrorCode code)
    {
        code = CaptureErrorCode.None;
        if (!NativeMethods.OpenClipboard(owner)) { code = CaptureErrorCode.ClipboardBusy; return null; }
        try
        {
            var snapshot = new ClipboardSnapshot();
            long total = 0;
            for (uint format = NativeMethods.EnumClipboardFormats(0); format != 0; format = NativeMethods.EnumClipboardFormats(format))
            {
                var supported = format is 1 or 7 or 13 or 16;
                if (format >= 0xc000)
                {
                    var name = new StringBuilder(256);
                    NativeMethods.GetClipboardFormatName(format, name, name.Capacity);
                    supported = name.ToString() is "HTML Format" or "Rich Text Format" or "CSV";
                }
                if (!supported) { code = CaptureErrorCode.ClipboardUnsupported; return null; }
                var memory = NativeMethods.GetClipboardData(format);
                var size = (long)NativeMethods.GlobalSize(memory);
                total += size;
                if (memory == IntPtr.Zero || size <= 0 || total > 8_000_000) { code = CaptureErrorCode.ClipboardBackupFailed; return null; }
                var pointer = NativeMethods.GlobalLock(memory);
                if (pointer == IntPtr.Zero) { code = CaptureErrorCode.ClipboardLockFailed; return null; }
                try
                {
                    var bytes = new byte[(int)size];
                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    snapshot._formats.Add((format, bytes));
                }
                finally { NativeMethods.GlobalUnlock(memory); }
            }
            snapshot.Sequence = NativeMethods.GetClipboardSequenceNumber();
            return snapshot;
        }
        catch { code = CaptureErrorCode.ClipboardBackupFailed; return null; }
        finally { NativeMethods.CloseClipboard(); }
    }

    public async Task<ClipboardRestoreResult> RestoreAsync(IntPtr owner, uint expectedSequence)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) return ClipboardRestoreResult.Changed;
            if (!NativeMethods.OpenClipboard(owner)) { await Task.Delay(15); continue; }
            var handles = new List<(uint Format, IntPtr Handle)>();
            try
            {
                if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) return ClipboardRestoreResult.Changed;
                // Allocate everything before clearing so allocation failure leaves copied text intact.
                foreach (var format in _formats)
                {
                    var memory = NativeMethods.GlobalAlloc(0x0042, (UIntPtr)format.Bytes.Length);
                    if (memory == IntPtr.Zero) return ClipboardRestoreResult.Failed;
                    handles.Add((format.Format, memory));
                    var pointer = NativeMethods.GlobalLock(memory);
                    if (pointer == IntPtr.Zero) return ClipboardRestoreResult.Failed;
                    try { Marshal.Copy(format.Bytes, 0, pointer, format.Bytes.Length); }
                    finally { NativeMethods.GlobalUnlock(memory); }
                }
                if (!NativeMethods.EmptyClipboard()) return ClipboardRestoreResult.Failed;
                var failed = false;
                for (var i = 0; i < handles.Count; i++)
                {
                    var item = handles[i];
                    var transferred = false;
                    for (var retry = 0; retry < 3 && !transferred; retry++)
                        transferred = NativeMethods.SetClipboardData(item.Format, item.Handle) != IntPtr.Zero;
                    if (transferred) handles[i] = (item.Format, IntPtr.Zero); // The OS now owns this allocation.
                    else failed = true; // Still restore the other formats if one format fails.
                }
                return failed ? ClipboardRestoreResult.Failed : ClipboardRestoreResult.Restored;
            }
            finally
            {
                foreach (var item in handles) if (item.Handle != IntPtr.Zero) NativeMethods.GlobalFree(item.Handle);
                NativeMethods.CloseClipboard();
            }
        }
        return ClipboardRestoreResult.Busy;
    }
}
