using System.Runtime.InteropServices;

namespace XTranslate.App;

internal static class NativeMethods
{
    internal const int HotkeyId = 0x5854;
    internal const int WmHotkey = 0x0312;
    internal static readonly uint ShowMessage = RegisterWindowMessage("XTranslate.Show.v01");
    /// <summary>Asks the running instance to exit through its normal tray-exit path (used by install.ps1).</summary>
    internal static readonly uint ExitMessage = RegisterWindowMessage("XTranslate.Exit.v01");
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] internal static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClipboardFormatName(uint format, System.Text.StringBuilder name, int max);
    [DllImport("kernel32.dll")] internal static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public UIntPtr Extra; }

    internal static bool SendCopy()
    {
        static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
        var inputs = new[] { Key(0x11), Key(0x43), Key(0x43, true), Key(0x11, true) };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return true;
        // A partial injection must not leave Ctrl or C logically held down.
        var releases = new[] { Key(0x43, true), Key(0x11, true) };
        SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
        return false;
    }

    internal static void PositionNearCursor(IntPtr window)
    {
        if (!GetCursorPos(out var cursor)) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(cursor, 2), ref info)) return;
        // Move first, then read the physical size again after per-monitor DPI changes.
        SetWindowPos(window, IntPtr.Zero, info.Work.Left + 12, info.Work.Top + 12, 0, 0, 0x0015);
        if (!GetWindowRect(window, out var rectangle)) return;
        var width = Math.Min(rectangle.Right - rectangle.Left, info.Work.Right - info.Work.Left);
        var height = Math.Min(rectangle.Bottom - rectangle.Top, info.Work.Bottom - info.Work.Top);
        var x = Math.Clamp(cursor.X + 18, info.Work.Left, info.Work.Right - width);
        var y = Math.Clamp(cursor.Y + 18, info.Work.Top, info.Work.Bottom - height);
        SetWindowPos(window, IntPtr.Zero, x, y, width, height, 0x0014);
    }
}
