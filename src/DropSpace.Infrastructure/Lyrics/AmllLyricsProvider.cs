using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class AmllLyricsProvider(LyricsHttpClient http) : IProgressiveLyricsProvider, ILyricsResponseCache
{
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CachedLyrics> _cache = new(StringComparer.Ordinal);
    private int _cacheBytes;
    private long _generation, _order;
    private sealed record CachedLyrics(string Text, int Bytes, long Order);
    private sealed record Candidate(string Id, string Title, string Artist, string Album, double Score);
    public LyricsProviderKind Kind => LyricsProviderKind.Amll;
    public void ClearResponseCache()
    { lock (_cacheGate) { _cache.Clear(); _cacheBytes = 0; _generation++; } }
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, cancellationToken, _ => { }).ConfigureAwait(false);

    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken,
        Action<LyricsDocument> reportCandidate)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.BypassProviderResponseCache) ClearResponseCache();
        var original = LyricsDocument.Empty;
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);
        var searchUrl = $"https://api.amll.dev/v1/lyrics/search?musicName={Escape(LyricsMatcher.SearchTitle(query.Title))}&pageSize=100";
        using var search = await http.GetAsync(searchUrl, cancellationToken);
        var data = Data(search.RootElement);
        var more = HasMore(data);
        var candidates = Candidates(data, query, cancellationToken).ToArray();
        // Only a truncated, identity-empty wide search gets one narrower request.
        // No unbounded pagination or retry around rejection; the caller's provider
        // and shared translation deadlines govern both requests.
        if (candidates.Length == 0 && more)
        {
            var artist = LyricsMatcher.SearchArtists(query).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(artist))
            {
                using var narrowed = await http.GetAsync(searchUrl + "&artistName=" + Escape(artist), cancellationToken);
                candidates = Candidates(Data(narrowed.RootElement), query, cancellationToken).ToArray();
            }
        }
        LyricsRequestTrace.Record("search-result", new { provider = "Amll", count = Array(data, "items").Count(), eligible = candidates.Length, more });
        var requests = new LyricsCandidateRequests();
        foreach (var best in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await requests.TryAsync(async () =>
            {
                string? text;
                long generation;
                lock (_cacheGate)
                {
                    generation = _generation;
                    text = null;
                    if (!query.BypassProviderResponseCache && _cache.TryGetValue(best.Id, out var entry))
                    { text = entry.Text; _cache[best.Id] = entry with { Order = ++_order }; }
                }
                var downloaded = text is null;
                if (!downloaded) http.ReportReuse(Kind);
                if (downloaded)
                {
                    using var lyric = await http.GetAsync($"https://api.amll.dev/v1/lyrics/get?id={Escape(best.Id)}", cancellationToken);
                    var lyricData = Data(lyric.RootElement);
                    if (Text(lyricData, "id") != best.Id) throw new InvalidDataException("AMLL returned a different lyric ID.");
                    if (!lyricData.TryGetProperty("lyrics", out var field) || field.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("Unsupported AMLL lyric payload.");
                    text = field.GetString()!;
                    if (!text.TrimStart().StartsWith('<') || !text.Contains("<tt", StringComparison.OrdinalIgnoreCase) ||
                        lyricData.TryGetProperty("format", out var format) &&
                            (format.ValueKind != JsonValueKind.String || format.GetString() != "ttml"))
                        throw new InvalidDataException("AMLL returned unsupported lyric format.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = LyricsParser.Parse(text!, Kind);
                cancellationToken.ThrowIfCancellationRequested();
                if (downloaded && parsed.Lines.Count > 0) Store(best.Id, text!, generation);
                // SongItem does not expose audio duration. Neither player duration
                // nor the last sung timestamp may be presented as source evidence.
                return parsed.Bind(query, best.Title, best.Artist, best.Album, 0, best.Score, best.Id);
            });
            if (document.Lines.Count == 0) continue;
            LyricsRequestTrace.Record("parse", new { provider = "Amll", best.Id, document = LyricsRequestTrace.Describe(document) });
            reportCandidate(document);
            if (!query.CollectSelectionCandidates && (target.Length == 0 || !LyricsTranslationPolicy.NeedsProviderTranslation(document, target) ||
                LyricsTranslationPolicy.HasMatchingProviderTranslation(document, target))) return document;
            if (original.Lines.Count == 0) original = document;
        }
        requests.ThrowIfFailed();
        return original;
    }

    private static JsonElement Data(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code))
            throw new InvalidDataException("Unsupported AMLL response wrapper.");
        if (code != 200) throw new LyricsProviderRejectedException("AMLL lyrics API rejected the request.", code);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unsupported AMLL response data.");
        return data;
    }

    private static bool HasMore(JsonElement data)
    {
        if (!data.TryGetProperty("pagination", out var pagination)) return false;
        if (pagination.ValueKind != JsonValueKind.Object || !pagination.TryGetProperty("hasMore", out var more) ||
            more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Unsupported AMLL pagination.");
        return more.GetBoolean();
    }

    private static IEnumerable<Candidate> Candidates(JsonElement data, LyricsQuery query, CancellationToken token)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unsupported AMLL catalogue.");
        var matches = new List<Candidate>();
        foreach (var item in items.EnumerateArray().Take(100))
        {
            token.ThrowIfCancellationRequested();
            var titles = Array(item, "musicNames").Where(name => name.ValueKind == JsonValueKind.String)
                .Take(16).Select(name => name.GetString()!).ToArray();
            var artist = string.Join("; ", Array(item, "artistNames").Where(name => name.ValueKind == JsonValueKind.String)
                .Take(16).Select(name => name.GetString()));
            var album = string.Join("; ", Array(item, "albumNames").Where(name => name.ValueKind == JsonValueKind.String)
                .Take(16).Select(name => name.GetString()));
            // Unknown duration requires a complete title and corroborating artist.
            // Contributors (authorUsernames) are never artist aliases. A title
            // alias cannot erase explicit version evidence in another source name.
            if ((!query.CollectSelectionCandidates && (query.ArtistCandidates.Count == 0 || !query.ArtistCandidates.Any(value => LyricsMatcher.AreArtistCreditsCompatible(value, artist)))) ||
                titles.Any(title => LyricsMatcher.HasVersionConflict(query.Title, title))) continue;
            var best = titles.Where(title => LyricsMatcher.AreTitlesEquivalent(query.Title, title))
                .Select(title => new Candidate(Text(item, "id"), title, artist, album, LyricsMatcher.CandidateScore(query, title, artist, album, 0)))
                .OrderByDescending(candidate => candidate.Score).FirstOrDefault();
            if (best is not null && best.Score >= 4 && !string.IsNullOrWhiteSpace(best.Id)) matches.Add(best);
        }
        return matches.OrderByDescending(candidate => candidate.Score).DistinctBy(candidate => candidate.Id).Take(3);
    }

    private void Store(string id, string text, long generation)
    {
        var bytes = checked(text.Length * 2);
        if (bytes > 4 * 1024 * 1024) return;
        lock (_cacheGate)
        {
            if (generation != _generation) return;
            if (_cache.Remove(id, out var old)) _cacheBytes -= old.Bytes;
            while (_cache.Count >= 128 || _cacheBytes + bytes > 16 * 1024 * 1024)
            {
                var oldest = _cache.MinBy(pair => pair.Value.Order);
                _cache.Remove(oldest.Key);
                _cacheBytes -= oldest.Value.Bytes;
            }
            _cache[id] = new(text, bytes, ++_order);
            _cacheBytes += bytes;
        }
    }
}
