using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LyricsService(LyricsProviderRegistry providers)
{
    private static readonly LyricsProviderKind[] OnlineProviders = [LyricsProviderKind.NetEase, LyricsProviderKind.QqMusic, LyricsProviderKind.Kugou, LyricsProviderKind.Lrclib, LyricsProviderKind.Amll];
    private readonly LyricsCache _cache = new();
    private readonly object _cacheGate = new();
    private long _cacheGeneration;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken) =>
        (await QueryDetailedAsync(query, settings, cancellationToken).ConfigureAwait(false)).Document;

    public async Task<LyricsQueryResult> QueryDetailedAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(query.Title)) return new(LyricsDocument.Empty, LyricsQueryStatus.Disabled);
        var kind = settings.Mode == LyricsMode.LocalLrc ? LyricsProviderKind.LocalLrc : settings.Provider;
        // Keep sub-second duration differences in the cache key. A same-metadata Apple Music
        // replacement (for example an edit/live file with the same displayed title) must not
        // inherit a document fetched for a nearby duration merely because both durations were
        // truncated to the same whole second.
        var key = $"{kind}|{query.TrackIdentity}|{LyricsMatcher.Normalize(query.Title)}|{LyricsMatcher.Normalize(query.Artist)}|{LyricsMatcher.Normalize(query.Album)}|{query.Duration.Ticks}";
        if (kind != LyricsProviderKind.LocalLrc && _cache.TryGet(key, out var cached)) return new(cached, LyricsQueryStatus.Found);
        var generation = Interlocked.Read(ref _cacheGeneration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        try
        {
            var primary = await QueryProviderAsync(kind, query, deadline.Token).ConfigureAwait(false);
            var document = Validate(primary.Document, query);
            var fallbackFailed = false;
            if (document.Lines.Count == 0 && kind != LyricsProviderKind.LocalLrc)
            {
                var fallback = await QueryFallbacksAsync(kind, query, deadline.Token).ConfigureAwait(false);
                document = fallback.Document;
                fallbackFailed = fallback.Failed;
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheGate)
                if (document.Lines.Count > 0 && kind != LyricsProviderKind.LocalLrc && generation == Interlocked.Read(ref _cacheGeneration)) _cache.Put(key, document);
            var failed = document.Lines.Count == 0 && (primary.Failed || fallbackFailed);
            return new(document, document.Lines.Count > 0 ? LyricsQueryStatus.Found : failed ? LyricsQueryStatus.Failed : LyricsQueryStatus.NotFound);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidDataException or XmlException or FormatException or RegexMatchTimeoutException)
        { return new(LyricsDocument.Empty, LyricsQueryStatus.Failed); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(LyricsDocument.Empty, LyricsQueryStatus.Failed); }
    }
    private sealed record ProviderResult(LyricsDocument Document, bool Failed);
    private async Task<ProviderResult> QueryProviderAsync(LyricsProviderKind kind, LyricsQuery query, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { return new(Validate(await providers.Get(kind).QueryAsync(query, timeout.Token).ConfigureAwait(false), query), false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(LyricsDocument.Empty, true); }
    }
    private async Task<(LyricsDocument Document, bool Failed)> QueryFallbacksAsync(LyricsProviderKind primary, LyricsQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(token);
        // At most four provider requests are active. Allow a short quality window
        // after the first usable result so a slower, stronger verified match can
        // win, then cancel and drain the rest.
        var pending = OnlineProviders.Where(kind => kind != primary).Select(kind => QueryProviderAsync(kind, query, stage.Token)).ToList();
        var all = pending.ToArray();
        var candidates = new List<(LyricsDocument Document, int Index)>();
        var failed = false;
        Task? qualityWindow = null;
        try
        {
            while (pending.Count > 0)
            {
                Task<ProviderResult> completed;
                if (qualityWindow is null)
                    completed = await Task.WhenAny(pending).ConfigureAwait(false);
                else
                {
                    var waitSet = pending.Cast<Task>().Append(qualityWindow).ToArray();
                    var finished = await Task.WhenAny(waitSet).ConfigureAwait(false);
                    if (ReferenceEquals(finished, qualityWindow)) break;
                    completed = (Task<ProviderResult>)finished;
                }
                var index = all.AsSpan().IndexOf(completed);
                pending.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                var document = result.Document;
                token.ThrowIfCancellationRequested();
                failed |= result.Failed;
                if (document.Lines.Count == 0) continue;
                candidates.Add((document, index));
                qualityWindow ??= Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            return (candidates.OrderByDescending(candidate => candidate.Document.Match?.Score ?? 0)
                .ThenBy(candidate => candidate.Index).Select(candidate => candidate.Document).FirstOrDefault() ?? LyricsDocument.Empty, failed);
        }
        finally
        {
            stage.Cancel();
            try { await Task.WhenAll(all).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidDataException or XmlException or FormatException or RegexMatchTimeoutException or OperationCanceledException) { }
        }
    }

    private static LyricsDocument Validate(LyricsDocument document, LyricsQuery query)
    {
        if (document.Lines.Count == 0 || document.Match is null) return LyricsDocument.Empty;
        var match = document.Match;
        if (!string.IsNullOrEmpty(query.TrackIdentity) && !string.IsNullOrEmpty(match.TrackIdentity) && match.TrackIdentity != query.TrackIdentity)
            return LyricsDocument.Empty;
        if (string.IsNullOrWhiteSpace(match.CandidateId)) return LyricsDocument.Empty;
        var score = LyricsMatcher.Score(query, match.Title, match.Artist, match.Album, match.DurationSeconds);
        return score < 4 ? LyricsDocument.Empty : document with { Match = match with { Score = score, TrackIdentity = query.TrackIdentity } };
    }
    public void ClearCache() { lock (_cacheGate) { Interlocked.Increment(ref _cacheGeneration); _cache.Clear(); } }
}
