using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class NetEaseLyricsProvider(LyricsHttpClient http, TimeProvider? timeProvider = null) : IProgressiveLyricsProvider, ILyricsResponseCache
{
    private readonly NetEaseResponseCache _responses = new(http, timeProvider);
    private const int MaximumLyricCandidates = 3;
    internal const int DataRevision = 3;
    public LyricsProviderKind Kind => LyricsProviderKind.NetEase;
    public void ClearResponseCache() => _responses.Clear();

    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, cancellationToken, _ => { }).ConfigureAwait(false);

    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken,
        Action<LyricsDocument> reportCandidate)
    {
        var searches = LyricsMatcher.SearchTerms(query);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var remaining = MaximumLyricCandidates;
        var requests = new LyricsCandidateRequests();
        var original = LyricsDocument.Empty;
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);

        foreach (var terms in searches)
        {
            if (remaining == 0) break;
            LyricsRequestTrace.Record("search", new { provider = "NetEase", terms, remaining });
            using var search = await _responses.GetAsync(
                $"https://music.163.com/api/search/get/web?s={Escape(terms)}&type=1&offset=0&total=true&limit=30",
                cancellationToken, query.BypassProviderResponseCache, query.PreferredTranslationLanguage);
            ThrowIfRejected(search.RootElement);
            foreach (var candidate in Candidates(search.RootElement, query, cancellationToken)
                .Where(value => value.Score >= 4 && attempted.Add(value.Id))
                .OrderByDescending(value => value.Score))
            {
                if (remaining == 0) break;
                remaining--;
                LyricsRequestTrace.Record("candidate-request", new { provider = "NetEase", candidate.Id, candidate.Title, candidate.Artist, candidate.Album, candidate.Duration, candidate.Score });
                var document = await requests.TryAsync(() => ReadLyricsAsync(candidate, query, cancellationToken));
                if (document.Lines.Count == 0) continue;
                reportCandidate(document);
                if (!query.CollectSelectionCandidates && (target.Length == 0 || !LyricsTranslationPolicy.NeedsProviderTranslation(document, target) || LyricsTranslationPolicy.HasMatchingProviderTranslation(document, target))) return document;
                if (original.Lines.Count == 0) original = document;
            }
        }
        requests.ThrowIfFailed();
        return original;
    }

    private async Task<LyricsDocument> ReadLyricsAsync(Candidate candidate, LyricsQuery query, CancellationToken token)
    {
        using var lyric = await _responses.GetAsync(
            $"https://music.163.com/api/song/lyric?id={Escape(candidate.Id)}&lv=1&kv=1&tv=-1&yv=1&ytv=1",
            token, query.BypassProviderResponseCache, query.PreferredTranslationLanguage);
        var root = lyric.RootElement;
        ThrowIfRejected(root);
        token.ThrowIfCancellationRequested();
        var document = ParseLyrics(root).Bind(query, candidate.Title, candidate.Artist, candidate.Album,
            candidate.Duration, candidate.Score, candidate.Id);
        LyricsRequestTrace.Record("parse", new { provider = "NetEase", candidate.Id,
            fields = new[] { "lrc", "tlyric", "romalrc", "yrc", "ytlrc", "yromalrc" }.Select(field => new {
                field, present = root.TryGetProperty(field, out _),
                characters = NestedText(root, field, "lyric").Length,
                rows = NestedText(root, field, "lyric").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length }),
            document = LyricsRequestTrace.Describe(document) });
        token.ThrowIfCancellationRequested();
        return document with { Match = document.Match! with { ArtistAliases = candidate.ArtistAliases,
            CanonicalTitle = candidate.Title, TitleAliases = candidate.TitleAliases } };
    }

    internal static LyricsDocument ParseLyrics(JsonElement root)
    {
        var yrc = NestedText(root, "yrc", "lyric");
        var lrc = NestedText(root, "lrc", "lyric");
        var yrcTranslation = NestedText(root, "ytlrc", "lyric");
        var lrcTranslation = NestedText(root, "tlyric", "lyric");
        // Keep each provider-authored original/translation pair intact. A single
        // accidentally matching timestamp must not make a mixed YRC/tlyric pair
        // appear usable while silently dropping the rest of the translation.
        var document = string.IsNullOrWhiteSpace(yrc)
            ? LyricsParser.Parse(lrc, LyricsProviderKind.NetEase, lrcTranslation)
            : LyricsParser.Parse(yrc, LyricsProviderKind.NetEase, yrcTranslation);
        // Credit-only translations do not cover the sung lyrics. Genuine partial
        // YRC translations retain priority; otherwise try the paired LRC document.
        if (!string.IsNullOrWhiteSpace(yrc) && !string.IsNullOrWhiteSpace(lrc) &&
            (document.Lines.Count == 0 || !HasProviderTranslation(document)))
        {
            var pairedLrc = LyricsParser.Parse(lrc, LyricsProviderKind.NetEase, lrcTranslation);
            if (document.Lines.Count == 0 || HasProviderTranslation(pairedLrc))
                document = pairedLrc;
        }
        return document with { ProviderDataRevision = DataRevision };
    }

    internal static bool HasProviderTranslation(LyricsDocument document) => document.Lines.Any(line =>
        line.TranslationOrigin == LyricsTranslationOrigin.Provider && !LyricsLanguagePolicy.IsCredit(line.Text) &&
        !string.IsNullOrWhiteSpace(line.Secondary));

    private static IEnumerable<Candidate> Candidates(JsonElement root, LyricsQuery query, CancellationToken token)
    {
        // A successful HTTP/API status is not sufficient: some regional responses
        // contain an opaque string instead of the searchable catalog object.
        // Surface a provider failure so it is not mistaken for a genuine no-match.
        if (root.TryGetProperty("result", out var result) &&
            result.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new InvalidDataException("NetEase search returned an unsupported result shape.");
        var songs = Array(root, "result", "songs");
        if (!songs.Any()) songs = Array(root, "songs");
        LyricsRequestTrace.Record("search-result", new { provider = "NetEase", count = songs.Count() });
        foreach (var song in songs)
        {
            token.ThrowIfCancellationRequested();
            var id = Text(song, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var artistItems = Array(song, "artists");
            if (!artistItems.Any()) artistItems = Array(song, "ar");
            var artist = string.Join("; ", artistItems.Select(value => Text(value, "name")).Where(value => !string.IsNullOrWhiteSpace(value)));
            var artistAliases = ArtistAliases(artistItems.ToArray(), query, token);
            var album = NestedText(song, "album", "name");
            if (string.IsNullOrWhiteSpace(album)) album = NestedText(song, "al", "name");
            var duration = Number(song, "duration");
            if (duration <= 0) duration = Number(song, "dt");
            duration /= 1000;

            // An alias can translate a title but cannot erase canonical Live/Remix evidence.
            if (LyricsMatcher.HasVersionConflict(query.Title, Text(song, "name")))
            {
                LyricsRequestTrace.Record("candidate", new { provider = "NetEase", id, title = Text(song, "name"), artist, album, duration, reason = "version-conflict" });
                continue;
            }
            var titles = new List<string>();
            AddTitle(titles, Text(song, "name"));
            foreach (var property in new[] { "alias", "alia", "transNames", "tns" })
                foreach (var alias in StringArray(song, property)) AddTitle(titles, alias);
            // Retain alias evidence, but never replace the recording's canonical title.
            // A translated alias alone cannot distinguish a re-recorded language version.
            var canonical = Text(song, "name");
            var score = LyricsMatcher.CandidateScore(query, canonical, artist, album, duration, artistAliases);
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                LyricsRequestTrace.Record("candidate", new { provider = "NetEase", id, title = canonical, aliases = titles,
                    artist, artistAliases, album, duration, score,
                    reason = score >= 4 ? "eligible" : "canonical-recording-not-confirmed" });
                yield return new(id, canonical, artist, album, duration, score, artistAliases, titles.Where(t => t != canonical).ToArray());
            }
        }
    }

    private static string[] ArtistAliases(JsonElement[] artists, LyricsQuery query, CancellationToken token)
    {
        // Each alternative remains attached to the same provider artist object.
        // Project complete collaboration credits; never treat any member's alias
        // as an alias for the entire collaboration or guess a Latin/Han suffix.
        return query.ArtistCandidates.Take(4).Select(requested => string.Join("; ", artists.Select(item =>
        {
            token.ThrowIfCancellationRequested();
            var canonical = Text(item, "name");
            var names = new[] { canonical }.Concat(new[] { "alias", "alia", "transNames", "tns" }
                .SelectMany(property => StringArray(item, property))).Take(65);
            return names.FirstOrDefault(name => LyricsMatcher.AreArtistCreditsCompatible(requested, name)) ?? canonical;
        }))).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
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
        if (code > 0 && code != 200) throw new LyricsProviderRejectedException("NetEase lyrics API rejected the request.", (int)code);
    }

    private sealed record Candidate(string Id, string Title, string Artist, string Album, double Duration, double Score,
        string[] ArtistAliases, string[] TitleAliases);
}
