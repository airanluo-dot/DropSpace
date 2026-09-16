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
        try
        {
            using var exact = await http.GetAsync($"https://lrclib.net/api/get?track_name={Escape(query.Title)}&artist_name={Escape(query.Artist)}&album_name={Escape(query.Album)}&duration={(long)query.Duration.TotalSeconds}", cancellationToken);
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
                var matchTitle = string.IsNullOrWhiteSpace(exactTitle) ? query.Title : exactTitle;
                var matchArtist = string.IsNullOrWhiteSpace(exactArtist) ? query.Artist : exactArtist;
                var matchAlbum = string.IsNullOrWhiteSpace(exactAlbum) ? query.Album : exactAlbum;
                var score = LyricsMatcher.Score(query, matchTitle, matchArtist, matchAlbum, exactDuration);
                return parsed.Bind(query, matchTitle, matchArtist, matchAlbum, exactDuration, score, exactId);
            }
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { }
        using var search = await http.GetAsync($"https://lrclib.net/api/search?q={Escape(query.Title + " " + query.Artist)}", cancellationToken);
        var best = Array(search.RootElement).Select(item => new
        { Item = item, Score = LyricsMatcher.Score(query, Text(item, "trackName"), Text(item, "artistName"), Text(item, "albumName"), Number(item, "duration")) })
            .OrderByDescending(candidate => candidate.Score).FirstOrDefault();
        var bestId = best is null ? string.Empty : Text(best.Item, "id");
        if (best is null || best.Score < 4 || string.IsNullOrWhiteSpace(bestId)) return LyricsDocument.Empty;
        var text = Text(best.Item, "syncedLyrics");
        if (string.IsNullOrWhiteSpace(text)) text = Text(best.Item, "plainLyrics");
        return LyricsParser.Parse(text, Kind).Bind(query, Text(best.Item, "trackName"), Text(best.Item, "artistName"),
            Text(best.Item, "albumName"), Number(best.Item, "duration"), best.Score, bestId);
    }
}
