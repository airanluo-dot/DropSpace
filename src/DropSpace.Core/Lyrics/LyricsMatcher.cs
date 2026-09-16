using System.Text;
using System.Text.RegularExpressions;

namespace DropSpace.Core.Lyrics;

public static class LyricsMatcher
{
    private const int MaximumMetadataCharacters = 2_048;
    // These Unicode regex tokens recognize publisher suffixes, not UI strings.
    private static readonly Regex PlayerSuffix = new(@"\s*[-|–]\s*(?:Apple Music|QQ\u97f3\u4e50|\u7f51\u6613\u4e91\u97f3\u4e50|\u9177\u72d7\u97f3\u4e50|Spotify|YouTube)\s*$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex VersionLabels = new(@"(?:\(|\[|（|【)([^\)\]）】]*)(?:\)|\]|）|】)|\b(live|remix|acoustic|instrumental|karaoke|radio|extended|edit|demo|version|mono|stereo|original|concert|cover|sped\s*up|slowed)\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
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

    public static string SearchTitle(string value) => PlayerSuffix.Replace(Limit(value), string.Empty).Trim();

    public static double Score(LyricsQuery query, string title, string artist, string album, double durationSeconds)
    {
        var titleScore = Similarity(query.Title, title);
        // Some media publishers reverse title and artist fields.
        if (titleScore < 0.4 && Similarity(query.Title, artist) > 0.8 && Similarity(query.Artist, title) > 0.8)
        { (title, artist) = (artist, title); titleScore = Similarity(query.Title, title); }
        if (titleScore < 0.45) return 0;
        if (!query.HasDisambiguatingMetadata) return 0;
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album) &&
            (!double.IsFinite(durationSeconds) || durationSeconds <= 0)) return 0;
        if (HasVersionConflict(query.Title, title)) return 0;
        var artistScore = ArtistSimilarity(query.Artist, artist);
        if (!string.IsNullOrWhiteSpace(query.Artist) && (string.IsNullOrWhiteSpace(artist) || artistScore < 0.5)) return 0;
        var albumScore = Similarity(query.Album, album);
        if (!string.IsNullOrWhiteSpace(query.Album) && (string.IsNullOrWhiteSpace(album) || albumScore < 0.5)) return 0;
        var candidateDuration = double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0;
        var durationDelta = Math.Abs(query.Duration.TotalSeconds - candidateDuration);
        if (candidateDuration > 0 && query.Duration.TotalSeconds > 0 && durationDelta > 12) return 0;
        return titleScore * 6 + artistScore * 4 + albumScore
            + (candidateDuration > 0 && durationDelta <= 5 ? 2 : 0);
    }

    private static double ArtistSimilarity(string left, string right)
    {
        // SMTC often supplies only the first credited artist. Preserve provider
        // artist boundaries so additional credits do not penalize the right song.
        var separators = new[] { ';', ',', '、', '/', '&' };
        var requested = Limit(left).Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(value => value.Length > 0).Distinct().ToArray();
        var candidates = Limit(right).Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
        var credits = requested.Length == 0 ? 0 : requested.Count(candidates.Contains) / (double)requested.Length;
        return Math.Max(Similarity(left, right), credits);
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
