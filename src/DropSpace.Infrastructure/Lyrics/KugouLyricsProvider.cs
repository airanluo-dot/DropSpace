using System.Text;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class KugouLyricsProvider(LyricsHttpClient http) : ILyricsProvider
{
    public LyricsProviderKind Kind => LyricsProviderKind.Kugou;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        using var search = await http.GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Escape(query.Title)}&duration={(long)query.Duration.TotalMilliseconds}", cancellationToken);
        var best = Choose(search.RootElement, query);
        if (best is null)
        {
            using var songs = await http.GetAsync($"https://songsearch.kugou.com/song_search_v2?keyword={Escape(query.Title + " " + query.Artist)}&page=1&pagesize=20&platform=WebFilter&filter=2&iscorrection=1&privilege_filter=0", cancellationToken);
            var song = Array(songs.RootElement, "data", "lists").Select(item => new
            { Item = item, Score = LyricsMatcher.Score(query, Text(item, "SongName"), Text(item, "SingerName"), Text(item, "AlbumName"), Number(item, "Duration")) })
                .OrderByDescending(candidate => candidate.Score).FirstOrDefault();
            if (song is null || song.Score < 4) return LyricsDocument.Empty;
            using var hashed = await http.GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&hash={Escape(Text(song.Item, "FileHash"))}", cancellationToken);
            best = Choose(hashed.RootElement, query);
        }
        if (best is null) return LyricsDocument.Empty;
        using var lyric = await http.GetAsync($"https://lyrics.kugou.com/download?ver=1&client=pc&id={Escape(best.Value.Id)}&accesskey={Escape(best.Value.Key)}&fmt=lrc&charset=utf8", cancellationToken);
        var bytes = Convert.FromBase64String(Text(lyric.RootElement, "content"));
        return LyricsParser.Parse(Encoding.UTF8.GetString(bytes), Kind);
    }

    private static (string Id, string Key)? Choose(System.Text.Json.JsonElement root, LyricsQuery query)
    {
        var best = Array(root, "candidates").Select(item => new
        { Item = item, Score = LyricsMatcher.Score(query, Text(item, "song"), Text(item, "singer"), string.Empty, Number(item, "duration") / 1000) })
            .OrderByDescending(candidate => candidate.Score).FirstOrDefault();
        return best is null || best.Score < 4 ? null : (Text(best.Item, "id"), Text(best.Item, "accesskey"));
    }
}
