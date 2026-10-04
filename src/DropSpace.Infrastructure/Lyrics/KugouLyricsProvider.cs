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
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var requests = new LyricsCandidateRequests();
        var remaining = 3;
        async Task<LyricsDocument> TryCandidatesAsync(System.Text.Json.JsonElement root, string album = "")
        {
            foreach (var candidate in Choose(root, query, album))
            {
                if (remaining == 0) break;
                if (!attempted.Add(candidate.Id)) continue;
                remaining--;
                var document = await requests.TryAsync(async () =>
                {
                    using var lyric = await http.GetAsync($"https://lyrics.kugou.com/download?ver=1&client=pc&id={Escape(candidate.Id)}&accesskey={Escape(candidate.Key)}&fmt=lrc&charset=utf8", cancellationToken);
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(Text(lyric.RootElement, "content")); }
                    catch (FormatException error) { throw new InvalidDataException("Invalid Kugou lyric encoding.", error); }
                    return LyricsParser.Parse(Encoding.UTF8.GetString(bytes), Kind)
                        .Bind(query, candidate.Title, candidate.Artist, candidate.Album, candidate.DurationSeconds, candidate.Score, candidate.Id);
                });
                if (document.Lines.Count > 0) return document;
            }
            return LyricsDocument.Empty;
        }
        using var search = await http.GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Escape(query.Title)}&duration={(long)query.Duration.TotalMilliseconds}", cancellationToken);
        var result = await TryCandidatesAsync(search.RootElement);
        if (result.Lines.Count > 0) return result;
        foreach (var terms in LyricsMatcher.SearchTerms(query))
        {
            if (remaining == 0) break;
            using var songs = await http.GetAsync($"https://songsearch.kugou.com/song_search_v2?keyword={Escape(terms)}&page=1&pagesize=20&platform=WebFilter&filter=2&iscorrection=1&privilege_filter=0", cancellationToken);
            var song = Array(songs.RootElement, "data", "lists").Select(item => new
                { Item = item, Score = LyricsMatcher.Score(query, Text(item, "SongName"), Text(item, "SingerName"), Text(item, "AlbumName"), Number(item, "Duration")) })
                .Where(candidate => candidate.Score >= 4 && !string.IsNullOrWhiteSpace(Text(candidate.Item, "FileHash")))
                .OrderByDescending(candidate => candidate.Score).FirstOrDefault();
            if (song is null) continue;
            using var hashed = await http.GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&hash={Escape(Text(song.Item, "FileHash"))}", cancellationToken);
            result = await TryCandidatesAsync(hashed.RootElement, Text(song.Item, "AlbumName"));
            if (result.Lines.Count > 0) return result;
        }
        requests.ThrowIfFailed();
        return LyricsDocument.Empty;
    }

    private sealed record Candidate(string Id, string Key, string Title, string Artist, string Album, double DurationSeconds, double Score);

    private static IEnumerable<Candidate> Choose(System.Text.Json.JsonElement root, LyricsQuery query, string verifiedAlbum = "") =>
        Array(root, "candidates").Select(item => new
        {
            Item = item,
            Title = Text(item, "song"),
            Artist = Text(item, "singer"),
            Album = string.IsNullOrWhiteSpace(Text(item, "album")) ? verifiedAlbum : Text(item, "album"),
            DurationSeconds = Number(item, "duration") / 1000,
        }).Select(candidate => new
        {
            candidate.Item, candidate.Title, candidate.Artist, candidate.Album, candidate.DurationSeconds,
            Score = LyricsMatcher.Score(query, candidate.Title, candidate.Artist, candidate.Album, candidate.DurationSeconds),
        })
        .Where(candidate => candidate.Score >= 4 && !string.IsNullOrWhiteSpace(Text(candidate.Item, "id")) &&
            !string.IsNullOrWhiteSpace(Text(candidate.Item, "accesskey")))
        .OrderByDescending(candidate => candidate.Score)
        .Select(candidate => new Candidate(Text(candidate.Item, "id"), Text(candidate.Item, "accesskey"), candidate.Title,
            candidate.Artist, candidate.Album, candidate.DurationSeconds, candidate.Score));
}
