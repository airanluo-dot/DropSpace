using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class NetEaseLyricsProvider(LyricsHttpClient http) : ILyricsProvider
{
    public LyricsProviderKind Kind => LyricsProviderKind.NetEase;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        using var search = await http.GetAsync($"https://music.163.com/api/search/get/web?s={Escape(LyricsMatcher.SearchTitle(query.Title) + " " + query.Artist)}&type=1&offset=0&total=true&limit=10", cancellationToken);
        var best = Array(search.RootElement, "result", "songs").Select(song => new
        {
            Song = song,
            Title = Text(song, "name"),
            Artist = string.Join("; ", Array(song, "artists").Select(artist => Text(artist, "name"))),
            Album = NestedText(song, "album", "name"),
            Duration = Number(song, "duration") / 1000,
        }).Select(candidate => new
        {
            candidate.Song, candidate.Title, candidate.Artist, candidate.Album, candidate.Duration,
            Score = LyricsMatcher.Score(query, candidate.Title, candidate.Artist, candidate.Album, candidate.Duration),
        }).OrderByDescending(candidate => candidate.Score).FirstOrDefault();
        var songId = best is null ? string.Empty : Text(best.Song, "id");
        if (best is null || best.Score < 4 || string.IsNullOrWhiteSpace(songId)) return LyricsDocument.Empty;
        using var lyric = await http.GetAsync($"https://music.163.com/api/song/lyric?id={Escape(songId)}&lv=1&kv=1&tv=-1&yv=1&ytv=1", cancellationToken);
        var root = lyric.RootElement;
        var primary = NestedText(root, "yrc", "lyric");
        if (string.IsNullOrEmpty(primary)) primary = NestedText(root, "lrc", "lyric");
        var translation = NestedText(root, "ytlrc", "lyric");
        if (string.IsNullOrEmpty(translation)) translation = NestedText(root, "tlyric", "lyric");
        return LyricsParser.Parse(primary, Kind, translation).Bind(query, best.Title, best.Artist, best.Album, best.Duration, best.Score, songId);
    }
}
