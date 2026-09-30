namespace XTranslate.Core;

public enum ProviderKind
{
    GoogleWeb = 0,
    DeepL = 1,
    OpenAI = 2,
    MicrosoftTranslator = 3,
    Papago = 4
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
    public string OpenAiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = "gpt-4o-mini";
}

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
    RequestFailed
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
