using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LyricsService(LyricsProviderRegistry providers)
{
    private readonly LyricsCache _cache = new();
    private readonly object _cacheGate = new();
    private long _cacheGeneration;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, LyricsSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(query.Title)) return LyricsDocument.Empty;
        var kind = settings.Mode == LyricsMode.LocalLrc ? LyricsProviderKind.LocalLrc : settings.Provider;
        var key = $"{kind}|{LyricsMatcher.Normalize(query.Title)}|{LyricsMatcher.Normalize(query.Artist)}|{LyricsMatcher.Normalize(query.Album)}|{(long)query.Duration.TotalSeconds}";
        if (kind != LyricsProviderKind.LocalLrc && _cache.TryGet(key, out var cached)) return cached;
        var generation = Interlocked.Read(ref _cacheGeneration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        try
        {
            var document = await providers.Get(kind).QueryAsync(query, deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheGate)
                if (kind != LyricsProviderKind.LocalLrc && generation == Interlocked.Read(ref _cacheGeneration)) _cache.Put(key, document);
            return document;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or XmlException or FormatException or RegexMatchTimeoutException)
        { return LyricsDocument.Empty; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return LyricsDocument.Empty; }
    }
    public void ClearCache() { lock (_cacheGate) { Interlocked.Increment(ref _cacheGeneration); _cache.Clear(); } }
}
