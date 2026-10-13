using System.Runtime.CompilerServices;
using System.Text;

namespace XTranslate.Core;

/// <summary>
/// Bounded Server-Sent Events framing shared by all AI protocols. Does not buffer the body,
/// never reconnects, and never dispatches an event that lacks its terminating blank line.
/// </summary>
internal static class SseReader
{
    // Limits are decoded UTF-16 characters, not token budgets or wire bytes.
    internal const int MaxOutputCharacters = 50_000;
    internal const int MaxEventCharacters = 65_536;
    internal const int MaxResponseCharacters = 2_097_152;

    public static async IAsyncEnumerable<(string EventType, string Data)> ReadEventsAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        var eventType = "";
        var hasData = false;
        var eventCharacters = 0;
        await foreach (var line in ReadLinesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (line.Length == 0)
            {
                if (hasData) yield return (eventType, data.ToString());
                data.Clear();
                hasData = false;
                eventType = "";
                eventCharacters = 0;
                continue;
            }
            eventCharacters += line.Length + 1;
            if (eventCharacters > MaxEventCharacters) throw TooLarge();
            if (line[0] == ':') continue; // Heartbeat/comment.
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "data")
            {
                if (hasData) data.Append('\n');
                data.Append(value);
                hasData = true;
            }
            else if (field == "event") eventType = value;
            // id/retry/unknown fields do not affect this non-reconnecting client.
        }
        // SSE dispatch requires a blank line; never promote a truncated event.
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var totalCharacters = 0;
        var first = true;
        var skipLf = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalCharacters += count;
            if (totalCharacters > MaxResponseCharacters) throw TooLarge();
            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (first)
                {
                    first = false;
                    if (character == '\uFEFF') continue; // The optional UTF-8 BOM.
                }
                if (skipLf)
                {
                    skipLf = false;
                    if (character == '\n') continue;
                }
                if (character is '\r' or '\n')
                {
                    skipLf = character == '\r';
                    yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    // Unlike ReadLineAsync, this bounds allocation before seeing a newline.
                    if (line.Length >= MaxEventCharacters) throw TooLarge();
                    line.Append(character);
                }
            }
        }
    }

    internal static TranslationException Invalid() => new("AI 返回了无效的流式响应。", TranslationErrorCode.InvalidResponse);
    internal static TranslationException Incomplete() => new("AI 未返回完整译文，请手动重试。", TranslationErrorCode.IncompleteResponse);
    internal static TranslationException TooLarge() => new("AI 响应超出安全大小限制。", TranslationErrorCode.ResponseTooLarge);
    internal static TranslationException StreamError() => new("AI 流式请求失败，请检查服务设置后手动重试。", TranslationErrorCode.RequestFailed);
    internal static TranslationException Empty() => new("AI 返回了空翻译结果。", TranslationErrorCode.EmptyResponse);
}
