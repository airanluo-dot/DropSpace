using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>A validated, ephemeral view of one request. It is never a complete cache result.
/// Consumers must recheck IsCurrent on their dispatcher, not only when scheduling the update.</summary>
public sealed class LyricsTranslationProgress(string requestIdentity, long cacheGeneration,
    LyricsDocument document, int lineId, int completedLineCount, int totalLineCount, Func<bool> isCurrent)
{
    public string RequestIdentity { get; } = requestIdentity;
    public long CacheGeneration { get; } = cacheGeneration;
    public LyricsDocument Document { get; } = document;
    public int LineId { get; } = lineId;
    public int CompletedLineCount { get; } = completedLineCount;
    public int TotalLineCount { get; } = totalLineCount;
    public bool IsEphemeral => true;
    public bool IsCurrent => isCurrent();
}

/// <summary>Backend-neutral request lifetime, current playback priority, and awaited publication.
/// A one-shot backend may ignore progress; a persistent engine uses the same line-level contract.</summary>
public sealed class LyricsTranslationProgressContext(
    Func<TimeSpan> getPosition,
    Func<bool> isCurrent,
    Func<LyricsTranslationProgress, CancellationToken, Task> publishAsync,
    string? requestIdentity = null)
{
    public string RequestIdentity { get; } = requestIdentity ?? Guid.NewGuid().ToString("N");
    public long CacheGeneration { get; private init; }
    public TimeSpan Position => getPosition();
    public bool IsCurrent => isCurrent();

    public LyricsTranslationProgressContext WithFence(long cacheGeneration, Func<bool> fence) =>
        new(getPosition, () => IsCurrent && fence(), publishAsync, RequestIdentity) { CacheGeneration = cacheGeneration };

    public Task ReportAsync(LyricsDocument document, int lineId, int completedLineCount, int totalLineCount,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent) return Task.CompletedTask;
        var update = new LyricsTranslationProgress(RequestIdentity, CacheGeneration, document, lineId,
            completedLineCount, totalLineCount, () => !token.IsCancellationRequested && IsCurrent);
        return publishAsync(update, token);
    }
}
