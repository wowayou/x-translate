using System.Runtime.ExceptionServices;

namespace XTranslate.Core;

/// <summary>One service the text may be sent to, with a detached settings snapshot.</summary>
public sealed record TranslationCandidate(string Key, string DisplayName, TranslationSettings Settings);

/// <summary>A failed candidate. <see cref="Sent"/> is false when it was skipped before any HTTP.</summary>
public sealed record TranslationAttempt(string Key, string DisplayName, TranslationErrorCode Code, int? StatusCode, bool Sent);

public sealed record FallbackResult(TranslationResult Result, TranslationCandidate Candidate, IReadOnlyList<TranslationAttempt> Failed);

/// <summary>Every candidate failed or was skipped. <see cref="Primary"/> is the first service's error.</summary>
public sealed class FallbackFailedException(IReadOnlyList<TranslationAttempt> attempts, TranslationException primary)
    : Exception(primary.Message, primary)
{
    public IReadOnlyList<TranslationAttempt> Attempts { get; } = attempts;
    public TranslationException Primary { get; } = primary;
}

/// <summary>
/// Opt-in, ordered service fallback. Candidates are tried strictly in the given order (the caller puts the
/// user's selected service first). Request problems and cancellation never switch services; configuration
/// problems are skipped without HTTP; at most <see cref="MaxSentRequests"/> requests are sent.
/// With a single candidate this behaves exactly like <see cref="TranslationService.TranslateAsync"/>.
/// Callbacks run on the caller's synchronization context.
/// </summary>
public static class TranslationFallback
{
    public const int MaxSentRequests = 3;

    /// <summary>Problems with the request itself; another service would fail the same way.</summary>
    public static bool IsRequestProblem(TranslationErrorCode code) =>
        code is TranslationErrorCode.InputEmpty or TranslationErrorCode.InputTooLong or TranslationErrorCode.MissingTargetLanguage;

    /// <summary>Raised by validation or local state before any HTTP is sent.</summary>
    public static bool IsSkippedWithoutHttp(TranslationErrorCode code) => code is
        TranslationErrorCode.MissingApiKey or TranslationErrorCode.InvalidApiKey or TranslationErrorCode.MissingModel or
        TranslationErrorCode.InvalidModel or TranslationErrorCode.InvalidEndpoint or TranslationErrorCode.UnsupportedProtocol or
        TranslationErrorCode.UnsupportedProvider or TranslationErrorCode.UnsupportedLanguage or TranslationErrorCode.RateLimitCooldown;

    public static async Task<FallbackResult> TranslateAsync(
        TranslationService service,
        string text,
        IReadOnlyList<TranslationCandidate> candidates,
        CancellationToken cancellationToken,
        Func<TranslationCandidate, IProgress<TranslationProgress>?>? progressFor = null,
        Action<TranslationCandidate, TranslationAttempt>? onFailed = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(candidates);
        var unique = candidates.DistinctBy(candidate => candidate.Key, StringComparer.Ordinal).ToArray();
        if (unique.Length == 0) throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        var failed = new List<TranslationAttempt>();
        TranslationException? primary = null;
        var sent = 0;
        foreach (var candidate in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sent >= MaxSentRequests) break;
            try
            {
                var result = await service.TranslateAsync(text, candidate.Settings, cancellationToken, progressFor?.Invoke(candidate));
                return new FallbackResult(result, candidate, failed);
            }
            catch (TranslationException error) when (!IsRequestProblem(error.Code))
            {
                if (unique.Length == 1) throw; // Disabled fallback: unchanged single-service behavior.
                primary ??= error;
                var attempt = new TranslationAttempt(candidate.Key, candidate.DisplayName, error.Code, error.StatusCode,
                    !IsSkippedWithoutHttp(error.Code));
                failed.Add(attempt);
                if (attempt.Sent) sent++;
                onFailed?.Invoke(candidate, attempt);
            }
        }
        if (primary is null) ExceptionDispatchInfo.Throw(new InvalidOperationException("No candidate was attempted."));
        throw new FallbackFailedException(failed, primary!);
    }
}
