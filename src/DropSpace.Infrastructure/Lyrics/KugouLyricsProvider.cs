using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class KugouLyricsProvider(LyricsHttpClient http) : IProgressiveLyricsProvider
{
    internal const int DataRevision = 1;
    public LyricsProviderKind Kind => LyricsProviderKind.Kugou;
    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        => await QueryAsync(query, cancellationToken, _ => { }).ConfigureAwait(false);

    public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken,
        Action<LyricsDocument> reportCandidate)
    {
        var original = LyricsDocument.Empty;
        var target = LyricsTranslationPolicy.NormalizeLanguage(query.PreferredTranslationLanguage);
        bool Complete(LyricsDocument document) => !query.CollectSelectionCandidates && document.Lines.Count > 0 &&
            (target.Length == 0 || !LyricsTranslationPolicy.NeedsProviderTranslation(document, target) ||
             LyricsTranslationPolicy.HasMatchingProviderTranslation(document, target));
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var requests = new LyricsCandidateRequests();
        var remaining = 3;
        var remainingCatalogs = 3;
        var attemptedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task<LyricsDocument> TryCandidatesAsync(System.Text.Json.JsonElement root, CatalogRecording? recording = null)
        {
            foreach (var candidate in Choose(root, query, recording))
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
                        // Charge the fallback to the existing three-download budget. Business
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
                    LyricsRequestTrace.Record("parse", new { provider = "Kugou", candidate.Id, candidate.Title, candidate.Artist, candidate.DurationSeconds, document = LyricsRequestTrace.Describe(parsed) });
                    return parsed.Bind(query, candidate.Title, candidate.Artist, candidate.Album, candidate.DurationSeconds, candidate.Score, candidate.Id)
                        with { ProviderDataRevision = DataRevision };
                });
                if (document.Lines.Count == 0) continue;
                reportCandidate(document);
                if (Complete(document)) return document;
                if (original.Lines.Count == 0) original = document;
            }
            return LyricsDocument.Empty;
        }
        using var search = await http.GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Escape(query.Title)}&duration={(long)query.Duration.TotalMilliseconds}", cancellationToken);
        Validate(search.RootElement, "candidates", JsonValueKind.Array);
        var result = await TryCandidatesAsync(search.RootElement);
        if (Complete(result)) return result;
        foreach (var terms in LyricsMatcher.SearchTerms(query))
        {
            if (remaining == 0 || remainingCatalogs == 0) break;
            using var songs = await http.GetAsync($"https://songsearch.kugou.com/song_search_v2?keyword={Escape(terms)}&page=1&pagesize=20&platform=WebFilter&filter=2&iscorrection=1&privilege_filter=0", cancellationToken);
            Validate(songs.RootElement, "data", JsonValueKind.Object, catalog: true);
            ValidateField(songs.RootElement.GetProperty("data"), "lists", JsonValueKind.Array);
            var matches = Array(songs.RootElement, "data", "lists").Select(item => new
                { Item = item, Title = CatalogTitle(item), Score = LyricsMatcher.CandidateScore(query, CatalogTitle(item), Text(item, "SingerName"), Text(item, "AlbumName"), Number(item, "Duration")) })
                .Where(candidate => candidate.Score >= 4 && !string.IsNullOrWhiteSpace(Text(candidate.Item, "FileHash")))
                .OrderByDescending(candidate => candidate.Score);
            foreach (var song in matches)
            {
                if (remaining == 0 || remainingCatalogs == 0) break;
                var hash = Text(song.Item, "FileHash");
                if (!attemptedHashes.Add(hash)) continue;
                remainingCatalogs--;
                var recording = new CatalogRecording(song.Title, Text(song.Item, "SingerName"),
                    Text(song.Item, "AlbumName"), Number(song.Item, "Duration"));
                result = await requests.TryAsync(async () =>
                {
                    using var hashed = await http.GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&hash={Escape(hash)}", cancellationToken);
                    Validate(hashed.RootElement, "candidates", JsonValueKind.Array);
                    return await TryCandidatesAsync(hashed.RootElement, recording);
                });
                if (Complete(result)) return result;
            }
        }
        requests.ThrowIfFailed();
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

    private sealed record CatalogRecording(string Title, string Artist, string Album, double DurationSeconds);

    private sealed record Candidate(string Id, string Key, string Title, string Artist, string Album, double DurationSeconds, double Score);

    private static IEnumerable<Candidate> Choose(System.Text.Json.JsonElement root, LyricsQuery query, CatalogRecording? recording = null) =>
        Array(root, "candidates").Select(item => new
        {
            Item = item,
            Title = Text(item, "song"),
            Artist = Text(item, "singer"),
            Album = Text(item, "album"),
            DurationSeconds = Number(item, "duration") / 1000,
        }).Select(candidate =>
        {
            // Only a hash-bound, independently matched recording can supply missing
            // metadata. Never borrow duration from the requested player track, and
            // never overwrite explicit conflicting lyric metadata.
            var sameRecording = recording is not null &&
                LyricsMatcher.AreTitlesEquivalent(recording.Title, candidate.Title) &&
                LyricsMatcher.AreArtistCreditsCompatible(recording.Artist, candidate.Artist);
            var duration = candidate.DurationSeconds > 0 ? candidate.DurationSeconds :
                sameRecording ? recording!.DurationSeconds : 0;
            var album = string.IsNullOrWhiteSpace(candidate.Album) && sameRecording ? recording!.Album : candidate.Album;
            return new
            {
                candidate.Item, candidate.Title, candidate.Artist, Album = album, DurationSeconds = duration,
                Score = LyricsMatcher.CandidateScore(query, candidate.Title, candidate.Artist, album, duration),
            };
        })
        .Where(candidate => candidate.Score >= 4 && !string.IsNullOrWhiteSpace(Text(candidate.Item, "id")) &&
            !string.IsNullOrWhiteSpace(Text(candidate.Item, "accesskey")))
        .OrderByDescending(candidate => candidate.Score)
        .Select(candidate => new Candidate(Text(candidate.Item, "id"), Text(candidate.Item, "accesskey"), candidate.Title,
            candidate.Artist, candidate.Album, candidate.DurationSeconds, candidate.Score));
}
