using System.Text.RegularExpressions;

namespace XTranslate.Core;

/// <summary>Deterministic URL rules; never probes routes or changes the configured origin.</summary>
public static partial class AiEndpoint
{
    /// <summary>Final request URL for a protocol. Credentials never appear in it.</summary>
    public static Uri Resolve(AiProtocol protocol, string? endpoint, string? model, bool streaming) => protocol switch
    {
        AiProtocol.OpenAiChat => ResolveChatCompletions(endpoint),
        AiProtocol.AnthropicMessages => ResolveAnthropic(endpoint),
        AiProtocol.GeminiGenerateContent => ResolveGemini(endpoint, model, streaming),
        _ => throw new TranslationException("不支持的 AI 协议。", TranslationErrorCode.UnsupportedProtocol)
    };

    /// <summary>Display label for provenance. Host is shown so relays are never mistaken for vendors.</summary>
    public static string ProviderName(AiProtocol protocol, Uri requestUri) => protocol switch
    {
        AiProtocol.AnthropicMessages => "Anthropic · " + requestUri.Host,
        AiProtocol.GeminiGenerateContent => "Gemini · " + requestUri.Host,
        _ => "OpenAI 兼容 · " + requestUri.Host
    };

    public static Uri ResolveChatCompletions(string? value)
    {
        var uri = Validate(value);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ||
            IsGeminiMethod(path))
            throw WrongProtocol();
        if (path.Length == 0)
            path = uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) ? "/chat/completions" : "/v1/chat/completions";
        else if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            path += "/chat/completions";
        else
            return uri; // Explicit custom routes are preserved, not guessed.
        return new UriBuilder(uri) { Path = path }.Uri;
    }

    /// <summary>Base URL in the official SDK sense: host or prefix, then /v1/messages.</summary>
    private static Uri ResolveAnthropic(string? value)
    {
        var uri = Validate(value);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) || IsGeminiMethod(path))
            throw WrongProtocol();
        if (path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)) return uri;
        path += path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/messages" : "/v1/messages";
        return new UriBuilder(uri) { Path = path }.Uri;
    }

    /// <summary>Base URL plus API version, then models/{model}:method. The key goes in a header.</summary>
    private static Uri ResolveGemini(string? value, string? model, bool streaming)
    {
        var uri = Validate(value);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            throw WrongProtocol();
        // A pasted full method URL or a models/ collection would embed a second model; ask for the base.
        if (IsGeminiMethod(path) || path.Contains("/models", StringComparison.OrdinalIgnoreCase))
            throw new TranslationException("Gemini 地址请填写基础地址（如 https://generativelanguage.googleapis.com），模型名单独填写。", TranslationErrorCode.InvalidEndpoint);
        if (!VersionSuffix().IsMatch(path)) path += "/v1beta";
        var method = streaming ? "streamGenerateContent" : "generateContent";
        var builder = new UriBuilder(uri) { Path = $"{path}/models/{NormalizeGeminiModel(model)}:{method}" };
        if (streaming) builder.Query = "alt=sse";
        return builder.Uri;
    }

    /// <summary>Gemini puts the model in the URL path, so only a strict character set is allowed.</summary>
    public static string NormalizeGeminiModel(string? model)
    {
        var value = (model ?? "").Trim();
        if (value.StartsWith("models/", StringComparison.Ordinal)) value = value["models/".Length..];
        if (!ModelPattern().IsMatch(value))
            throw new TranslationException("模型名只能包含字母、数字、点、下划线和连字符。", TranslationErrorCode.InvalidModel);
        return value;
    }

    public static Uri Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new TranslationException("接口地址必须使用 HTTPS，且不能包含凭据、查询参数或片段。", TranslationErrorCode.InvalidEndpoint);
        return uri;
    }

    public static bool SameOrigin(string? first, string? second) =>
        Uri.TryCreate(first?.Trim(), UriKind.Absolute, out var a) && Uri.TryCreate(second?.Trim(), UriKind.Absolute, out var b) &&
        a.Scheme == b.Scheme && a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static bool IsGeminiMethod(string path) =>
        path.Contains(":generateContent", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(":streamGenerateContent", StringComparison.OrdinalIgnoreCase);

    private static TranslationException WrongProtocol() =>
        new("该地址与所选 AI 协议不一致，请检查协议选择。", TranslationErrorCode.UnsupportedProtocol);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ModelPattern();

    [GeneratedRegex(@"/v\d+(alpha|beta)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();
}
