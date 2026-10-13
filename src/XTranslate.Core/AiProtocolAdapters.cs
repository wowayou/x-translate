using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace XTranslate.Core;

/// <summary>
/// Anthropic Messages API (https://docs.anthropic.com/en/api/messages). One request, no retries.
/// Success requires stop_reason end_turn/stop_sequence; streaming additionally requires message_stop.
/// </summary>
internal static class AnthropicProtocol
{
    public const string ApiVersion = "2023-06-01";
    // Required by the API. Large enough for the 3000-character input limit in any target language.
    public const int MaxTokens = 4096;

    public static HttpRequestMessage Build(Uri uri, string key, string model, string system, string text, bool stream)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (stream) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var payload = new { model, max_tokens = MaxTokens, system, stream, messages = new[] { new { role = "user", content = text } } };
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    public static (string Text, string Model) Parse(JsonElement root, string model)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw Invalid();
        if (!root.TryGetProperty("stop_reason", out var stop) || stop.ValueKind != JsonValueKind.String || !IsComplete(stop.GetString()))
            throw SseReader.Incomplete();
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type)) throw Invalid();
            // thinking / redacted_thinking and future block types are not translation text.
            if (type.GetString() != "text") continue;
            if (!block.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String) throw Invalid();
            text.Append(value.GetString());
        }
        if (string.IsNullOrWhiteSpace(text.ToString())) throw SseReader.Empty();
        return (text.ToString(), ReadModel(root, model));
    }

    public static async Task<(string Text, string Model)> ReadStreamAsync(
        Stream stream, string model, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var textBlocks = new HashSet<int>();
        string? stopReason = null;
        try
        {
            await foreach (var (eventType, data) in SseReader.ReadEventsAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw Invalid();
                var type = root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                    ? typeValue.GetString() : eventType;
                switch (type)
                {
                    case "error":
                        throw SseReader.StreamError();
                    case "message_start":
                        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                            model = ReadModel(message, model);
                        break;
                    case "content_block_start":
                        if (!root.TryGetProperty("content_block", out var block) || block.ValueKind != JsonValueKind.Object) throw Invalid();
                        if (block.TryGetProperty("type", out var blockType) && blockType.GetString() == "text")
                        {
                            textBlocks.Add(ReadIndex(root));
                            if (block.TryGetProperty("text", out var initial) && initial.ValueKind == JsonValueKind.String)
                                Append(initial.GetString()!, text, model, progress);
                        }
                        break;
                    case "content_block_delta":
                        if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) throw Invalid();
                        // thinking_delta, signature_delta, citations and tool input are never translation text.
                        if (!delta.TryGetProperty("type", out var deltaType) || deltaType.GetString() != "text_delta") break;
                        if (!textBlocks.Contains(ReadIndex(root)) || !delta.TryGetProperty("text", out var added) ||
                            added.ValueKind != JsonValueKind.String) throw Invalid();
                        Append(added.GetString()!, text, model, progress);
                        break;
                    case "message_delta":
                        if (root.TryGetProperty("delta", out var messageDelta) && messageDelta.ValueKind == JsonValueKind.Object &&
                            messageDelta.TryGetProperty("stop_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                            stopReason = reason.GetString();
                        break;
                    case "message_stop":
                        if (!IsComplete(stopReason)) throw SseReader.Incomplete();
                        if (string.IsNullOrWhiteSpace(text.ToString())) throw SseReader.Empty();
                        return (text.ToString(), model);
                    // ping, content_block_stop and new event types are ignored, as the API requires.
                }
            }
        }
        catch (JsonException) { throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
        throw SseReader.Incomplete(); // EOF without message_stop.
    }

    private static bool IsComplete(string? stopReason) => stopReason is "end_turn" or "stop_sequence";

    private static int ReadIndex(JsonElement root) =>
        root.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var value) && value >= 0
            ? value : throw Invalid();

    private static string ReadModel(JsonElement element, string fallback) =>
        element.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(model.GetString())
            ? model.GetString()! : fallback;

    private static void Append(string added, StringBuilder text, string model, IProgress<TranslationProgress>? progress)
    {
        if (added.Length == 0) return;
        if (text.Length + added.Length > SseReader.MaxOutputCharacters) throw SseReader.TooLarge();
        text.Append(added);
        progress?.Report(new TranslationProgress(added, model));
    }

    private static TranslationException Invalid() => SseReader.Invalid();
}

