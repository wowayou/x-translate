using System.Text;

namespace XTranslate.Core;

public enum CaptureAppDecision { Allowed, Excluded, Unidentified }

/// <summary>
/// Local convenience filter, not a security sandbox. Rules are executable basenames, never paths/globs/regex.
/// The observed identity is Process.ProcessName (without the final .exe), not window titles or selected text.
/// </summary>
public sealed class CaptureAppPolicy
{
    public const int MaxApps = 128;
    public const int MaxNameChars = 255;
    public const int MaxSettingsChars = MaxApps * (MaxNameChars + 2);
    private readonly HashSet<string> _stems;
    public IReadOnlyList<string> Apps { get; }

    private CaptureAppPolicy(string[] stems)
    {
        _stems = new(stems, StringComparer.OrdinalIgnoreCase);
        Apps = Array.AsReadOnly(stems.Select(stem => stem + ".exe").ToArray());
    }

    public CaptureAppDecision Decide(string? processName) => _stems.Count == 0 ? CaptureAppDecision.Allowed :
        string.IsNullOrWhiteSpace(processName) ? CaptureAppDecision.Unidentified :
        _stems.Contains(processName) ? CaptureAppDecision.Excluded : CaptureAppDecision.Allowed;

    public string ToSettingsText() => string.Join("\n", Apps);

    /// <summary>Reject the entire invalid list; never drop an unsafe/misspelled rule and silently allow capture.</summary>
    public static bool TryParse(string? text, out CaptureAppPolicy policy)
    {
        policy = null!;
        if (text is null || text.Length > MaxSettingsChars) return false;
        var stems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var name = line.Trim();
            if (name.Length == 0) continue;
            if (++count > MaxApps || name.Length > MaxNameChars || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) return false;
            try { _ = new UTF8Encoding(false, true).GetByteCount(name); }
            catch (EncoderFallbackException) { return false; }
            var stem = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (stem.Length is 0 or > MaxNameChars - 4 || stem != stem.Trim() || stem is "." or "..") return false;
            if (seen.Add(stem)) stems.Add(stem.ToLowerInvariant());
        }
        policy = new(stems.ToArray());
        return true;
    }
}
