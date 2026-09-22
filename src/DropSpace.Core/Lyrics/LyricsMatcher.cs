using System.Text;
using System.Text.RegularExpressions;

namespace DropSpace.Core.Lyrics;

public static class LyricsMatcher
{
    private const int MaximumMetadataCharacters = 2_048;
    // These Unicode regex tokens recognize publisher suffixes, not UI strings.
    private static readonly Regex PlayerSuffix = new(@"\s*[-|–]\s*(?:Apple Music|QQ\u97f3\u4e50|\u7f51\u6613\u4e91\u97f3\u4e50|\u9177\u72d7\u97f3\u4e50|Spotify|YouTube)\s*$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex VersionLabels = new(@"(?:\(|\[|（|【)([^\)\]）】]*)(?:\)|\]|）|】)|\b(live|remix|acoustic|instrumental|karaoke|radio|extended|edit|demo|version|mono|stereo|original|concert|cover|sped\s*up|slowed)\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex FeaturedArtistDecoration = new(
        @"(?:\s*(?:\(|\[|（|【)\s*(?:feat(?:uring)?|ft|with)\.?\s+[^\)\]）】]+(?:\)|\]|）|】)\s*|\s*[-–—:]\s*(?:feat(?:uring)?|ft|with)\.?\s+.+|\s+(?:feat(?:uring)?|ft)\.?\s+.+)$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ReleaseDecoration = new(
        @"(?:\s*(?:\(|\[|（|【)\s*(?:(?:\d{4}\s*)?remaster(?:ed)?(?:\s*\d{4})?|explicit|clean|album\s+version|single\s+version|original\s+motion\s+picture\s+soundtrack)(?:\)|\]|）|】)\s*|\s*[-–—:]\s*(?:(?:\d{4}\s*)?remaster(?:ed)?(?:\s*\d{4})?|explicit|clean|album\s+version|single\s+version|original\s+motion\s+picture\s+soundtrack))$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ArtistCreditSeparator = new(
        @"\s*(?:;|；|,|，|、|&|＆)\s*|\s+[/／]\s+|(?<=[^\u0000-\u007F])[/／]|[/／](?=[^\u0000-\u007F])|\s+(?:feat(?:uring)?|ft|with|x)\.?\s+",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly string[] DisambiguatingVersionTokens =
    [
        "live", "remix", "acoustic", "instrumental", "karaoke", "radio", "extended",
        "edit", "demo", "concert", "cover", "spedup", "slowed",
    ];
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var input = Limit(value).Normalize(NormalizationForm.FormKC);
        return string.Concat(PlayerSuffix.Replace(input, string.Empty).EnumerateRunes()
            .Where(Rune.IsLetterOrDigit).Select(rune => Rune.ToLowerInvariant(rune).ToString()));
    }

    public static string SearchTitle(string value)
    {
        var title = PlayerSuffix.Replace(Limit(value), string.Empty);
        title = FeaturedArtistDecoration.Replace(title, string.Empty);
        title = ReleaseDecoration.Replace(title, string.Empty);
        return title.Trim();
    }

    public static bool AreTitlesEquivalent(string left, string right) =>
        !HasVersionConflict(left, right) && !HasVersionConflict(right, left) &&
        ComparableTitle(left) is { Length: > 0 } title && title == ComparableTitle(right);

    public static bool AreArtistCreditsCompatible(string left, string right) => ArtistSimilarity(left, right) >= 0.6;

    public static double Score(LyricsQuery query, string title, string artist, string album, double durationSeconds)
    {
        var titleScore = TitleSimilarity(query.Title, title);
        // Some media publishers reverse title and artist fields.
        if (titleScore < 0.4 && TitleSimilarity(query.Title, artist) > 0.8 && ArtistSimilarity(query.Artist, title) > 0.8)
        { (title, artist) = (artist, title); titleScore = TitleSimilarity(query.Title, title); }
        if (titleScore < 0.45) return 0;
        if (!query.HasDisambiguatingMetadata) return 0;
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album) &&
            (!double.IsFinite(durationSeconds) || durationSeconds <= 0)) return 0;
        if (HasVersionConflict(query.Title, title)) return 0;
        var artistScore = ArtistSimilarity(query.Artist, artist);
        var albumScore = Similarity(query.Album, album);
        var candidateDuration = double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0;
        var durationDelta = Math.Abs(query.Duration.TotalSeconds - candidateDuration);
        var durationKnown = candidateDuration > 0 && query.Duration.TotalSeconds > 0;
        var durationMatches = durationKnown && durationDelta <= DurationTolerance(query.Duration.TotalSeconds, candidateDuration);
        var artistMatches = !string.IsNullOrWhiteSpace(query.Artist) && !string.IsNullOrWhiteSpace(artist) && artistScore >= 0.6;
        var albumMatches = !string.IsNullOrWhiteSpace(query.Album) && !string.IsNullOrWhiteSpace(album) && albumScore >= 0.6;

        // Provider catalogues often omit an album, use a compilation/deluxe album, or
        // round duration differently. Keep title mandatory, but let independent artist,
        // album and duration evidence corroborate one another instead of requiring every
        // optional field to be present and identical.
        if (!string.IsNullOrWhiteSpace(query.Artist))
        {
            if (!string.IsNullOrWhiteSpace(artist) && !artistMatches) return 0;
            if (string.IsNullOrWhiteSpace(artist) && !albumMatches && !durationMatches) return 0;
        }
        if (!string.IsNullOrWhiteSpace(query.Album) && !artistMatches)
        {
            if (!string.IsNullOrWhiteSpace(album) && !albumMatches) return 0;
            if (string.IsNullOrWhiteSpace(album) && !durationMatches) return 0;
        }
        if (durationKnown && !durationMatches) return 0;
        return titleScore * 6 + artistScore * 4 + albumScore
            + (durationMatches ? 2 : 0);
    }

    private static double ArtistSimilarity(string left, string right)
    {
        var requested = ArtistCredits(left);
        var candidates = ArtistCredits(right);
        if (requested.Length == 0 || candidates.Length == 0) return 0;
        var exact = Normalize(left) == Normalize(right) ? 1 : 0;
        var overlap = requested.Count(candidates.Contains) / (double)Math.Max(requested.Length, candidates.Length);
        // A single SMTC credit may be the primary artist while a provider returns
        // the complete list (or vice versa). Do not treat substrings such as AC and
        // AC/DC as credits; a normalized whole-credit equality is required.
        if (requested.All(candidates.Contains) || candidates.All(requested.Contains)) overlap = 1;
        return Math.Max(exact, overlap);
    }

    private static string[] ArtistCredits(string value) => ArtistCreditSeparator.Split(Limit(value))
        .Select(Normalize).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static double TitleSimilarity(string left, string right)
    {
        var direct = Similarity(left, right);
        var a = ComparableTitle(left);
        var b = ComparableTitle(right);
        if (a.Length == 0 || b.Length == 0) return direct;
        if (a == b) return 1;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            direct = Math.Max(direct, (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length));
        return Math.Max(direct, EditSimilarity(a, b));
    }

    private static string ComparableTitle(string value)
    {
        var limited = PlayerSuffix.Replace(Limit(value), string.Empty);
        limited = FeaturedArtistDecoration.Replace(limited, string.Empty);
        limited = ReleaseDecoration.Replace(limited, string.Empty);
        return Normalize(limited);
    }

    private static double Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            return (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
        return 0;
    }

    private static double EditSimilarity(string left, string right)
    {
        var a = left.EnumerateRunes().ToArray();
        var b = right.EnumerateRunes().ToArray();
        if (a.Length < 5 || b.Length < 5 || a.Length > 256 || b.Length > 256) return 0;
        if (a.Length > b.Length) (a, b) = (b, a);
        var previous = Enumerable.Range(0, a.Length + 1).ToArray();
        var current = new int[a.Length + 1];
        for (var row = 1; row <= b.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= a.Length; column++)
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (a[column - 1] == b[row - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        var similarity = 1 - previous[a.Length] / (double)Math.Max(a.Length, b.Length);
        return similarity >= 0.78 ? similarity : 0;
    }

    private static double DurationTolerance(double requested, double candidate) =>
        Math.Clamp(Math.Max(20, Math.Min(requested, candidate) * 0.08), 20, 40);

    private static bool HasVersionConflict(string requested, string candidate)
    {
        var requestedLabels = Labels(requested);
        var candidateLabels = Labels(candidate);
        if (requestedLabels.Count == 0)
        {
            // A plain SMTC title must not silently resolve to a Live/Remix/Acoustic candidate
            // merely because the base title is similar. Less meaningful labels such as
            // "Original" and "Version" are intentionally not treated as a hard conflict.
            return candidateLabels.Count > 0;
        }

        return candidateLabels.Count == 0 || !requestedLabels.Overlaps(candidateLabels);
    }

    private static HashSet<string> Labels(string value)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in VersionLabels.Matches(Limit(value)))
        {
            var label = Normalize(match.Groups[1].Success ? match.Groups[1].Value : match.Value);
            foreach (var token in DisambiguatingVersionTokens)
            {
                if (label.Contains(token, StringComparison.OrdinalIgnoreCase)) labels.Add(token);
            }
        }
        return labels;
    }

    private static string Limit(string value) => string.Concat(value.EnumerateRunes().Take(MaximumMetadataCharacters).Select(rune => rune.ToString()));
}
