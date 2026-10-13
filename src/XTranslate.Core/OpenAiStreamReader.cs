using System.Text;
using System.Text.Json;

namespace XTranslate.Core;

/// <summary>Reads the OpenAI Chat Completions SSE protocol without buffering the HTTP body.</summary>
internal static class OpenAiStreamReader
{
    public static async Task<(string Text, string Model)> ReadAsync(
        Stream stream, string requestedModel, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var model = requestedModel;
        var stopped = false;
        try
        {
            await foreach (var (eventType, data) in SseReader.ReadEventsAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (eventType == "error") throw StreamError();
                if (data == "[DONE]")
                {
                    if (!stopped) throw Incomplete();
                    if (string.IsNullOrWhiteSpace(text.ToString()))
                        throw new TranslationException("AI 返回了空翻译结果。", TranslationErrorCode.EmptyResponse);
                    return (text.ToString(), model);
                }

                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw Invalid();
                if (root.TryGetProperty("error", out _)) throw StreamError();
                if (root.TryGetProperty("model", out var responseModel) && responseModel.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(responseModel.GetString()))
                    model = responseModel.GetString()!;
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                    throw Invalid();
                // Optional usage-only trailer, not a translation or an end marker.
                if (choices.GetArrayLength() == 0)
                {
                    if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) throw Invalid();
                    continue;
                }
                // We request one completion. Do not silently mix multiple choices.
                if (choices.GetArrayLength() != 1) throw Invalid();
                var choice = choices[0];
                if (choice.ValueKind != JsonValueKind.Object ||
                    !choice.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number ||
                    !index.TryGetInt32(out var indexValue) || indexValue != 0 ||
                    !choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                    throw Invalid();
                if (delta.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null &&
                    (refusal.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(refusal.GetString())))
                    throw Incomplete();
                if ((delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null) ||
                    (delta.TryGetProperty("function_call", out var function) && function.ValueKind != JsonValueKind.Null))
                    throw Incomplete();

                var finishes = false;
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                {
                    if (reason.ValueKind != JsonValueKind.String) throw Invalid();
                    if (reason.GetString() != "stop") throw Incomplete();
                    finishes = true;
                }
                if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                {
                    if (content.ValueKind != JsonValueKind.String) throw Invalid();
                    var added = content.GetString()!;
                    if (added.Length > 0)
                    {
                        if (stopped) throw Invalid();
                        if (text.Length + added.Length > SseReader.MaxOutputCharacters) throw TooLarge();
                        text.Append(added);
                        progress?.Report(new TranslationProgress(added, model));
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                stopped |= finishes;
            }
        }
        catch (JsonException) { throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
        // EOF, including a finish_reason without [DONE], cannot prove completion.
        throw Incomplete();
    }

    private static TranslationException Invalid() => SseReader.Invalid();
    private static TranslationException Incomplete() => SseReader.Incomplete();
    private static TranslationException TooLarge() => SseReader.TooLarge();
    private static TranslationException StreamError() => SseReader.StreamError();
}
