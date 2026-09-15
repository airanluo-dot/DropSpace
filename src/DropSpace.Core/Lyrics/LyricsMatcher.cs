using System.Text;
using System.Text.RegularExpressions;

namespace DropSpace.Core.Lyrics;

public static class LyricsMatcher
{
    // These Unicode regex tokens recognize publisher suffixes, not UI strings.
    private static readonly Regex PlayerSuffix = new(@"\s*[-|–]\s*(?:Apple Music|QQ\u97f3\u4e50|\u7f51\u6613\u4e91\u97f3\u4e50|\u9177\u72d7\u97f3\u4e50|Spotify|YouTube)\s*$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Brackets = new(@"[\(\[（【][^\)\]）】]*[\)\]）】]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var input = value[..Math.Min(value.Length, 2_048)].Normalize(NormalizationForm.FormKC);
        return string.Concat(PlayerSuffix.Replace(input, string.Empty).EnumerateRunes()
            .Where(Rune.IsLetterOrDigit).Select(rune => Rune.ToLowerInvariant(rune).ToString()));
    }

    public static string SearchTitle(string value) => PlayerSuffix.Replace(Brackets.Replace(value[..Math.Min(value.Length, 2_048)], string.Empty), string.Empty).Trim();

    public static double Score(LyricsQuery query, string title, string artist, string album, double durationSeconds)
    {
        var titleScore = Similarity(query.Title, title);
        // Some media publishers reverse title and artist fields.
        if (titleScore < 0.4 && Similarity(query.Title, artist) > 0.8 && Similarity(query.Artist, title) > 0.8) titleScore = 0.85;
        if (titleScore < 0.45) return 0;
        var durationDelta = Math.Abs(query.Duration.TotalSeconds - durationSeconds);
        if (durationSeconds > 0 && query.Duration.TotalSeconds > 0 && durationDelta > 30) return 0;
        return titleScore * 6 + ArtistSimilarity(query.Artist, artist) * 3 + Similarity(query.Album, album)
            + (durationSeconds > 0 && durationDelta <= 5 ? 2 : 0);
    }

    private static double ArtistSimilarity(string left, string right)
    {
        // SMTC often supplies only the first credited artist. Preserve provider
        // artist boundaries so additional credits do not penalize the right song.
        var separators = new[] { ';', ',', '、', '/', '&' };
        var requested = left[..Math.Min(left.Length, 2_048)].Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(value => value.Length > 0).Distinct().ToArray();
        var candidates = right[..Math.Min(right.Length, 2_048)].Split(separators, StringSplitOptions.RemoveEmptyEntries)
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
        var coreA = Normalize(Brackets.Replace(left[..Math.Min(left.Length, 2_048)], string.Empty));
        var coreB = Normalize(Brackets.Replace(right[..Math.Min(right.Length, 2_048)], string.Empty));
        if (coreA.Length > 0 && coreA == coreB) return 0.85;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            return (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
        return 0;
    }
}
