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
        @"(?:\s*(?:\(|\[|（|【)\s*(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+)[^\)\]）】]+(?:\)|\]|）|】)\s*|\s*[-–—:]\s*(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+).+|\s+(?:feat(?:uring)?|ft)(?:\.\s*|\s+).+)$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ReleaseDecoration = new(
        @"(?:\s*(?:\(|\[|（|【)\s*(?:(?:\d{4}\s*)?remaster(?:ed)?(?:\s*\d{4})?|explicit|clean|album\s+version|single\s+version|original\s+motion\s+picture\s+soundtrack)(?:\)|\]|）|】)\s*|\s*[-–—:]\s*(?:(?:\d{4}\s*)?remaster(?:ed)?(?:\s*\d{4})?|explicit|clean|album\s+version|single\s+version|original\s+motion\s+picture\s+soundtrack))$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    // Publisher descriptions are not part of the sung title. Keep language
    // identity separately so cleaning a search cannot select another language.
    private static readonly Regex SoundtrackDecoration = new(
        @"\s*(?:\(|\[|（|【)\s*[^\)\]）】]*(?:\u4e3b\u9898\u66f2|\u4e3b\u984c\u66f2|\u7247\u5934\u66f2|\u7247\u982d\u66f2|\u7247\u5c3e\u66f2|\u63d2\u66f2|\u63a8\u5e7f\u66f2|\u63a8\u5ee3\u66f2)\s*(?:\)|\]|）|】)\s*$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LanguageDecoration = new(
        @"\s*(?:\(|\[|（|【)\s*(\u4e2d\u6587|\u56fd\u8bed|\u570b\u8a9e|\u666e\u901a\u8bdd|\u666e\u901a\u8a71|\u82f1\u8bed|\u82f1\u8a9e|\u82f1\u6587|\u65e5\u8bed|\u65e5\u8a9e|\u65e5\u6587|\u97e9\u8bed|\u97d3\u8a9e|\u97e9\u6587|\u97d3\u6587|\u7ca4\u8bed|\u7cb5\u8a9e)(?:\u7248|\u7248\u672c)\s*(?:\)|\]|）|】)\s*$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex BilingualTitle = new(
        @"^([\u3400-\u9fff]{2,})\s+[A-Za-z][A-Za-z0-9 '’,:!?\-]+$",
        RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ArtistCreditSeparator = new(
        @"\s*(?:;|；|,|，|、|&|＆)\s*|\s+[/／]\s+|(?<=[^\u0000-\u007F])[/／]|[/／](?=[^\u0000-\u007F])|\s+(?:feat(?:uring)?|ft|with|x)\.?\s+",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    // Some SMTC publishers expose display-ready metadata such as
    // "Artist — Album" in the artist field while leaving AlbumTitle empty.
    // Preserve the original credit, but also expose the leading segment as an
    // alternative candidate. This is a publisher-agnostic recovery rule, not
    // an app identity check.
    private static readonly Regex PublisherMetadataSeparator = new(
        @"\s+(?:—|–|•|·)\s+|[\r\n]+",
        RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Regex DisambiguatingVersionWords = new(
        @"\b(?:live|remix|acoustic|instrumental|karaoke|radio|extended|edit|demo|concert|cover|sped\s*up|slowed)\b",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
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
        // Process stacked decorations, e.g. feat followed by [Chinese version].
        for (var pass = 0; pass < 4; pass++)
        {
            var before = title;
            title = LanguageDecoration.Replace(title, string.Empty);
            title = SoundtrackDecoration.Replace(title, string.Empty);
            title = FeaturedArtistDecoration.Replace(title, string.Empty);
            title = ReleaseDecoration.Replace(title, string.Empty);
            if (title == before) break;
        }
        return title.Trim();
    }

    public static IReadOnlyList<string> SearchTerms(LyricsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var title = SearchTitle(query.Title);
        return query.ArtistCandidates
            .Take(2)
            .Select(artist => $"{title} {artist}".Trim())
            .Append(title)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
    }

    public static IReadOnlyList<string> ExpandArtistCandidates(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var candidates = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var limited = Limit(value).Trim();
            candidates.Add(limited);
            var separator = PublisherMetadataSeparator.Match(limited);
            if (separator.Success && separator.Index > 0)
            {
                var leadingCredit = limited[..separator.Index].Trim();
                if (leadingCredit.Length > 0) candidates.Add(leadingCredit);
            }
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool AreTitlesEquivalent(string left, string right) =>
        !HasVersionConflict(left, right) && !HasVersionConflict(right, left) &&
        ComparableTitle(left) is { Length: > 0 } title && title == ComparableTitle(right);

    public static bool AreArtistCreditsCompatible(string left, string right) => ArtistSimilarity(left, right) >= 0.6;

    public static double Score(LyricsQuery query, string title, string artist, string album, double durationSeconds,
        IReadOnlyList<string>? artistAliases = null)
    {
        var titleScore = TitleSimilarity(query.Title, title);
        // Some media publishers reverse title and artist fields.
        if (titleScore < 0.4 && TitleSimilarity(query.Title, artist) > 0.8 && ArtistSimilarity(query.ArtistCandidates, title) > 0.8)
        { (title, artist) = (artist, title); titleScore = TitleSimilarity(query.Title, title); }
        // Catalogues can append the English title after the exact Chinese title.
        // This recovery needs independent artist AND duration evidence; substring
        // similarity alone must never authorize a different song or language.
        var artistScore = ArtistSimilarity(query.ArtistCandidates, artist);
        if (artistAliases is not null)
            foreach (var alias in artistAliases.Take(16))
                artistScore = Math.Max(artistScore, ArtistSimilarity(query.ArtistCandidates, alias));
        var bilingualMatch = BilingualBaseMatches(query.Title, title);
        if (bilingualMatch && artistScore >= 0.6 &&
            query.Duration > TimeSpan.Zero && double.IsFinite(durationSeconds) && durationSeconds > 0 &&
            Math.Abs(query.Duration.TotalSeconds - durationSeconds) <= DurationTolerance(query.Duration.TotalSeconds, durationSeconds))
            titleScore = Math.Max(titleScore, 0.95);
        if (titleScore < 0.45) return 0;
        if (!query.HasDisambiguatingMetadata) return 0;
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album) &&
            (!double.IsFinite(durationSeconds) || durationSeconds <= 0)) return 0;
        if (HasVersionConflict(query.Title, title)) return 0;
        var albumScore = Similarity(query.Album, album);
        var candidateDuration = double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0;
        var durationDelta = Math.Abs(query.Duration.TotalSeconds - candidateDuration);
        var durationKnown = candidateDuration > 0 && query.Duration.TotalSeconds > 0;
        var durationMatches = durationKnown && durationDelta <= DurationTolerance(query.Duration.TotalSeconds, candidateDuration);
        var artistKnown = query.ArtistCandidates.Count > 0 && !string.IsNullOrWhiteSpace(artist);
        var artistMatches = artistKnown && artistScore >= 0.6;
        var albumMatches = !string.IsNullOrWhiteSpace(query.Album) && !string.IsNullOrWhiteSpace(album) && albumScore >= 0.6;

        // Provider catalogues often omit an album, use a compilation/deluxe album, or
        // round duration differently. Keep title mandatory, but let independent artist,
        // album and duration evidence corroborate one another instead of requiring every
        // optional field to be present and identical.
        if (query.ArtistCandidates.Count > 0)
        {
            // A known conflicting artist remains a hard rejection. Cross-publisher performer
            // versus album-artist differences are handled by ArtistCandidates rather than by
            // weakening this wrong-song safeguard.
            if (artistKnown && !artistMatches) return 0;
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

    private static double ArtistSimilarity(IReadOnlyList<string> requested, string candidate) =>
        requested.Count == 0 ? 0 : requested.Max(value => ArtistSimilarity(value, candidate));

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
        .Select(ArtistCreditOrthography.Fold).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

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

    private static bool BilingualBaseMatches(string left, string right)
    {
        var a = SearchTitle(left);
        var b = SearchTitle(right);
        var am = BilingualTitle.Match(a);
        var bm = BilingualTitle.Match(b);
        return bm.Success && Normalize(a) == Normalize(bm.Groups[1].Value) ||
            am.Success && Normalize(b) == Normalize(am.Groups[1].Value);
    }

    private static string ComparableTitle(string value)
    {
        return Normalize(SearchTitle(value));
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

    public static bool HasVersionConflict(string requested, string candidate)
    {
        var requestedLanguage = LanguageVersion(requested);
        var candidateLanguage = LanguageVersion(candidate);
        if (requestedLanguage is not null && candidateLanguage is not null && requestedLanguage != candidateLanguage)
            return true;
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

    private static string? LanguageVersion(string value)
    {
        var match = LanguageDecoration.Match(PlayerSuffix.Replace(Limit(value), string.Empty));
        if (!match.Success) return null;
        return match.Groups[1].Value switch
        {
            "\u4e2d\u6587" or "\u56fd\u8bed" or "\u570b\u8a9e" or "\u666e\u901a\u8bdd" or "\u666e\u901a\u8a71" => "zh-Hans",
            "\u82f1\u6587" or "\u82f1\u8bed" or "\u82f1\u8a9e" => "en",
            "\u65e5\u6587" or "\u65e5\u8bed" or "\u65e5\u8a9e" => "ja",
            "\u97e9\u6587" or "\u97d3\u6587" or "\u97e9\u8bed" or "\u97d3\u8a9e" => "ko",
            "\u7ca4\u8bed" or "\u7cb5\u8a9e" => "yue",
            _ => null,
        };
    }

    private static HashSet<string> Labels(string value)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in VersionLabels.Matches(FeaturedArtistDecoration.Replace(Limit(value), string.Empty)))
        {
            var label = match.Groups[1].Success ? match.Groups[1].Value : match.Value;
            // Compare whole version words before removing punctuation. Otherwise names
            // such as Oliver in a featured credit contain "live" and reject the studio song.
            foreach (Match word in DisambiguatingVersionWords.Matches(label))
                labels.Add(Normalize(word.Value));
        }
        return labels;
    }

    private static string Limit(string value) => string.Concat(value.EnumerateRunes().Take(MaximumMetadataCharacters).Select(rune => rune.ToString()));
}
