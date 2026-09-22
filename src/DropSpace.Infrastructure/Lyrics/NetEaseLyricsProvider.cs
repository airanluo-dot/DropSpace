using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class NetEaseLyricsProvider(LyricsHttpClient http) : ILyricsProvider
{
    private const int MaximumLyricCandidates = 3;
    public LyricsProviderKind Kind => LyricsProviderKind.NetEase;

    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        var title = LyricsMatcher.SearchTitle(query.Title);
        var searches = new[] { string.Join(' ', new[] { title, query.Artist }.Where(value => !string.IsNullOrWhiteSpace(value))), title }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var remaining = MaximumLyricCandidates;

        foreach (var terms in searches)
        {
            using var search = await http.GetAsync(
                $"https://music.163.com/api/search/get/web?s={Escape(terms)}&type=1&offset=0&total=true&limit=30",
                cancellationToken);
            ThrowIfRejected(search.RootElement);
            foreach (var candidate in Candidates(search.RootElement, query)
                .Where(value => value.Score >= 4 && attempted.Add(value.Id))
                .OrderByDescending(value => value.Score))
            {
                if (remaining-- <= 0) return LyricsDocument.Empty;
                var document = await ReadLyricsAsync(candidate, query, cancellationToken);
                if (document.Lines.Count > 0) return document;
            }
        }
        return LyricsDocument.Empty;
    }

    private async Task<LyricsDocument> ReadLyricsAsync(Candidate candidate, LyricsQuery query, CancellationToken token)
    {
        using var lyric = await http.GetAsync(
            $"https://music.163.com/api/song/lyric?id={Escape(candidate.Id)}&lv=1&kv=1&tv=-1&yv=1&ytv=1",
            token);
        var root = lyric.RootElement;
        ThrowIfRejected(root);
        var yrc = NestedText(root, "yrc", "lyric");
        var lrc = NestedText(root, "lrc", "lyric");
        var yrcTranslation = NestedText(root, "ytlrc", "lyric");
        var lrcTranslation = NestedText(root, "tlyric", "lyric");
        var document = LyricsParser.Parse(string.IsNullOrWhiteSpace(yrc) ? lrc : yrc, Kind,
            string.IsNullOrWhiteSpace(yrcTranslation) ? lrcTranslation : yrcTranslation);
        // Some catalogue rows expose YRC while its payload is temporarily empty or
        // malformed. The independently returned LRC is still a valid representation.
        if (document.Lines.Count == 0 && !string.IsNullOrWhiteSpace(yrc) && !string.IsNullOrWhiteSpace(lrc))
            document = LyricsParser.Parse(lrc, Kind, lrcTranslation);
        return document.Bind(query, candidate.Title, candidate.Artist, candidate.Album,
            candidate.Duration, candidate.Score, candidate.Id);
    }

    private static IEnumerable<Candidate> Candidates(JsonElement root, LyricsQuery query)
    {
        var songs = Array(root, "result", "songs");
        if (!songs.Any()) songs = Array(root, "songs");
        foreach (var song in songs)
        {
            var id = Text(song, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var artistItems = Array(song, "artists");
            if (!artistItems.Any()) artistItems = Array(song, "ar");
            var artist = string.Join("; ", artistItems.Select(value => Text(value, "name")).Where(value => !string.IsNullOrWhiteSpace(value)));
            var album = NestedText(song, "album", "name");
            if (string.IsNullOrWhiteSpace(album)) album = NestedText(song, "al", "name");
            var duration = Number(song, "duration");
            if (duration <= 0) duration = Number(song, "dt");
            duration /= 1000;

            var titles = new List<string>();
            AddTitle(titles, Text(song, "name"));
            foreach (var property in new[] { "alias", "alia", "transNames", "tns" })
                foreach (var alias in StringArray(song, property)) AddTitle(titles, alias);
            var best = titles.Select(value => new { Title = value, Score = LyricsMatcher.Score(query, value, artist, album, duration) })
                .OrderByDescending(value => value.Score).FirstOrDefault();
            if (best is not null) yield return new(id, best.Title, artist, album, duration, best.Score);
        }
    }

    private static IEnumerable<string> StringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var values) ||
            values.ValueKind != JsonValueKind.Array) yield break;
        foreach (var value in values.EnumerateArray().Take(16))
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) yield return value.GetString()!;
    }

    private static void AddTitle(List<string> titles, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !titles.Contains(value, StringComparer.OrdinalIgnoreCase)) titles.Add(value);
    }

    private static void ThrowIfRejected(JsonElement root)
    {
        var code = Number(root, "code");
        if (code > 0 && code != 200) throw new HttpRequestException("NetEase lyrics API rejected the request.");
    }

    private sealed record Candidate(string Id, string Title, string Artist, string Album, double Duration, double Score);
}
