using System.Buffers;
using System.Text.Json;

namespace XTranslate.Core;

internal static class ResponseReader
{
    internal const int MaxJsonBytes = 1_048_576;

    public static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaxJsonBytes) throw TooLarge();
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(0, 8192), token).ConfigureAwait(false)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (bytes.Length + count > MaxJsonBytes) throw TooLarge();
                bytes.Write(buffer, 0, count);
            }
            // JsonDocument retains this array; disposing MemoryStream does not invalidate it.
            return JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
        }
        catch (JsonException)
        {
            throw new TranslationException("服务返回了无效的 JSON 响应。", TranslationErrorCode.InvalidResponse);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static TranslationException TooLarge() => new("服务响应超出安全大小限制。", TranslationErrorCode.ResponseTooLarge);
}
