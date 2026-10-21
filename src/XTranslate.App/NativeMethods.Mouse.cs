using System.Runtime.InteropServices;

namespace XTranslate.App;

internal static partial class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    internal delegate IntPtr MouseHookProc(int code, IntPtr message, IntPtr data);
    internal delegate void ForegroundHookProc(IntPtr hook, uint evt, IntPtr window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] internal struct LowLevelMouse { public Point Point; public uint MouseData; public uint Flags; public uint Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int hook, MouseHookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, ForegroundHookProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(IntPtr window, int index, int value);
}
