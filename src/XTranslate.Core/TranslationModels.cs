namespace XTranslate.Core;

public enum ProviderKind
{
    GoogleWeb = 0,
    DeepL = 1,
    /// <summary>Any configured AI service; the wire protocol is <see cref="TranslationSettings.AiProtocol"/>.</summary>
    OpenAI = 2,
    MicrosoftTranslator = 3,
    Papago = 4
}

/// <summary>Native AI wire protocols. Values are persisted; append only.</summary>
public enum AiProtocol
{
    OpenAiChat = 0,
    AnthropicMessages = 1,
    GeminiGenerateContent = 2
}

public sealed record ProviderDefinition(
    ProviderKind Kind,
    string Id,
    bool IsAvailable,
    bool IsAi,
    bool RequiresApiKey);

public static class ProviderCatalog
{
    private static readonly IReadOnlyList<ProviderDefinition> Definitions = Array.AsReadOnly(new[]
    {
        new ProviderDefinition(ProviderKind.GoogleWeb, "google-web", true, false, false),
        new ProviderDefinition(ProviderKind.DeepL, "deepl", true, false, true),
        new ProviderDefinition(ProviderKind.OpenAI, "openai-compatible", true, true, true),
        new ProviderDefinition(ProviderKind.MicrosoftTranslator, "microsoft-translator", false, false, true),
        new ProviderDefinition(ProviderKind.Papago, "papago", false, false, true)
    });

    public static IReadOnlyList<ProviderDefinition> All => Definitions;

    public static bool IsAvailable(ProviderKind provider) =>
        Definitions.FirstOrDefault(definition => definition.Kind == provider)?.IsAvailable == true;
}

public sealed class TranslationSettings
{
    public ProviderKind Provider { get; set; } = ProviderKind.GoogleWeb;
    public string TargetLanguage { get; set; } = "zh-CN";
    public string DeepLApiKey { get; set; } = string.Empty;
    public string DeepLEndpoint { get; set; } = "https://api-free.deepl.com/v2/translate";
    // The OpenAi* names are kept for settings.json compatibility. They describe the active AI
    // service of any protocol; the application copies them from the selected AiServiceProfile.
    public AiProtocol AiProtocol { get; set; } = AiProtocol.OpenAiChat;
    public string OpenAiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = "gpt-4o-mini";
    public bool OpenAiStreaming { get; set; }

    // Configuration snapshots must not create JSON strings containing plaintext keys.
    public TranslationSettings Copy() => (TranslationSettings)MemberwiseClone();
}

/// <summary>A named AI service: protocol, address, model and its own credential.</summary>
public sealed class AiServiceProfile
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    public AiProtocol Protocol { get; set; }
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Streaming { get; set; }
    /// <summary>Plaintext only in memory; storage persists a DPAPI blob separately.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string ApiKey { get; set; } = "";
    /// <summary>A stored credential exists but cannot be decrypted (other PC or Windows user).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool SecretUnavailable { get; set; }

    public AiServiceProfile Copy() => (AiServiceProfile)MemberwiseClone();

    public void ApplyTo(TranslationSettings settings)
    {
        settings.AiProtocol = Protocol;
        settings.OpenAiEndpoint = Endpoint;
        settings.OpenAiApiKey = ApiKey;
        settings.OpenAiModel = Model;
        settings.OpenAiStreaming = Streaming;
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public sealed record AiServiceTemplate(string Id, string Name, AiProtocol Protocol, string Endpoint, string Model);

/// <summary>Default services. No template carries a credential; models are editable starting points.</summary>
public static class AiServiceTemplates
{
    public static IReadOnlyList<AiServiceTemplate> All { get; } = Array.AsReadOnly(new[]
    {
        new AiServiceTemplate("openai", "OpenAI", AiProtocol.OpenAiChat, "https://api.openai.com/v1", "gpt-6-luna"),
        new AiServiceTemplate("deepseek", "DeepSeek", AiProtocol.OpenAiChat, "https://api.deepseek.com", "deepseek-flash"),
        new AiServiceTemplate("anthropic", "Anthropic Claude", AiProtocol.AnthropicMessages, "https://api.anthropic.com", "claude-haiku-5-5"),
        new AiServiceTemplate("gemini", "Google Gemini", AiProtocol.GeminiGenerateContent, "https://generativelanguage.googleapis.com", "gemini-3.8-flash"),
        new AiServiceTemplate("custom", "", AiProtocol.OpenAiChat, "", "")
    });

    public static AiServiceProfile Create(AiServiceTemplate template, string name) => new()
    {
        Name = name, Protocol = template.Protocol, Endpoint = template.Endpoint, Model = template.Model
    };
}

/// <summary>Provisional AI text, not a completed translation. Reports contain only new text.</summary>
public sealed record TranslationProgress(string TextDelta, string Model);

public sealed record TranslationResult(
    string Text,
    string ProviderName,
    string EndpointType,
    string? Model,
    long DurationMs,
    string SourceLanguage,
    string TargetLanguage);

public enum TranslationErrorCode
{
    Unknown,
    InputEmpty,
    InputTooLong,
    UnsupportedProvider,
    Timeout,
    InvalidResponse,
    EmptyResponse,
    Network,
    MissingTargetLanguage,
    MissingApiKey,
    MissingModel,
    InvalidEndpoint,
    Unauthorized,
    Forbidden,
    RateLimited,
    RateLimitCooldown,
    ServerUnavailable,
    RequestFailed,
    StreamingNotSupported,
    IncompleteResponse,
    ResponseTooLarge,
    UnsupportedProtocol,
    UnsupportedLanguage,
    InvalidApiKey,
    BadRequest,
    NotFound,
    MethodNotAllowed,
    RedirectDenied,
    InvalidModel
}

public sealed class TranslationException : Exception
{
    public TranslationErrorCode Code { get; }
    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    public TranslationException(
        string message,
        TranslationErrorCode code = TranslationErrorCode.Unknown,
        int? statusCode = null,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public TranslationException(
        string message,
        Exception innerException,
        TranslationErrorCode code = TranslationErrorCode.Unknown,
        TimeSpan? retryAfter = null)
        : base(message, innerException)
    {
        Code = code;
        RetryAfter = retryAfter;
    }
}
