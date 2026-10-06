using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class QqMusicLyricsProvider(LyricsHttpClient http) : IProgressiveLyricsProvider
{
    public const int DataRevision = 3;
    private static readonly Regex VocalCredit = new(
        @"^(?:演唱(?:\s*Artist)?|歌手|Artist|Vocals?|Singer)\s*[:：]\s*(.{1,128})$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
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
                NeedsVocalProof = CanReadMissingFeaturedCredit(query, candidate.Title, candidate.Artist, candidate.Album, candidate.Duration),
            }).ToArray();
            foreach (var candidate in candidates)
                LyricsRequestTrace.Record("candidate", new { provider = "QqMusic", id = Field(candidate.Song, "mid", "songmid"),
                    candidate.Title, candidate.Artist, candidate.Album, candidate.Duration, candidate.Score,
                    reason = candidate.Score >= 4 ? "recording-matched" : candidate.NeedsVocalProof
                        ? "missing-featured-credit-requires-lyric-proof" : "canonical-recording-not-confirmed" });
            foreach (var best in candidates.Where(candidate => candidate.Score >= 4 || candidate.NeedsVocalProof)
                .OrderByDescending(candidate => candidate.Score))
            {
                var songId = Field(best.Song, "mid", "songmid");
                if (string.IsNullOrWhiteSpace(songId) || !attempted.Add(songId)) continue;
                if (remaining == 0) break;
                remaining--;
                string[] vocalCredits = [];
                var document = await requests.TryAsync(() => ReadLyricsAsync(songId, cancellationToken, value => vocalCredits = value));
                if (document.Lines.Count == 0) continue;
                // The catalogue sometimes names the production group alone. A
                // same-ID response's explicit early vocal credit can complete it,
                // but this read is never permission to publish an unverified body.
                var completeArtist = vocalCredits.Length == 0 ? best.Artist : best.Artist + "; " + string.Join("; ", vocalCredits);
                var confirmedScore = vocalCredits.Length == 0 ? best.Score :
                    LyricsMatcher.Score(query, best.Title, completeArtist, best.Album, best.Duration);
                if (confirmedScore < 4 || best.Score < 4 && vocalCredits.Length == 0)
                {
                    LyricsRequestTrace.Record("candidate-rejected", new { provider = "QqMusic", id = songId,
                        reason = "featured-performer-not-confirmed", vocalCredits });
                    continue;
                }
                document = document.Bind(query, best.Title, best.Artist, best.Album, best.Duration, confirmedScore, songId);
                if (vocalCredits.Length > 0)
                    document = document with { Match = document.Match! with { ArtistAliases = [completeArtist] } };
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

    private static bool CanReadMissingFeaturedCredit(LyricsQuery query, string title, string artist, string album, double duration)
    {
        if (!LyricsMatcher.HasFeaturedArtistCredit(query.Title) || query.Duration <= TimeSpan.Zero ||
            !double.IsFinite(duration) || duration <= 0 || Math.Abs(query.Duration.TotalSeconds - duration) > 3) return false;
        // Remove only the featured-credit decoration for this bounded probe.
        // Language, live/remix labels, base artist and duration remain mandatory.
        var probe = query with { Title = LyricsMatcher.TitleWithoutFeaturedArtists(query.Title), CollectSelectionCandidates = false };
        return LyricsMatcher.Score(probe, title, artist, album, duration) >= 4;
    }

    private async Task<LyricsDocument> ReadLyricsAsync(string songId, CancellationToken token, Action<string[]> reportVocalCredits)
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
        try
        {
            var document = ParseLyrics(lyric.RootElement, token);
            var credits = document.Lines.Take(24).Where(line => line.Start <= TimeSpan.FromSeconds(10))
                .Select(line => VocalCredit.Match(line.Text.Trim())).Where(match => match.Success)
                .Select(match => match.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
            reportVocalCredits(credits);
            return document;
        }
        catch (LyricsProviderRejectedException error)
        {
            if (error.ApiCode is 1000 or 2001 or 101010 or 401 or 429)
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
