using System.Net;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class QqMusicLyricsProvider(LyricsHttpClient http) : IProgressiveLyricsProvider
{
    public LyricsProviderKind Kind => LyricsProviderKind.QqMusic;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, cancellationToken, _ => { }).ConfigureAwait(false);

    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken,
        Action<LyricsDocument> reportCandidate)
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var remaining = 3;
        var requests = new LyricsCandidateRequests();
        var original = LyricsDocument.Empty;
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);
        foreach (var terms in LyricsMatcher.SearchTerms(query))
        {
            if (remaining == 0) break;
            using var search = await http.GetAsync($"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=20&w={Escape(terms)}", cancellationToken, "https://y.qq.com/");
            ThrowIfRejected(search.RootElement);
            ValidateSearchShape(search.RootElement);
            var candidates = Array(search.RootElement, "data", "song", "list").Select(song => new
            {
                Song = song,
                Title = Text(song, "songname"),
                Artist = string.Join("; ", Array(song, "singer").Select(artist => Text(artist, "name"))),
                Album = Text(song, "albumname"),
                Duration = Number(song, "interval"),
            }).Select(candidate => new
            {
                candidate.Song, candidate.Title, candidate.Artist, candidate.Album, candidate.Duration,
                Score = LyricsMatcher.Score(query, candidate.Title, candidate.Artist, candidate.Album, candidate.Duration),
            }).Where(candidate => candidate.Score >= 4).OrderByDescending(candidate => candidate.Score);
            foreach (var best in candidates)
            {
                var songId = Text(best.Song, "songmid");
                if (string.IsNullOrWhiteSpace(songId) || !attempted.Add(songId)) continue;
                if (remaining == 0) break;
                remaining--;
                var document = await requests.TryAsync(() => ReadLyricsAsync(songId, cancellationToken));
                if (document.Lines.Count == 0) continue;
                document = document.Bind(query, best.Title, best.Artist, best.Album, best.Duration, best.Score, songId);
                reportCandidate(document);
                if (target.Length == 0 || !LyricsTranslationPolicy.NeedsProviderTranslation(document, target) ||
                    LyricsTranslationPolicy.HasMatchingProviderTranslation(document, target)) return document;
                if (original.Lines.Count == 0) original = document;
            }
        }
        requests.ThrowIfFailed();
        return original;
    }

    private async Task<LyricsDocument> ReadLyricsAsync(string songId, CancellationToken token)
    {
        using var lyric = await http.GetAsync($"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Escape(songId)}&format=json&nobase64=1&g_tk=5381", token, "https://y.qq.com/");
        ThrowIfRejected(lyric.RootElement);
        if (!lyric.RootElement.TryGetProperty("lyric", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("QQ lyrics returned an unsupported lyric shape.");
        token.ThrowIfCancellationRequested();
        var document = LyricsParser.Parse(WebUtility.HtmlDecode(Text(lyric.RootElement, "lyric")), Kind,
            WebUtility.HtmlDecode(Text(lyric.RootElement, "trans")));
        token.ThrowIfCancellationRequested();
        return document;
    }

    private static void ValidateSearchShape(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("song", out var song) || song.ValueKind != JsonValueKind.Object ||
            !song.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("QQ search returned an unsupported result shape.");
    }

    private static void ThrowIfRejected(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("QQ returned an unsupported response shape.");
        foreach (var property in new[] { "code", "retcode", "subcode" })
        {
            if (!root.TryGetProperty(property, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var code))
                throw new InvalidDataException("QQ returned an unsupported status code.");
            if (code != 0) throw new LyricsProviderRejectedException("QQ lyrics API rejected the request.", code);
        }
    }
}
