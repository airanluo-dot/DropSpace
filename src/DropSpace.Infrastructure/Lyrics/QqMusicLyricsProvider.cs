using System.Net;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class QqMusicLyricsProvider(LyricsHttpClient http) : ILyricsProvider
{
    public LyricsProviderKind Kind => LyricsProviderKind.QqMusic;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        using var search = await http.GetAsync($"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=20&w={Escape(query.Title + " " + query.Artist)}", cancellationToken, "https://y.qq.com/");
        var best = Array(search.RootElement, "data", "song", "list").Select(song => new
        {
            Song = song,
            Score = LyricsMatcher.Score(query, Text(song, "songname"), string.Join(' ', Array(song, "singer").Select(artist => Text(artist, "name"))), Text(song, "albumname"), Number(song, "interval")),
        }).OrderByDescending(candidate => candidate.Score).FirstOrDefault();
        if (best is null || best.Score < 4) return LyricsDocument.Empty;
        using var lyric = await http.GetAsync($"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Escape(Text(best.Song, "songmid"))}&format=json&nobase64=1&g_tk=5381", cancellationToken, "https://y.qq.com/");
        return LyricsParser.Parse(WebUtility.HtmlDecode(Text(lyric.RootElement, "lyric")), Kind, WebUtility.HtmlDecode(Text(lyric.RootElement, "trans")));
    }
}
