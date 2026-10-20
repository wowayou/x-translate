using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XTranslate.Core;
using XTranslate.App.Lexicons;

namespace XTranslate.App;

public sealed class UserSettings
{
    public TranslationSettings Translation { get; set; } = new();
    public UiLanguage UiLanguage { get; set; } = UiLanguage.Auto;
    public UiTheme Theme { get; set; } = UiTheme.System;
    public bool CaptureHotkeyEnabled { get; set; } = true;
    public bool AutoTranslateSelection { get; set; }
    public bool AllowCopyCapture { get; set; } = true;
    public bool AutoHideSelectionPopup { get; set; } = true;
    public bool UseCtrlAltQ { get; set; }
    public string? CaptureHotkey { get; set; }
    public string DeepLSecret { get; set; } = "";
    /// <summary>Legacy single-AI credential; migrated into <see cref="AiSecrets"/> on load.</summary>
    public string OpenAiSecret { get; set; } = "";
    public List<AiServiceProfile> AiServices { get; set; } = new();
    public string? ActiveAiServiceId { get; set; }
    /// <summary>DPAPI blobs keyed by profile id. Never plaintext.</summary>
    public Dictionary<string, string> AiSecrets { get; set; } = new();
    [JsonIgnore] public bool DeepLSecretUnavailable { get; set; }
    /// <summary>Off by default: a failure is reported and nothing else receives the text.</summary>
    public bool AutoFallback { get; set; }
    /// <summary>Backup order. Keys: "google", "deepl", "ai:{id}". The selected service is always tried first.</summary>
    public List<FallbackEntry> FallbackOrder { get; set; } = new();
    /// <summary>Off by default: contacting GitHub reveals the IP address and version.</summary>
    public bool AutoCheckUpdates { get; set; }
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    [JsonIgnore] public string EffectiveCaptureHotkey => CaptureHotkey ?? (UseCtrlAltQ ? "Ctrl+Alt+Q" : "Ctrl+Q");

    /// <summary>The selected AI service, or the first one if the id is stale.</summary>
    [JsonIgnore] public AiServiceProfile? ActiveAiService =>
        AiServices.FirstOrDefault(service => service.Id == ActiveAiServiceId) ?? AiServices.FirstOrDefault();

    /// <summary>Copies the active profile into the request settings and fixes a stale selection.</summary>
    public void ApplyActiveAiService()
    {
        var active = ActiveAiService;
        if (active is null) return;
        ActiveAiServiceId = active.Id;
        active.ApplyTo(Translation);
    }

    /// <summary>Settings built in code (not via Load) still get one default, credential-free service.</summary>
    public void EnsureAiService()
    {
        if (AiServices.Count > 0) return;
        var service = AiServiceTemplates.Create(AiServiceTemplates.All[0], AiServiceTemplates.All[0].Name);
        AiServices.Add(service);
        ActiveAiServiceId = service.Id;
    }

    public UserSettings Copy()
    {
        var copy = (UserSettings)MemberwiseClone();
        copy.Translation = Translation.Copy();
        copy.AiServices = AiServices.Select(service => service.Copy()).ToList();
        copy.AiSecrets = new Dictionary<string, string>(AiSecrets);
        copy.FallbackOrder = FallbackOrder.Select(entry => entry.Copy()).ToList();
        return copy;
    }

    public const string GoogleKey = "google";
    public const string DeepLKey = "deepl";
    public static string AiKey(AiServiceProfile service) => "ai:" + service.Id;

    /// <summary>Keeps the user's order, drops deleted services and duplicates, appends new services (enabled).</summary>
    public void NormalizeFallbackOrder()
    {
        var known = new[] { GoogleKey, DeepLKey }.Concat(AiServices.Select(AiKey)).ToList();
        FallbackOrder = (FallbackOrder ?? new()).Where(entry => entry is not null && known.Contains(entry.Key))
            .DistinctBy(entry => entry.Key).ToList();
        foreach (var key in known.Where(key => FallbackOrder.All(entry => entry.Key != key)))
            FallbackOrder.Add(new FallbackEntry { Key = key, Enabled = true });
    }
}

