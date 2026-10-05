using System.Net;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class QqMusicLyricsProvider(LyricsHttpClient http) : IProgressiveLyricsProvider
{
    public const int DataRevision = 2;
    public LyricsProviderKind Kind => LyricsProviderKind.QqMusic;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, cancellationToken, _ => { }).ConfigureAwait(false);

    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken,
        Action<LyricsDocument> reportCandidate)
    {
        if (http.QqSession is { } session)
        {
            await session.LoadAsync(cancellationToken).ConfigureAwait(false);
            session.EnsureUsable();
        }
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var remaining = 3;
        var requests = new LyricsCandidateRequests();
        var original = LyricsDocument.Empty;
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);
        foreach (var terms in LyricsMatcher.SearchTerms(query))
        {
            if (remaining == 0) break;
            // The website's current SearchCgi contract replaces client_search_cp,
            // which now returns HTTP 500 on otherwise valid searches.
            LyricsRequestTrace.Record("search", new { provider = "QqMusic", terms });
            var identity = http.QqSession?.Identity() ?? (Uin: "0", Gtk: 5381);
            var sessionGeneration = http.QqSession?.Generation ?? 0;
            var payload = JsonSerializer.Serialize(new
            {
                comm = new { ct = 24, cv = 4747474, format = "json", inCharset = "utf-8", outCharset = "utf-8",
                    notice = 0, platform = "yqq.json", needNewCode = 1, uin = identity.Uin,
                    g_tk = identity.Gtk, g_tk_new_20200303 = identity.Gtk },
                req_1 = new { module = "music.search.SearchCgiService", method = "DoSearchForQQMusicDesktop",
                    param = new { query = terms, search_type = 0, num_per_page = 20, page_num = 1 } },
            });
            using var search = await http.PostAsync("https://u.y.qq.com/cgi-bin/musicu.fcg", payload, cancellationToken, "https://y.qq.com/");
            JsonElement songs;
            try
            {
                ThrowIfRejected(search.RootElement);
                songs = SearchSongs(search.RootElement);
                if (songs.GetArrayLength() > 0) http.QqSession?.ReportAccess(sessionGeneration, true);
            }
            catch (LyricsProviderRejectedException error)
            {
                http.QqSession?.ReportAccess(sessionGeneration, false, error.ApiCode);
                throw;
            }
            LyricsRequestTrace.Record("search-result", new { provider = "QqMusic", count = songs.GetArrayLength() });
            var candidates = songs.EnumerateArray().Take(100).Select(song => new
            {
                Song = song,
                Title = Field(song, "name", "songname"),
                Artist = string.Join("; ", Array(song, "singer").Select(artist => Text(artist, "name"))),
                Album = string.IsNullOrWhiteSpace(NestedText(song, "album", "name")) ? Text(song, "albumname") : NestedText(song, "album", "name"),
                Duration = Number(song, "interval"),
            }).Select(candidate => new
            {
                candidate.Song, candidate.Title, candidate.Artist, candidate.Album, candidate.Duration,
                Score = LyricsMatcher.CandidateScore(query, candidate.Title, candidate.Artist, candidate.Album, candidate.Duration),
            }).Where(candidate => candidate.Score >= 4).OrderByDescending(candidate => candidate.Score);
            foreach (var best in candidates)
            {
                var songId = Field(best.Song, "mid", "songmid");
                if (string.IsNullOrWhiteSpace(songId) || !attempted.Add(songId)) continue;
                if (remaining == 0) break;
                remaining--;
                var document = await requests.TryAsync(() => ReadLyricsAsync(songId, cancellationToken));
                if (document.Lines.Count == 0) continue;
                document = document.Bind(query, best.Title, best.Artist, best.Album, best.Duration, best.Score, songId);
                LyricsRequestTrace.Record("parse", new { provider = "QqMusic", id = songId, document = LyricsRequestTrace.Describe(document) });
                reportCandidate(document);
                if (!query.CollectSelectionCandidates && (target.Length == 0 || !LyricsTranslationPolicy.NeedsProviderTranslation(document, target) ||
                    LyricsTranslationPolicy.HasMatchingProviderTranslation(document, target))) return document;
                if (original.Lines.Count == 0) original = document;
            }
        }
        requests.ThrowIfFailed();
        return original;
    }

    private async Task<LyricsDocument> ReadLyricsAsync(string songId, CancellationToken token)
    {
        var identity = http.QqSession?.Identity() ?? (Uin: "0", Gtk: 5381);
        var generation = http.QqSession?.Generation ?? 0;
        var payload = JsonSerializer.Serialize(new
        {
            comm = new { ct = 24, cv = 4747474, format = "json", inCharset = "utf-8", outCharset = "utf-8",
                notice = 0, platform = "yqq.json", needNewCode = 1, uin = identity.Uin,
                g_tk = identity.Gtk, g_tk_new_20200303 = identity.Gtk },
            req_1 = new { module = "music.musichallSong.PlayLyricInfo", method = "GetPlayLyricInfo",
                param = new { songMID = songId, trans = 1 } },
        });
        using var lyric = await http.PostAsync("https://u.y.qq.com/cgi-bin/musicu.fcg", payload, token,
            "https://y.qq.com/", LyricsDiagnosticStage.Lyric);
        try { return ParseLyrics(lyric.RootElement, token); }
        catch (LyricsProviderRejectedException error)
        {
            if (error.ApiCode is 1000 or 2001 or 101010 or 401)
                http.QqSession?.ReportAccess(generation, false, error.ApiCode);
            throw;
        }
    }

    private LyricsDocument ParseLyrics(JsonElement root, CancellationToken token)
    {
        ThrowIfRejected(root);
        var encoded = root.TryGetProperty("req_1", out var reply);
        var body = root;
        if (encoded)
        {
            ThrowIfRejected(reply);
            if (!reply.TryGetProperty("data", out body) || body.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("QQ lyrics returned unsupported data.");
        }
        if (!body.TryGetProperty("lyric", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("QQ lyrics returned an unsupported lyric shape.");
        token.ThrowIfCancellationRequested();
        string Decode(string value)
        {
            if (encoded && value.Length > 0)
            {
                try { value = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value)); }
                catch (Exception error) when (error is FormatException or DecoderFallbackException)
                { throw new InvalidDataException("QQ lyrics returned invalid encoded text.", error); }
            }
            return WebUtility.HtmlDecode(value);
        }
        var original = Decode(Text(body, "lyric"));
        var translated = Decode(Text(body, "trans"));
        LyricsRequestTrace.Record("lyric-fields", new { provider = "QqMusic", originalPresent = original.Length > 0,
            translationPresent = translated.Length > 0, encoded });
        var document = LyricsParser.Parse(original, Kind, translated) with { ProviderDataRevision = DataRevision };
        // QQ's own renderer treats // as a missing translation, including credit rows.
        document = document with { Lines = document.Lines.Select(line => line.Secondary?.Trim() == "//"
            ? line with { Secondary = null, TranslationOrigin = LyricsTranslationOrigin.None,
                TranslationLanguage = null, TranslationLanguageIsExplicit = null }
            : line).ToArray() };
        token.ThrowIfCancellationRequested();
        return document;
    }

    internal static JsonElement SearchSongs(JsonElement root)
    {
        // Accept the legacy data/song shape as well as the current batched wrapper.
        // Both still pass independent status, array and recording validation.
        var modern = root.TryGetProperty("req_1", out var reply);
        if (!modern) reply = root;
        if (reply.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("QQ search returned an unsupported request wrapper.");
        ThrowIfRejected(reply);
        if (!reply.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("QQ search returned unsupported data.");
        if (data.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            var filtered = Number(meta, "is_filter");
            var code = Number(meta, "ret");
            LyricsRequestTrace.Record("search-status", new { provider = "QqMusic", code, filtered });
            // A success envelope can still suppress every result. This is a rejected
            // request, not proof that the recording or its translation does not exist.
            if (filtered < 0 || code != 0)
                throw new LyricsProviderRejectedException("QQ search result access was rejected.", (int)(code != 0 ? code : filtered));
        }
        var body = data;
        if (modern && (!data.TryGetProperty("body", out body) || body.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("QQ search returned an unsupported body.");
        if (
            !body.TryGetProperty("song", out var song) || song.ValueKind != JsonValueKind.Object ||
            !song.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("QQ search returned an unsupported result shape.");
        return list;
    }

    private static string Field(JsonElement item, string primary, string legacy) =>
        string.IsNullOrWhiteSpace(Text(item, primary)) ? Text(item, legacy) : Text(item, primary);

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
