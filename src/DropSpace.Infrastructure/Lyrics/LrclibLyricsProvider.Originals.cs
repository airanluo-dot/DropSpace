using System.Text.Json;
using DropSpace.Core.Lyrics;
using static DropSpace.Infrastructure.Lyrics.LyricsHttpClient;

namespace DropSpace.Infrastructure.Lyrics;

public sealed partial class LrclibLyricsProvider
{
    // LRCLIB can attach different sung texts to a translated title on the same release.
    // Use the catalogue's own bilingual titles to read the other title on that release.
    // The actual text must agree across two distinct IDs. A title's script alone says
    // nothing about the language of a recording.
    private async Task<LyricsDocument?> CheckAlternateOriginalAsync(LyricsQuery query, JsonElement rows, CancellationToken token)
    {
        if (query.ArtistCandidates.Count == 0 || string.IsNullOrWhiteSpace(query.Album) || query.Duration <= TimeSpan.Zero)
            return null;
        var entries = new List<(JsonElement Row, LyricsMatchInfo Match, string? OtherTitle)>();
        foreach (var row in Array(rows))
        {
            var artist = Text(row, "artistName");
            var album = Text(row, "albumName");
            var seconds = Number(row, "duration");
            if (seconds <= 0 || Math.Abs(seconds - query.Duration.TotalSeconds) > 3) continue;
            var (artistName, artistAlias) = NamePair(artist);
            var confirmedArtist = query.ArtistCandidates.FirstOrDefault(requested =>
                LyricsMatcher.AreArtistCreditsCompatible(requested, artist) ||
                DifferentScripts(artistName, artistAlias) && LyricsMatcher.AreArtistCreditsCompatible(requested, artistAlias));
            if (confirmedArtist is null) continue;
            if (!LyricsCandidateRules.SameReleaseTitle(query.Album, album) &&
                !string.Equals(album, confirmedArtist + " - " + query.Album, StringComparison.OrdinalIgnoreCase)) continue;
            var title = Text(row, "trackName");
            var (name, alternate) = NamePair(title);
            var sameName = LyricsMatcher.AreTitlesEquivalent(query.Title, name);
            var sameAlternate = alternate.Length > 0 && LyricsMatcher.AreTitlesEquivalent(query.Title, alternate);
            if (!sameName && !sameAlternate || LyricsMatcher.HasVersionConflict(query.Title, title) ||
                LyricsMatcher.HasVersionConflict(title, query.Title)) continue;
            var id = Text(row, "id");
            if (string.IsNullOrEmpty(id)) continue;
            var match = new LyricsMatchInfo(title, artist, album, seconds, 0, id, query.TrackIdentity)
            {
                TitleAliases = alternate.Length > 0 ? [name, alternate] : [],
                ArtistAliases = artist == confirmedArtist ? [] : [confirmedArtist],
            };
            var score = LyricsMatcher.Score(query, match);
            // A bilingual witness can name the other title even when its audio length
            // exceeds the stricter bound for selecting that row as the final document.
            var other = sameAlternate ? name : alternate;
            entries.Add((row, match with { Score = score }, DifferentScripts(query.Title, other) ? other : null));
        }
        var alternateNames = entries.Select(entry => entry.OtherTitle).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        if (alternateNames.Length == 0 || entries.Count < 2) return null;
        var options = entries.Select(entry => RowBody(entry.Row) with { Match = entry.Match })
            .Where(document => document.Lines.Count > 0)
            .Select(document => (Document: document, Fragments: BodyFragments(document))).ToArray();
        var distinctBodies = options.Select(option => option.Fragments).Where(body => body.Count >= 24).ToArray();
        if (distinctBodies.Length < 2 || distinctBodies.Skip(1).All(body => TextAgrees(distinctBodies[0], body))) return null;
        // Several unrelated alternate titles, or conflicting texts with no corroboration,
        // cannot authorize publication from this catalogue.
        if (alternateNames.Length > 1) return new LyricsDocument([], Kind) { BodyQuality = LyricsBodyQuality.Ambiguous };
        var referenceQuery = query with { Title = alternateNames[0] };
        using var reference = await http.GetAsync($"https://lrclib.net/api/search?track_name={Escape(referenceQuery.Title)}" +
            $"&artist_name={Escape(query.ArtistCandidates[0])}&album_name={Escape(query.Album)}", token).ConfigureAwait(false);
        if (reference.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Unsupported LRCLIB alias response.");
        var references = Array(reference.RootElement).Where(row =>
            LyricsMatcher.AreTitlesEquivalent(referenceQuery.Title, Text(row, "trackName")) &&
            LyricsMatcher.AreArtistCreditsCompatible(query.ArtistCandidates[0], Text(row, "artistName")) &&
            LyricsCandidateRules.SameReleaseTitle(query.Album, Text(row, "albumName")) &&
            Number(row, "duration") > 0 && Math.Abs(query.Duration.TotalSeconds - Number(row, "duration")) <= 3)
            .Select(row => (Id: Text(row, "id"), Body: BodyFragments(RowBody(row))))
            .Where(row => row.Id.Length > 0 && row.Body.Count >= 24).ToArray();
        var confirmed = options.Where(option => option.Document.Match!.Score >= 4 &&
            references.Any(reference => reference.Id != option.Document.Match.CandidateId &&
                TextAgrees(reference.Body, option.Fragments))).ToArray();
        if (confirmed.Length == 0 || confirmed.Skip(1).Any(option =>
            !TextAgrees(confirmed[0].Fragments, option.Fragments)))
            return new LyricsDocument([], Kind) { BodyQuality = LyricsBodyQuality.Ambiguous };
        return LyricsCandidateRules.Best(confirmed.Select(option => LyricsCandidateRules.Describe(
            option.Document.Match!.CandidateId!, option.Document, query.PreferredTranslationLanguage ?? "")), query, Kind, null);
    }

    private LyricsDocument RowBody(JsonElement row)
    {
        var text = Text(row, "syncedLyrics");
        return ParseBody(row, string.IsNullOrWhiteSpace(text) ? Text(row, "plainLyrics") : text);
    }

    private static (string Name, string Alternate) NamePair(string text)
    {
        var open = text.LastIndexOf('(');
        return open > 0 && text.EndsWith(')') ? (text[..open].Trim(), text[(open + 1)..^1].Trim()) : (text, "");
    }

    private static bool DifferentScripts(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(second)) return false;
        static bool Latin(string text) => text.Any(char.IsLetter) && text.Where(char.IsLetter).All(c => c <= '\u024f');
        static bool Other(string text) => text.Count(c => char.IsLetter(c) && c > '\u024f') >= 2;
        return Latin(first) && Other(second) || Latin(second) && Other(first);
    }

    // Character windows tolerate line splitting, punctuation and timestamp changes.
    private static HashSet<string> BodyFragments(LyricsDocument document)
    {
        var text = string.Concat(document.Lines.Where(line => !LyricsLanguagePolicy.IsCredit(line.Text))
            .SelectMany(line => line.Text).Where(char.IsLetter).Take(4096)).ToLowerInvariant();
        var parts = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 8 <= text.Length; i++) parts.Add(text.Substring(i, 8));
        return parts;
    }

    private static bool TextAgrees(HashSet<string> left, HashSet<string> right) =>
        Math.Min(left.Count, right.Count) >= 24 && left.Count(right.Contains) >= Math.Max(left.Count, right.Count) * 0.7;
}