/// <summary>
/// Gemini Developer API generateContent / streamGenerateContent (https://ai.google.dev/api/generate-content).
/// The key is sent in x-goog-api-key, never in the URL. Success requires finishReason STOP.
/// </summary>
internal static class GeminiProtocol
{
    public static HttpRequestMessage Build(Uri uri, string key, string system, string text, bool stream)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        if (stream) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text } } } }
        };
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    public static (string Text, string Model) Parse(JsonElement root, string model)
    {
        var text = new StringBuilder();
        var finish = ReadChunk(root, text, ref model, null, requireCandidate: true);
        if (finish != "STOP") throw SseReader.Incomplete();
        if (string.IsNullOrWhiteSpace(text.ToString())) throw SseReader.Empty();
        return (text.ToString(), model);
    }

    public static async Task<(string Text, string Model)> ReadStreamAsync(
        Stream stream, string model, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var stopped = false;
        try
        {
            await foreach (var (_, data) in SseReader.ReadEventsAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(data);
                var before = text.Length;
                var finish = ReadChunk(document.RootElement, text, ref model, progress, requireCandidate: false);
                if (stopped && text.Length != before) throw SseReader.Invalid(); // No text after the final candidate.
                if (finish is null) continue;
                if (finish != "STOP") throw SseReader.Incomplete();
                stopped = true; // A trailing usage-only chunk may still follow.
            }
        }
        catch (JsonException) { throw SseReader.Invalid(); }
        catch (DecoderFallbackException) { throw SseReader.Invalid(); }
        // Gemini has no [DONE] marker; completion is STOP followed by a clean end of stream.
        if (!stopped) throw SseReader.Incomplete();
        if (string.IsNullOrWhiteSpace(text.ToString())) throw SseReader.Empty();
        return (text.ToString(), model);
    }

    /// <summary>Returns the candidate's finishReason, or null if this chunk has none.</summary>
    private static string? ReadChunk(JsonElement root, StringBuilder text, ref string model,
        IProgress<TranslationProgress>? progress, bool requireCandidate)
    {
        if (root.ValueKind != JsonValueKind.Object) throw SseReader.Invalid();
        if (root.TryGetProperty("error", out _)) throw SseReader.StreamError();
        if (root.TryGetProperty("modelVersion", out var version) && version.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(version.GetString()))
            model = version.GetString()!;
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object &&
            feedback.TryGetProperty("blockReason", out var block) && block.ValueKind == JsonValueKind.String)
            throw SseReader.Incomplete(); // Prompt blocked; not a translation.
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
            return requireCandidate ? throw SseReader.Invalid() : null;
        if (candidates.GetArrayLength() != 1) throw SseReader.Invalid(); // We request one candidate.
        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object) throw SseReader.Invalid();
        if (candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("parts", out var parts))
        {
            if (parts.ValueKind != JsonValueKind.Array) throw SseReader.Invalid();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) throw SseReader.Invalid();
                if (part.TryGetProperty("functionCall", out _)) throw SseReader.Incomplete();
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                if (!part.TryGetProperty("text", out var value)) continue; // Signatures and other metadata parts.
                if (value.ValueKind != JsonValueKind.String) throw SseReader.Invalid();
                var added = value.GetString()!;
                if (added.Length == 0) continue;
                if (text.Length + added.Length > SseReader.MaxOutputCharacters) throw SseReader.TooLarge();
                text.Append(added);
                progress?.Report(new TranslationProgress(added, model));
            }
        }
        if (!candidate.TryGetProperty("finishReason", out var finish) || finish.ValueKind == JsonValueKind.Null) return null;
        return finish.ValueKind == JsonValueKind.String ? finish.GetString() : throw SseReader.Invalid();
    }
}
