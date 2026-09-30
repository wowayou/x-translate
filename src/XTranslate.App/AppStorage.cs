using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using XTranslate.Core;

namespace XTranslate.App;

public sealed class UserSettings
{
    public TranslationSettings Translation { get; set; } = new();
    public UiLanguage UiLanguage { get; set; } = UiLanguage.Auto;
    public bool AutoTranslateSelection { get; set; }
    public bool AllowCopyCapture { get; set; } = true;
    public bool AutoHideSelectionPopup { get; set; } = true;
    public bool UseCtrlAltQ { get; set; }
    public string DeepLSecret { get; set; } = "";
    public string OpenAiSecret { get; set; } = "";
}

public sealed class AppStorage
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public string? Warning { get; private set; }
    public string DirectoryPath => _directory;

    public AppStorage(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "data");
    }

    public UserSettings Load()
    {
        try
        {
            var path = Path.Combine(_directory, "settings.json");
            if (!File.Exists(path)) return new UserSettings();
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new UserSettings();
            settings.Translation ??= new TranslationSettings();
            settings.Translation.DeepLApiKey = SecretProtection.Unprotect(settings.DeepLSecret);
            settings.Translation.OpenAiApiKey = SecretProtection.Unprotect(settings.OpenAiSecret);
            if (!Enum.IsDefined(settings.Translation.Provider) || !ProviderCatalog.IsAvailable(settings.Translation.Provider))
                settings.Translation.Provider = ProviderKind.GoogleWeb;
            if (!Enum.IsDefined(settings.UiLanguage)) settings.UiLanguage = UiLanguage.Auto;
            return settings;
        }
        catch
        {
            Warning = UiLocalization.Get("StorageWarning");
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        // Serialize a detached copy; never clear live credentials during a request.
        var copy = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
        copy.DeepLSecret = SecretProtection.Protect(copy.Translation.DeepLApiKey);
        copy.OpenAiSecret = SecretProtection.Protect(copy.Translation.OpenAiApiKey);
        copy.Translation.DeepLApiKey = "";
        copy.Translation.OpenAiApiKey = "";
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(copy, _json));
        File.Move(temporary, path, true);
    }

    public void Log(string requestId, string action, string provider, long durationMs, string outcome)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "diagnostics.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.Move(path, Path.Combine(_directory, "diagnostics.previous.jsonl"), true);
            // Deliberately excludes source text, translation, keys, URLs and remote bodies.
            var row = new { utc = DateTimeOffset.UtcNow, requestId, action, provider, durationMs, outcome };
            File.AppendAllText(path, JsonSerializer.Serialize(row) + Environment.NewLine);
        }
        catch { /* Logging failures must not prevent a translation. */ }
    }
}

internal static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plain), true));
    public static string Unprotect(string encoded) => string.IsNullOrEmpty(encoded) ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(encoded), false));

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect
                ? CryptProtectData(ref input, "XTranslate", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            Array.Clear(bytes);
        }
    }
}