public sealed class AppStorage
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public string? Warning { get; private set; }
    public string DirectoryPath => _directory;

    /// <summary>On-demand file storage; construction does not open dictionaries or initialize SQLite.</summary>
    public LexiconStore CreateLexiconStore() => new(_directory);

    public AppStorage(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "data");
    }

    public UserSettings Load()
    {
        Warning = null;
        try
        {
            var path = Path.Combine(_directory, "settings.json");
            if (!File.Exists(path)) return Normalize(new UserSettings());
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new UserSettings();
            settings.Translation ??= new TranslationSettings();
            settings.Translation.DeepLApiKey = ReadSecret(settings.DeepLSecret, out var deepLUnavailable);
            settings.DeepLSecretUnavailable = deepLUnavailable;
            if (settings.CaptureHotkey is not null && !HotkeyGesture.TryParse(settings.CaptureHotkey, out _))
            {
                Warning = UiLocalization.Get("StorageWarning");
                settings.CaptureHotkey = null;
            }
            if (!Enum.IsDefined(settings.Translation.Provider) || !ProviderCatalog.IsAvailable(settings.Translation.Provider))
                settings.Translation.Provider = ProviderKind.GoogleWeb;
            if (!Enum.IsDefined(settings.UiLanguage)) settings.UiLanguage = UiLanguage.Auto;
            if (!Enum.IsDefined(settings.Theme)) settings.Theme = UiTheme.System;
            return Normalize(settings);
        }
        catch
        {
            Warning = UiLocalization.Get("StorageWarning");
            return Normalize(new UserSettings());
        }
    }

    /// <summary>Migrates the single legacy AI service, decrypts per-profile keys, drops invalid entries.</summary>
    private UserSettings Normalize(UserSettings settings)
    {
        settings.AiServices ??= new();
        settings.AiSecrets ??= new();
        settings.AiServices.RemoveAll(service => service is null || string.IsNullOrWhiteSpace(service.Id) || !Enum.IsDefined(service.Protocol));
        settings.AiServices = settings.AiServices.GroupBy(service => service.Id).Select(group => group.First()).ToList();
        if (settings.AiServices.Count == 0)
        {
            var legacy = settings.Translation;
            var hasLegacy = !string.IsNullOrEmpty(settings.OpenAiSecret) ||
                legacy.OpenAiEndpoint != new TranslationSettings().OpenAiEndpoint || legacy.OpenAiModel != new TranslationSettings().OpenAiModel;
            AiServiceProfile profile;
            if (hasLegacy)
            {
                var host = Uri.TryCreate(legacy.OpenAiEndpoint, UriKind.Absolute, out var uri) ? uri.Host : "AI";
                var template = AiServiceTemplates.All.FirstOrDefault(item => item.Endpoint.Length > 0 &&
                    item.Protocol == legacy.AiProtocol && AiEndpoint.SameOrigin(item.Endpoint, legacy.OpenAiEndpoint));
                profile = new AiServiceProfile
                {
                    Name = template?.Name ?? host, Protocol = legacy.AiProtocol, Endpoint = legacy.OpenAiEndpoint,
                    Model = legacy.OpenAiModel, Streaming = legacy.OpenAiStreaming
                };
                if (!string.IsNullOrEmpty(settings.OpenAiSecret)) settings.AiSecrets[profile.Id] = settings.OpenAiSecret;
            }
            else profile = AiServiceTemplates.Create(AiServiceTemplates.All[0], AiServiceTemplates.All[0].Name);
            settings.AiServices.Add(profile);
            settings.ActiveAiServiceId = profile.Id;
        }
        settings.OpenAiSecret = "";
        foreach (var service in settings.AiServices)
        {
            var unavailable = false;
            service.ApiKey = settings.AiSecrets.TryGetValue(service.Id, out var secret) ? ReadSecret(secret, out unavailable) : "";
            service.SecretUnavailable = unavailable;
        }
        settings.ApplyActiveAiService();
        settings.NormalizeFallbackOrder();
        return settings;
    }

    private string ReadSecret(string value, out bool unavailable)
    {
        unavailable = false;
        try { return SecretProtection.Unprotect(value); }
        catch
        {
            unavailable = true;
            Warning = UiLocalization.Get("StorageWarning");
            return "";
        }
    }

    public void Save(UserSettings settings)
    {
        // An unreadable key (portable folder on another PC/user) is kept as ciphertext rather than
        // erased or blocking unrelated saves; it decrypts again where it was created.
        settings.ApplyActiveAiService();
        settings.NormalizeFallbackOrder();
        var copy = settings.Copy();
        copy.DeepLSecret = settings.DeepLSecretUnavailable && string.IsNullOrEmpty(settings.Translation.DeepLApiKey)
            ? settings.DeepLSecret
            : SecretProtection.Protect(copy.Translation.DeepLApiKey);
        copy.AiSecrets = new Dictionary<string, string>();
        foreach (var service in copy.AiServices)
        {
            var blob = service.SecretUnavailable && service.ApiKey.Length == 0
                ? settings.AiSecrets.GetValueOrDefault(service.Id, "")
                : SecretProtection.Protect(service.ApiKey);
            if (blob.Length > 0) copy.AiSecrets[service.Id] = blob; // Deleted profiles drop their keys.
        }
        copy.OpenAiSecret = "";
        copy.Translation.DeepLApiKey = "";
        copy.Translation.OpenAiApiKey = "";
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(copy, _json));
        File.Move(temporary, path, true);
        settings.AiSecrets = copy.AiSecrets;
        settings.DeepLSecret = copy.DeepLSecret;
        settings.OpenAiSecret = "";
        settings.DeepLSecretUnavailable &= string.IsNullOrEmpty(settings.Translation.DeepLApiKey);
        foreach (var service in settings.AiServices) service.SecretUnavailable &= service.ApiKey.Length == 0;
    }

    public void Log(string requestId, string action, string provider, long durationMs, string outcome,
        TranslationErrorCode? errorCode = null, int? statusCode = null)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "diagnostics.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.Move(path, Path.Combine(_directory, "diagnostics.previous.jsonl"), true);
            // Deliberately excludes source text, translation, keys, URLs and remote bodies.
            var row = new { utc = DateTimeOffset.UtcNow, requestId, action, provider, durationMs, outcome,
                errorCode = errorCode?.ToString(), statusCode = statusCode is >= 100 and <= 599 ? statusCode : null };
            File.AppendAllText(path, JsonSerializer.Serialize(row) + Environment.NewLine);
        }
        catch { /* Logging failures must not prevent a translation. */ }
    }
}

public sealed class FallbackEntry
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public FallbackEntry Copy() => (FallbackEntry)MemberwiseClone();
}

internal static class AiServiceDisplay
{
    /// <summary>User-given name, else the endpoint host, else a generic label.</summary>
    public static string DisplayName(this AiServiceProfile service) =>
        !string.IsNullOrWhiteSpace(service.Name) ? service.Name.Trim()
        : Uri.TryCreate(service.Endpoint?.Trim(), UriKind.Absolute, out var uri) ? uri.Host
        : UiLocalization.Get("SettingsAiNameDefault");
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
