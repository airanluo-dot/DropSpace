using System.Net;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LrclibLyricsProvider(LyricsHttpClient http) : ILyricsProvider
{
    public LyricsProviderKind Kind => LyricsProviderKind.Lrclib;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        var requests = new LyricsCandidateRequests();
        if (query.Duration > TimeSpan.Zero && query.ArtistCandidates.Count > 0 && !string.IsNullOrWhiteSpace(query.Album))
        {
            foreach (var artist in query.ArtistCandidates.Take(2))
            {
                try
                {
                    var exactDocument = await requests.TryAsync(async () =>
                    {
                        try
                        {
                            using var exact = await http.GetAsync($"https://lrclib.net/api/get?track_name={Escape(query.Title)}&artist_name={Escape(artist)}&album_name={Escape(query.Album)}&duration={(long)query.Duration.TotalSeconds}", cancellationToken);
                            var exactTitle = Text(exact.RootElement, "trackName");
                            var exactArtist = Text(exact.RootElement, "artistName");
                            var exactAlbum = Text(exact.RootElement, "albumName");
                            var exactDuration = Number(exact.RootElement, "duration");
                            var exactText = Text(exact.RootElement, "syncedLyrics");
                            if (string.IsNullOrWhiteSpace(exactText)) exactText = Text(exact.RootElement, "plainLyrics");
                            var parsed = LyricsParser.Parse(exactText, Kind);
                            var exactId = Text(exact.RootElement, "id");
                            if (parsed.Lines.Count > 0 && !string.IsNullOrWhiteSpace(exactId))
                            {
                                var score = LyricsMatcher.Score(query, exactTitle, exactArtist, exactAlbum, exactDuration);
                                if (score >= 4) return parsed.Bind(query, exactTitle, exactArtist, exactAlbum, exactDuration, score, exactId);
                            }
                            return LyricsDocument.Empty;
                        }
                        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                        { return LyricsDocument.Empty; }
                    });
                    if (exactDocument.Lines.Count > 0) return exactDocument;
                }
                catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { }
            }
        }
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var remaining = 3;
        foreach (var terms in LyricsMatcher.SearchTerms(query))
        {
            if (remaining == 0) break;
            using var search = await http.GetAsync($"https://lrclib.net/api/search?q={Escape(terms)}", cancellationToken);
            var candidates = Array(search.RootElement)
                .Where(item => !string.IsNullOrWhiteSpace(Text(item, "syncedLyrics")) || !string.IsNullOrWhiteSpace(Text(item, "plainLyrics")))
                .Select(item => new
                { Item = item, Score = LyricsMatcher.Score(query, Text(item, "trackName"), Text(item, "artistName"), Text(item, "albumName"), Number(item, "duration")) })
                .Where(candidate => candidate.Score >= 4 && !string.IsNullOrWhiteSpace(Text(candidate.Item, "id")))
                .OrderByDescending(candidate => candidate.Score).Take(3);
            foreach (var best in candidates)
            {
                var bestId = Text(best.Item, "id");
                if (remaining == 0) break;
                if (!attempted.Add(bestId)) continue;
                remaining--;
                var parsed = await requests.TryAsync(() =>
                {
                    var text = Text(best.Item, "syncedLyrics");
                    if (string.IsNullOrWhiteSpace(text)) text = Text(best.Item, "plainLyrics");
                    return Task.FromResult(LyricsParser.Parse(text, Kind));
                });
                if (parsed.Lines.Count > 0)
                    return parsed.Bind(query, Text(best.Item, "trackName"), Text(best.Item, "artistName"),
                        Text(best.Item, "albumName"), Number(best.Item, "duration"), best.Score, bestId);
            }
        }
        requests.ThrowIfFailed();
        return LyricsDocument.Empty;
    }
}
