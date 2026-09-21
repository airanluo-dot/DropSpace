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
            if (song is null || song.Score < 4 || string.IsNullOrWhiteSpace(Text(song.Item, "FileHash"))) return LyricsDocument.Empty;
            using var hashed = await http.GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&hash={Escape(Text(song.Item, "FileHash"))}", cancellationToken);
            best = Choose(hashed.RootElement, query, Text(song.Item, "AlbumName"));
        }
        if (best is null) return LyricsDocument.Empty;
        using var lyric = await http.GetAsync($"https://lyrics.kugou.com/download?ver=1&client=pc&id={Escape(best.Id)}&accesskey={Escape(best.Key)}&fmt=lrc&charset=utf8", cancellationToken);
        var bytes = Convert.FromBase64String(Text(lyric.RootElement, "content"));
        return LyricsParser.Parse(Encoding.UTF8.GetString(bytes), Kind)
            .Bind(query, best.Title, best.Artist, best.Album, best.DurationSeconds, best.Score, best.Id);
    }

    private sealed record Candidate(string Id, string Key, string Title, string Artist, string Album, double DurationSeconds, double Score);

    private static Candidate? Choose(System.Text.Json.JsonElement root, LyricsQuery query, string verifiedAlbum = "")
    {
        var best = Array(root, "candidates").Select(item => new
        {
            Item = item,
            Title = Text(item, "song"),
            Artist = Text(item, "singer"),
            Album = string.IsNullOrWhiteSpace(Text(item, "album")) ? verifiedAlbum : Text(item, "album"),
            DurationSeconds = Number(item, "duration") / 1000,
        })
            .Select(candidate => new
            {
                candidate.Item,
                candidate.Title,
                candidate.Artist,
                candidate.Album,
                candidate.DurationSeconds,
                Score = LyricsMatcher.Score(query, candidate.Title, candidate.Artist, candidate.Album, candidate.DurationSeconds),
            })
            .OrderByDescending(candidate => candidate.Score).FirstOrDefault();
        return best is null || best.Score < 4 || string.IsNullOrWhiteSpace(Text(best.Item, "id")) ||
            string.IsNullOrWhiteSpace(Text(best.Item, "accesskey")) ? null : new(Text(best.Item, "id"), Text(best.Item, "accesskey"), best.Title, best.Artist,
            best.Album, best.DurationSeconds, best.Score);
    }
}
