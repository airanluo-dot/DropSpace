using System.Net;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LrclibLyricsProvider(LyricsHttpClient http) : IProgressiveLyricsProvider
{
    public LyricsProviderKind Kind => LyricsProviderKind.Lrclib;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, cancellationToken, _ => { }).ConfigureAwait(false);

    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken,
        Action<LyricsDocument> reportCandidate)
    {
        var original = LyricsDocument.Empty;
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var remaining = 3;
        var requests = new LyricsCandidateRequests();
        if (query.Duration > TimeSpan.Zero && query.ArtistCandidates.Count > 0 && !string.IsNullOrWhiteSpace(query.Album))
        {
            foreach (var artist in LyricsMatcher.SearchArtists(query).Take(2))
            {
                try
                {
                    var exactDocument = await requests.TryAsync(async () =>
                    {
                        try
                        {
                            using var exact = await http.GetAsync($"https://lrclib.net/api/get?track_name={Escape(query.Title)}&artist_name={Escape(artist)}&album_name={Escape(query.Album)}&duration={(long)query.Duration.TotalSeconds}", cancellationToken);
                            if (exact.RootElement.ValueKind != JsonValueKind.Object ||
                                string.IsNullOrWhiteSpace(Text(exact.RootElement, "id")) ||
                                !exact.RootElement.TryGetProperty("trackName", out var titleField) || titleField.ValueKind != JsonValueKind.String)
                                throw new InvalidDataException("Unsupported LRCLIB exact response.");
                            var exactTitle = Text(exact.RootElement, "trackName");
                            var exactArtist = Text(exact.RootElement, "artistName");
                            var exactAlbum = Text(exact.RootElement, "albumName");
                            var exactDuration = Number(exact.RootElement, "duration");
                            var exactText = Text(exact.RootElement, "syncedLyrics");
                            if (string.IsNullOrWhiteSpace(exactText)) exactText = Text(exact.RootElement, "plainLyrics");
                            cancellationToken.ThrowIfCancellationRequested();
                            var parsed = ParseBody(exact.RootElement, exactText);
                            cancellationToken.ThrowIfCancellationRequested();
                            var exactId = Text(exact.RootElement, "id");
                            if (!string.IsNullOrWhiteSpace(exactId))
                            {
                                var score = LyricsMatcher.CandidateScore(query, exactTitle, exactArtist, exactAlbum, exactDuration);
                                if (score >= 4) return parsed.Bind(query, exactTitle, exactArtist, exactAlbum, exactDuration, score, exactId);
                            }
                            return LyricsDocument.Empty;
                        }
                        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                        { return LyricsDocument.Empty; }
                    });
                    if (exactDocument.Match is not null && exactDocument.Lines.Count == 0)
                    {
                        LyricsRequestTrace.Record("provider-body", LyricsRequestTrace.Describe(exactDocument));
                        if (exactDocument.BodyQuality == LyricsBodyQuality.ConfirmedInstrumental)
                        {
                            // Preserve already verified evidence even if later
                            // catalogue requests fail or exhaust their budget.
                            reportCandidate(exactDocument);
                            if (original.Lines.Count == 0) original = exactDocument;
                        }
                    }
                    if (exactDocument.Lines.Count > 0 && attempted.Add(exactDocument.Match!.CandidateId!))
                    {
                        reportCandidate(exactDocument);
                        remaining--;
                        if (!query.CollectSelectionCandidates) return exactDocument;
                        if (original.Lines.Count == 0) original = exactDocument;
                        if (remaining == 0) return original;
                    }
                }
                catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { }
            }
        }
        foreach (var terms in LyricsMatcher.SearchTerms(query))
        {
            if (remaining == 0) break;
            using var search = await http.GetAsync($"https://lrclib.net/api/search?q={Escape(terms)}", cancellationToken);
            if (search.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Unsupported LRCLIB search response.");
            LyricsRequestTrace.Record("search-result", new { provider = "Lrclib", terms, count = search.RootElement.GetArrayLength() });
            var candidates = Array(search.RootElement)
                .Where(item => !string.IsNullOrWhiteSpace(Text(item, "syncedLyrics")) || !string.IsNullOrWhiteSpace(Text(item, "plainLyrics")) ||
                    item.TryGetProperty("instrumental", out var flag) && flag.ValueKind == JsonValueKind.True)
                .Select(item => new
                { Item = item, Score = LyricsMatcher.CandidateScore(query, Text(item, "trackName"), Text(item, "artistName"), Text(item, "albumName"), Number(item, "duration")) })
                .Where(candidate => candidate.Score >= 4 && !string.IsNullOrWhiteSpace(Text(candidate.Item, "id")))
                .OrderByDescending(candidate => candidate.Score).Take(3);
            foreach (var best in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bestId = Text(best.Item, "id");
                if (remaining == 0) break;
                if (!attempted.Add(bestId)) continue;
                remaining--;
                var parsed = await requests.TryAsync(() =>
                {
                    var text = Text(best.Item, "syncedLyrics");
                    if (string.IsNullOrWhiteSpace(text)) text = Text(best.Item, "plainLyrics");
                    var body = ParseBody(best.Item, text);
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(body);
                });
                var document = parsed.Bind(query, Text(best.Item, "trackName"), Text(best.Item, "artistName"),
                    Text(best.Item, "albumName"), Number(best.Item, "duration"), best.Score, bestId);
                LyricsRequestTrace.Record("provider-body", LyricsRequestTrace.Describe(document));
                if (parsed.Lines.Count > 0)
                {
                    reportCandidate(document);
                    if (!query.CollectSelectionCandidates) return document;
                    if (original.Lines.Count == 0) original = document;
                }
                else if (document.BodyQuality == LyricsBodyQuality.ConfirmedInstrumental)
                {
                    reportCandidate(document);
                    if (original.Lines.Count == 0) original = document;
                }
            }
        }
        requests.ThrowIfFailed();
        return original;
    }

    private LyricsDocument ParseBody(JsonElement response, string text)
    {
        var parsed = LyricsParser.Parse(text, Kind);
        // Only the explicit provider flag is evidence; a placeholder sentence is not.
        return parsed.Lines.Count == 0 && response.TryGetProperty("instrumental", out var flag) && flag.ValueKind == JsonValueKind.True
            ? parsed with { BodyQuality = LyricsBodyQuality.ConfirmedInstrumental } : parsed;
    }
}
