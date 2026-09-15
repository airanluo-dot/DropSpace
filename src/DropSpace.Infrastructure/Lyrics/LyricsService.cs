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
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(query.Title)) return LyricsDocument.Empty;
        var kind = settings.Mode == LyricsMode.LocalLrc ? LyricsProviderKind.LocalLrc : settings.Provider;
        var key = $"{kind}|{LyricsMatcher.Normalize(query.Title)}|{LyricsMatcher.Normalize(query.Artist)}|{LyricsMatcher.Normalize(query.Album)}|{(long)query.Duration.TotalSeconds}";
        if (kind != LyricsProviderKind.LocalLrc && _cache.TryGet(key, out var cached)) return cached;
        var generation = Interlocked.Read(ref _cacheGeneration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        try
        {
            var document = await QueryProviderAsync(kind, query, deadline.Token).ConfigureAwait(false);
            if (document.Lines.Count == 0 && kind != LyricsProviderKind.LocalLrc)
                document = await QueryFallbacksAsync(kind, query, deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheGate)
                if (document.Lines.Count > 0 && kind != LyricsProviderKind.LocalLrc && generation == Interlocked.Read(ref _cacheGeneration)) _cache.Put(key, document);
            return document;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or XmlException or FormatException or RegexMatchTimeoutException)
        { return LyricsDocument.Empty; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return LyricsDocument.Empty; }
    }
    private async Task<LyricsDocument> QueryProviderAsync(LyricsProviderKind kind, LyricsQuery query, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { return await providers.Get(kind).QueryAsync(query, timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or XmlException or FormatException or RegexMatchTimeoutException or OperationCanceledException)
        { return LyricsDocument.Empty; }
    }
    private async Task<LyricsDocument> QueryFallbacksAsync(LyricsProviderKind primary, LyricsQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(token);
        // At most four provider requests are active; the first timed result wins.
        // Cancel and drain the others so track changes cannot leak background jobs.
        var pending = OnlineProviders.Where(kind => kind != primary).Select(kind => QueryProviderAsync(kind, query, stage.Token)).ToList();
        var all = pending.ToArray();
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var document = await completed.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (document.Lines.Count > 0) return document;
            }
            return LyricsDocument.Empty;
        }
        finally { stage.Cancel(); await Task.WhenAll(all).ConfigureAwait(false); }
    }
    public void ClearCache() { lock (_cacheGate) { Interlocked.Increment(ref _cacheGeneration); _cache.Clear(); } }
}
