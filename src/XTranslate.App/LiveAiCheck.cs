using System.Diagnostics;
using System.Text.Json;
using XTranslate.Core;

namespace XTranslate.App;

/// <summary>
/// Explicit, user-approved live check: one fixed sentence, one request, configured streaming mode.
/// Reads settings without saving; the report never contains the key, prompt body or remote error body.
/// </summary>
internal static class LiveAiCheck
{
    private const string FixedSentence = "Hello, world.";

    /// <param name="serviceName">Optional profile name or id; defaults to the active AI service.</param>
    public static async Task<int> RunAsync(string settingsDirectory, string output, string? serviceName = null)
    {
        var storage = new AppStorage(settingsDirectory);
        var settings = storage.Load();
        var active = serviceName is null
            ? settings.ActiveAiService
            : settings.AiServices.FirstOrDefault(service =>
                service.Id == serviceName || service.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
        var translation = settings.Translation;
        active?.ApplyTo(translation);
        string? resolved = null;
        try { resolved = AiEndpoint.Resolve(translation.AiProtocol, translation.OpenAiEndpoint, translation.OpenAiModel, translation.OpenAiStreaming).AbsoluteUri; }
        catch (TranslationException) { }
        var watch = Stopwatch.StartNew();
        object report;
        var success = false;
        try
        {
            if (active is null) throw new TranslationException("Service not found", TranslationErrorCode.UnsupportedProvider);
            if (active.SecretUnavailable) throw new TranslationException("Stored key unavailable", TranslationErrorCode.MissingApiKey);
            translation.Provider = ProviderKind.OpenAI;
            using var service = new TranslationService();
            var deltas = 0;
            var result = await service.TranslateAsync(FixedSentence, translation, CancellationToken.None,
                new SynchronousProgress(_ => deltas++));
            success = true;
            report = new { success, service = active.Name, protocol = translation.AiProtocol.ToString(), resolvedEndpoint = resolved,
                requestedModel = translation.OpenAiModel, servedModel = result.Model, streaming = translation.OpenAiStreaming,
                streamDeltas = deltas, durationMs = watch.ElapsedMilliseconds, fixedSentence = FixedSentence,
                target = translation.TargetLanguage, translation = result.Text };
        }
        catch (TranslationException error)
        {
            report = new { success, service = active?.Name, protocol = translation.AiProtocol.ToString(), resolvedEndpoint = resolved,
                requestedModel = translation.OpenAiModel, streaming = translation.OpenAiStreaming,
                errorCode = error.Code.ToString(), statusCode = error.StatusCode, durationMs = watch.ElapsedMilliseconds };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }

    private sealed class SynchronousProgress(Action<TranslationProgress> report) : IProgress<TranslationProgress>
    {
        public void Report(TranslationProgress value) => report(value);
    }
}
