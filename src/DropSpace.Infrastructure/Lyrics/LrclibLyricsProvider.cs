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
            var parsed = LyricsParser.Parse(Text(exact.RootElement, "syncedLyrics"), Kind);
            if (parsed.Lines.Count > 0) return parsed;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { }
        using var search = await http.GetAsync($"https://lrclib.net/api/search?q={Escape(query.Title + " " + query.Artist)}", cancellationToken);
        var best = Array(search.RootElement).Select(item => new
        { Item = item, Score = LyricsMatcher.Score(query, Text(item, "trackName"), Text(item, "artistName"), Text(item, "albumName"), Number(item, "duration")) })
            .OrderByDescending(candidate => candidate.Score).FirstOrDefault();
        return best is null || best.Score < 4 ? LyricsDocument.Empty : LyricsParser.Parse(Text(best.Item, "syncedLyrics"), Kind);
    }
}
