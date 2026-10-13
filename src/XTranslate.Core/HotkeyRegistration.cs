namespace XTranslate.Core;

public sealed record HotkeyGesture(uint Modifiers, uint Key)
{
    public string Display => (Modifiers.HasFlag(2) ? "Ctrl+" : "") +
        (Modifiers.HasFlag(1) ? "Alt+" : "") + (Modifiers.HasFlag(4) ? "Shift+" : "") +
        (Key is >= 0x70 and <= 0x7A ? $"F{Key - 0x6F}" : ((char)Key).ToString());

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = new HotkeyGesture(2, 0x51);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 4) return false;
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToUpperInvariant() switch { "CTRL" => 2u, "ALT" => 1u, "SHIFT" => 4u, _ => 0u };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }
        if ((modifiers & 3) == 0) return false;
        var keyName = parts[^1].ToUpperInvariant();
        uint key;
        if (keyName.Length == 1 && (keyName[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) key = keyName[0];
        else if (keyName.StartsWith('F') && int.TryParse(keyName[1..], out var number) && number is >= 1 and <= 11)
            key = (uint)(0x6F + number);
        else return false; // F12 is reserved by Windows for debugging.
        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }
}

internal static class ModifierBits
{
    public static bool HasFlag(this uint value, uint flag) => (value & flag) != 0;
}

/// <summary>UI-thread owned. A failed replacement never unregisters the working gesture.</summary>
public sealed class HotkeyRegistration(
    int firstId, int secondId, Func<int, uint, uint, bool> register, Action<int> unregister) : IDisposable
{
    public HotkeyGesture? Current { get; private set; }
    public int? Id { get; private set; }
    private bool _disposed;

    public bool TryApply(HotkeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HotkeyGesture.TryParse(gesture.Display, out var checkedGesture) || checkedGesture != gesture)
            return false;
        if (Current == gesture) return true;
        var candidate = Id == firstId ? secondId : firstId;
        if (!register(candidate, gesture.Modifiers | 0x4000, gesture.Key)) return false;
        if (Id is int previous) unregister(previous);
        Id = candidate;
        Current = gesture;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Id is int id) unregister(id);
        Id = null;
        Current = null;
    }
}
