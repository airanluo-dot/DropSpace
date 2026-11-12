using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class KugouLyricsProvider(LyricsHttpClient http, ILyricsLanguageIdentifier? languageIdentifier = null) : IProgressiveLyricsProvider
{
    internal const int DataRevision = 2;
    public LyricsProviderKind Kind => LyricsProviderKind.Kugou;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, cancellationToken, _ => { }).ConfigureAwait(false);

    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken,
        Action<LyricsDocument> reportCandidate)
    {
        var original = LyricsDocument.Empty;
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var requests = new LyricsCandidateRequests();
        var remaining = LyricsCandidateRequests.MaximumCandidates;
        var remainingCatalogs = 3;
        var attemptedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task TryCandidatesAsync(JsonElement root, LyricsMatchInfo? recording = null)
        {
            foreach (var candidate in LyricsCandidateRequests.OrderCatalogue(Choose(root, query, recording), query, value => value.Match))
            {
                if (remaining == 0) break;
                if (!attempted.Add(candidate.Id)) continue;
                remaining--;
                var document = await requests.TryAsync(async () =>
                {
                    var url = $"https://lyrics.kugou.com/download?ver=1&client=pc&id={Escape(candidate.Id)}&accesskey={Escape(candidate.Key)}";
                    LyricsDocument parsed;
                    try
                    {
                        using var lyric = await http.GetAsync(url + "&fmt=krc&charset=utf8", cancellationToken);
                        Validate(lyric.RootElement, "content", JsonValueKind.String);
                        parsed = KugouKrcParser.Parse(Text(lyric.RootElement, "content"), target, cancellationToken);
                    }
                    catch (Exception error) when (error is InvalidDataException or FormatException or DecoderFallbackException && remaining > 0)
                    {
                        // Charge the fallback to the shared six-download budget. Business
                        // rejection, cancellation and HTTP failures never trigger this retry.
                        remaining--;
                        using var lyric = await http.GetAsync(url + "&fmt=lrc&charset=utf8", cancellationToken);
                        Validate(lyric.RootElement, "content", JsonValueKind.String);
                        var content = Text(lyric.RootElement, "content");
                        if (content.Length > 2 * 1024 * 1024) throw new InvalidDataException("Kugou lyric content exceeds limit.");
                        var bytes = Convert.FromBase64String(content);
                        cancellationToken.ThrowIfCancellationRequested();
                        parsed = LyricsParser.Parse(Encoding.UTF8.GetString(bytes), Kind);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    var match = candidate.Match;
                    LyricsRequestTrace.Record("parse", new { provider = "Kugou", candidate.Id, match.Title, match.Artist, match.DurationSeconds, match.ArtistAliases, document = LyricsRequestTrace.Describe(parsed) });
                    var bound = parsed.Bind(query, match.Title, match.Artist, match.Album, match.DurationSeconds, match.Score, candidate.Id);
                    return bound with { ProviderDataRevision = DataRevision, Match = bound.Match! with { ArtistAliases = match.ArtistAliases } };
                });
                if (document.Lines.Count == 0) continue;
                if (languageIdentifier is not null && target.Length > 0)
                    document = await languageIdentifier.PrepareAsync(document, target, cancellationToken).ConfigureAwait(false);
                reportCandidate(document);
                original = LyricsCandidateRules.Best([
                    LyricsCandidateRules.Describe("current", original, target),
                    LyricsCandidateRules.Describe("candidate", document, target),
                ], query, Kind, null);
            }
        }
        using var search = await http.GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Escape(query.Title)}&duration={(long)query.Duration.TotalMilliseconds}", cancellationToken);
        Validate(search.RootElement, "candidates", JsonValueKind.Array);
        await TryCandidatesAsync(search.RootElement);
        if (!LyricsCandidateRequests.NeedsMoreSearch(query, original, target))
        {
            return original;
        }
        foreach (var terms in LyricsMatcher.SearchTerms(query))
        {
            if (remaining == 0 || remainingCatalogs == 0) break;
            using var songs = await http.GetAsync($"https://songsearch.kugou.com/song_search_v2?keyword={Escape(terms)}&page=1&pagesize=20&platform=WebFilter&filter=2&iscorrection=1&privilege_filter=0", cancellationToken);
            Validate(songs.RootElement, "data", JsonValueKind.Object, catalog: true);
            ValidateField(songs.RootElement.GetProperty("data"), "lists", JsonValueKind.Array);
            var matches = Array(songs.RootElement, "data", "lists")
                .Select(item => (Hash: Text(item, "FileHash"), Match: CatalogMatch(item, query)))
                .Where(song => song.Hash.Length > 0 && song.Match.Score >= 4);
            foreach (var song in LyricsCandidateRequests.OrderCatalogue(matches, query, value => value.Match))
            {
                if (remaining == 0 || remainingCatalogs == 0) break;
                if (!attemptedHashes.Add(song.Hash)) continue;
                remainingCatalogs--;
                await requests.TryAsync(async () =>
                {
                    using var hashed = await http.GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&hash={Escape(song.Hash)}", cancellationToken);
                    Validate(hashed.RootElement, "candidates", JsonValueKind.Array);
                    await TryCandidatesAsync(hashed.RootElement, song.Match);
                    return LyricsDocument.Empty;
                });
            }
            if (!LyricsCandidateRequests.NeedsMoreSearch(query, original, target)) break;
        }
        if (LyricsCandidateRequests.NeedsMoreSearch(query, original, target)) requests.ThrowIfFailed();
        return original;
    }

    internal static string CatalogTitle(JsonElement song)
    {
        var title = Text(song, "SongName").Trim();
        var suffix = Text(song, "Suffix").Trim();
        // Kugou sometimes moves recording/version evidence out of SongName, e.g.
        // Taylor's Version. Use the catalogue's own suffix, never the requested title.
        return title.Length == 0 || suffix.Length == 0 ||
            LyricsMatcher.Normalize(title).EndsWith(LyricsMatcher.Normalize(suffix), StringComparison.Ordinal)
            ? title : title + " " + suffix;
    }

    private static void Validate(JsonElement root, string field, JsonValueKind shape, bool catalog = false)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unsupported Kugou response shape.");
        foreach (var property in new[] { "error_code", "errcode", "status" })
        {
            if (!root.TryGetProperty(property, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var code))
                throw new InvalidDataException("Unsupported Kugou response status.");
            var success = KugouResponseStatus.IsSuccess(property, code, catalog);
            if (!success) throw new LyricsProviderRejectedException("Kugou lyrics API rejected the request.", code);
        }
        ValidateField(root, field, shape);
    }

    private static void ValidateField(JsonElement root, string field, JsonValueKind shape)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind != shape)
            throw new InvalidDataException("Unsupported Kugou response payload.");
    }

    private static LyricsMatchInfo CatalogMatch(JsonElement song, LyricsQuery query)
    {
        var title = CatalogTitle(song);
        var artist = Text(song, "SingerName");
        var album = Text(song, "AlbumName");
        var duration = Number(song, "Duration");
        // A localized search can match a singer's full localized name ("hl")
        // even though SingerName and the hash-bound lyrics carry the Latin credit.
        var singers = Array(song, "Singers").Select(singer =>
            new[] { Text(singer, "name"), Text(singer, "hl") }).ToArray();
        var canonicalCredit = string.Join("; ", singers.Select(names => names[0]));
        var aliases = singers.Any(names => names[1].Length > 0) && singers.All(names => names[0].Length > 0) &&
            LyricsMatcher.AreArtistCreditsCompatible(artist, canonicalCredit)
            ? LyricsMatcher.ProjectArtistCredits(query, singers) : [];
        return new(title, artist, album, duration, LyricsMatcher.CandidateScore(query, title, artist, album, duration, aliases))
            { ArtistAliases = aliases };
    }

    private sealed record Candidate(string Id, string Key, LyricsMatchInfo Match);

    private static IEnumerable<Candidate> Choose(JsonElement root, LyricsQuery query, LyricsMatchInfo? recording)
    {
        foreach (var item in Array(root, "candidates"))
        {
            var title = Text(item, "song");
            var artist = Text(item, "singer");
            var album = Text(item, "album");
            var duration = Number(item, "duration") / 1000;
            // Only a hash-bound, independently matched recording can supply missing
            // metadata. Never borrow duration from the requested player track, and
            // never overwrite explicit conflicting lyric metadata.
            var sameRecording = recording is not null &&
                LyricsMatcher.AreTitlesEquivalent(recording.Title, title) &&
                LyricsMatcher.AreArtistCreditsCompatible(recording.Artist, artist);
            if (sameRecording)
            {
                if (duration <= 0) duration = recording!.DurationSeconds;
                if (string.IsNullOrWhiteSpace(album)) album = recording!.Album;
            }
            var aliases = sameRecording ? recording!.ArtistAliases : [];
            var score = LyricsMatcher.CandidateScore(query, title, artist, album, duration, aliases);
            var id = Text(item, "id");
            var key = Text(item, "accesskey");
            if (score >= 4 && !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(key))
                yield return new(id, key, new(title, artist, album, duration, score) { ArtistAliases = aliases });
        }
    }
}
