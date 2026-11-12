using System.Text;
using System.Text.RegularExpressions;

namespace DropSpace.Core.Lyrics;

public static partial class LyricsMatcher
{
    public const string Version = "recording-identity-v7-corroborated-catalogue";
    private const int MaximumMetadataCharacters = 2_048;
    // These Unicode regex tokens recognize publisher suffixes, not UI strings.
    private static readonly Regex PlayerSuffix = new(@"\s*[-|–]\s*(?:Apple Music|QQ\u97f3\u4e50|\u7f51\u6613\u4e91\u97f3\u4e50|\u9177\u72d7\u97f3\u4e50|Spotify|YouTube)\s*$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex FeaturedArtistDecoration = new(
        @"(?:\s*(?:\(|\[|（|【)\s*(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+)[^\)\]）】]+(?:\)|\]|）|】)\s*|\s*[-–—:]\s*(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+).+|\s+(?:feat(?:uring)?|ft)(?:\.\s*|\s+).+)$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex FeaturedArtistNames = new(
        @"\b(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+)([^\)\]）】]+)",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex FeaturedArtistCredit = new(
        @"(?:\(|\[|（|【)\s*(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+)([^\)\]）】]+)(?:\)|\]|）|】)",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ReleaseDecoration = new(
        @"(?:\s*(?:\(|\[|（|【)\s*(?:(?:\d{4}\s*)?remaster(?:ed)?(?:\s*\d{4})?|explicit|clean|album\s+version|single\s+version|original\s+motion\s+picture\s+soundtrack)(?:\)|\]|）|】)\s*|\s*[-–—:]\s*(?:(?:\d{4}\s*)?remaster(?:ed)?(?:\s*\d{4})?|explicit|clean|album\s+version|single\s+version|original\s+motion\s+picture\s+soundtrack))$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex DisplayDecoration = new(
        @"(?:\s*[(\[（【]\s*(?<kind>official\s+(?:audio|(?:music\s+)?video))\s*[)\]）】]|\s+[-–—|]\s*(?<kind>official\s+(?:audio|(?:music\s+)?video)))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ReleaseEdition = new(
        @"(?:[(\[（【]|\s[-–—:]\s*)\s*(?<kind>explicit|clean|album\s+version|single\s+version)\s*(?:[)\]）】]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    // Publisher descriptions are not part of the sung title. Keep language
    // identity separately so cleaning a search cannot select another language.
    private static readonly Regex SoundtrackDecoration = new(
        @"\s*(?:\(|\[|（|【)\s*[^\)\]）】]*(?:\u4e3b\u9898\u66f2|\u4e3b\u984c\u66f2|\u7247\u5934\u66f2|\u7247\u982d\u66f2|\u7247\u5c3e\u66f2|\u63d2\u66f2|\u63a8\u5e7f\u66f2|\u63a8\u5ee3\u66f2)\s*(?:\)|\]|）|】)\s*$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LanguageDecoration = new(
        @"\s*(?:\(|\[|（|【)\s*(\u4e2d\u6587|\u56fd\u8bed|\u570b\u8a9e|\u666e\u901a\u8bdd|\u666e\u901a\u8a71|\u82f1\u8bed|\u82f1\u8a9e|\u82f1\u6587|\u65e5\u8bed|\u65e5\u8a9e|\u65e5\u6587|\u97e9\u8bed|\u97d3\u8a9e|\u97e9\u6587|\u97d3\u6587|\u7ca4\u8bed|\u7cb5\u8a9e)(?:\u7248|\u7248\u672c)\s*(?:\)|\]|）|】)\s*$",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EnglishLanguageDecoration = new(
        @"(?:\s*[(\[（【]\s*(english|japanese|chinese|mandarin|cantonese|korean)\s+(?:version|ver\.?)\s*[)\]）】]|\s+[-–—]\s*(english|japanese|chinese|mandarin|cantonese|korean)\s+(?:version|ver\.?))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
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
    private static readonly Regex FastSagaReleaseCredit = new(
        @"(?:\s*[,;&]\s*)?Fast\s*&\s*Furious:\s*The\s+Fast\s+Saga(?=\s*(?:[,;]|$))",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private const string EnglishRecordingKinds = @"live|remix|acoustic|instrumental|karaoke|radio|extended|edit|demo|concert|cover|sped[\s-]*up|slowed";
    private const string ChineseRecordingKinds = "现场|現場|混音|伴奏|翻唱|不插电|不插電";
    private const string RecordingYear = @"(?:19|20)[0-9]{2}";
    private const string EditionSeparator = @"[\s\p{Pd}()\[\]【】]*";
    // Only canonicalize a complete edition suffix. A venue, remixer or other recording
    // credit remains part of the title and must match across catalogues.
    private static readonly Regex RecordingSuffix = new(
        @"(?:[\(\[【–—-]\s*|\s+|(?<=[\u3400-\u9fff])(?=[a-z]|(?:" + ChineseRecordingKinds + @")版))" +
        @"(?:(?<before>" + RecordingYear + @")" + EditionSeparator + @")?(?<kind>" + EnglishRecordingKinds + "|" + ChineseRecordingKinds + @")" +
        @"(?:\s*(?:版本?|ver(?:sion)?\.?))?(?:" + EditionSeparator + @"(?<after>" + RecordingYear + @"))?\s*[\)\]】]*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex DisambiguatingVersionWords = new(
        @"\b(?:" + EnglishRecordingKinds + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var input = Limit(value).Normalize(NormalizationForm.FormKC);
        return string.Concat(PlayerSuffix.Replace(input, string.Empty).EnumerateRunes()
            .Where(Rune.IsLetterOrDigit).Select(rune => Rune.ToLowerInvariant(rune).ToString()));
    }

    public static string SearchTitle(string value)
    {
        var title = FeaturedArtistCredit.Replace(PlayerSuffix.Replace(Limit(value), string.Empty), string.Empty);
        // Process stacked decorations, e.g. feat followed by [Chinese version].
        for (var pass = 0; pass < 4; pass++)
        {
            var before = title;
            title = DisplayDecoration.Replace(title, string.Empty);
            title = LanguageDecoration.Replace(title, string.Empty);
            title = EnglishLanguageDecoration.Replace(title, string.Empty);
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
        var artists = SearchArtists(query).Take(2).ToArray();
        if (DisplayDecoration.IsMatch(Limit(query.Title)))
            return new[] { $"{title} {artists.FirstOrDefault()}".Trim(), $"{query.Title} {artists.FirstOrDefault()}".Trim(), title }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var mapped = artists.Select(artist => LyricsNameAliases.SearchTerm(query.Title, artist))
            .FirstOrDefault(term => term is not null);
        if (mapped is not null)
            return new[] { mapped, $"{title} {artists.FirstOrDefault()}".Trim(), title }
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        var aliases = query.ArtistCandidates.Any(artist => KnownRecordingAlias(query.Title, artist))
            ? new[] { "怪獣 サカナクション", "Kaiju Sakanaction" } : [];
        return SearchArtists(query)
            .Take(1)
            .Select(artist => $"{title} {artist}".Trim())
            .Concat(aliases)
            .Concat(SearchArtists(query).Where(artist => Normalize(artist) == "abeltesfaye")
                .Take(1).Select(_ => $"{title} The Weeknd"))
            .Concat(SearchArtists(query).Skip(1).Take(1).Select(artist => $"{title} {artist}".Trim()))
            .Append(title)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
    }

    public static IEnumerable<string> SearchArtists(LyricsQuery query) => ExpandArtistCandidates(
        string.IsNullOrWhiteSpace(query.Artist) ? query.AlbumArtist : query.Artist)
        // Prefer complete performer credits over the publisher's "Artist — Album" display string.
        .OrderBy(artist => PublisherMetadataSeparator.IsMatch(artist) ? 1 : 0);

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

    public static IReadOnlyList<string> ExpandRecordingArtistCandidates(string title, string artist) =>
        ExpandArtistCandidates(artist).Select(value => WithFeaturedArtists(title, value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool HasFeaturedArtistCredit(string title) => FeaturedArtistCredit.IsMatch(Limit(title)) ||
        FeaturedArtistDecoration.IsMatch(Limit(title));

    public static string TitleWithoutFeaturedArtists(string title) => FeaturedArtistDecoration.Replace(
        FeaturedArtistCredit.Replace(Limit(title), string.Empty), string.Empty).Trim();

    private static string WithFeaturedArtists(string title, string artist)
    {
        // A featured performer can live only in the title. Removing that decoration
        // for search must not also remove it from the recording's required credits.
        var credits = FeaturedArtistCredit.Matches(Limit(title)).Select(match => match.Groups[1].Value.Trim()).Take(16).ToArray();
        if (credits.Length > 0) return artist + "; " + string.Join("; ", credits);
        var decoration = FeaturedArtistDecoration.Match(LanguageDecoration.Replace(Limit(title), string.Empty));
        if (!decoration.Success) return artist;
        var names = FeaturedArtistNames.Match(decoration.Value);
        return names.Success ? artist + "; " + names.Groups[1].Value.Trim() : artist;
    }

    // Bounded publisher evidence: https://sakanaction.jp/news/detail/2959 and ?lang=en.
    // This never provides a general romanization/fuzzy-title equivalence.
    private static bool Sakanaction(string artist) => Normalize(artist) is "sakanaction" or "サカナクション" or "鱼韵" or "魚韻";
    private static bool KnownRecordingAlias(string title, string artist) => Sakanaction(artist) &&
        Normalize(SearchTitle(title)) is "kaiju" or "怪獣";

    public static bool AreTitlesEquivalent(string left, string right) =>
        !HasVersionConflict(left, right) && !HasVersionConflict(right, left) &&
        ComparableTitle(left) is { Length: > 0 } title && title == ComparableTitle(right);

    public static bool AreArtistCreditsCompatible(string left, string right) => ArtistSimilarity(left, right) == 1;

    // A catalogue artist's own aliases can identify that performer within a collaboration.
    // The complete projected list still has to pass the recording matcher.
    public static string? FindCreditedArtistName(string fullCredit, IEnumerable<string> names) =>
        FindCreditedArtistName(fullCredit, names, false);

    public static string? FindCreditedArtistName(string fullCredit, IEnumerable<string> names, bool multipleSourceArtists)
    {
        var requested = ArtistCredits(fullCredit);
        if (multipleSourceArtists && (fullCredit.Contains('/') || fullCredit.Contains('／')))
            requested = requested.Concat(ArtistCreditSeparator.Split(Limit(fullCredit)).SelectMany(SlashCredits))
                .Distinct(StringComparer.Ordinal).ToArray();
        return names.FirstOrDefault(name =>
        {
            var parts = ArtistCredits(name);
            return parts.Length > 0 && parts.All(requested.Contains);
        });
    }

    // Each row contains one source artist's canonical name followed by that
    // artist's aliases. Never turn an individual alias into a whole band's credit.
    public static string[] ProjectArtistCredits(LyricsQuery query, string[][] sourceArtists)
    {
        return query.ArtistCandidates.Take(4).Select(requested =>
        {
            var credits = sourceArtists.Select(names =>
                FindCreditedArtistName(requested, names, sourceArtists.Length > 1) ?? names[0]).ToArray();
            return credits.Select(Normalize).Distinct(StringComparer.Ordinal).Count() == credits.Length
                ? string.Join("; ", credits) : string.Empty;
        }).Where(credit => credit.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    // Collection admission is not authorization to publish an original. Unknown
    // cross-script artists reach the selector only with exact title/version and
    // independent duration evidence; the strict Score remains the rules fallback.
    public static double CandidateScore(LyricsQuery query, string title, string artist, string album,
        double durationSeconds, IReadOnlyList<string>? artistAliases = null) =>
        CandidateScore(query, title, artist, album, durationSeconds, artistAliases, null);

    public static double CandidateScore(LyricsQuery query, string title, string artist, string album,
        double durationSeconds, IReadOnlyList<string>? artistAliases, IReadOnlyList<string>? titleAliases)
    {
        var strict = Score(query, title, artist, album, durationSeconds, artistAliases, titleAliases);
        if (strict >= 4 || !query.CollectSelectionCandidates) return strict;
        return IsSafeSelectionCandidate(query, title, artist, durationSeconds, album) ? 4 : 0;
    }

    public static bool IsSafeSelectionCandidate(LyricsQuery query, string title, string artist, double durationSeconds, string album = "") =>
        Score(query, title, artist, album, durationSeconds) >= 4 ||
        query.HasDisambiguatingMetadata && !string.IsNullOrWhiteSpace(artist) &&
        !HasConflictingCredits(query, title, artist) &&
        AreTitlesEquivalent(query.Title, title) && double.IsFinite(durationSeconds) && durationSeconds >= 0 &&
        (query.Duration <= TimeSpan.Zero || durationSeconds <= 0 ||
            Math.Abs(query.Duration.TotalSeconds - durationSeconds) <= DurationTolerance(query.Duration.TotalSeconds, durationSeconds)) &&
        (query.ArtistCandidates.Any(value => AreArtistCreditsCompatible(value, WithFeaturedArtists(title, artist))) ||
            query.Duration > TimeSpan.Zero && durationSeconds > 0 ||
            Normalize(query.Album) is { Length: > 0 } requestedAlbum && requestedAlbum == Normalize(album));

    private static bool HasConflictingCredits(LyricsQuery query, string title, string artist)
    {
        artist = WithFeaturedArtists(title, artist);
        var similarity = ArtistSimilarity(query.ArtistCandidates, artist);
        if (similarity > 0 && similarity < 1) return true;
        // Unknown cross-script aliases can be reviewed using independent metadata.
        // Different known Latin credits or a partial collaboration cannot be rescued by a model.
        bool Latin(string text) => text.EnumerateRunes().Where(Rune.IsLetter).All(r => r.Value < 0x0250);
        return similarity < 1 && Latin(artist) && query.ArtistCandidates.Any(Latin);
    }

    public static double Score(LyricsQuery query, LyricsMatchInfo match) => Score(query,
        string.IsNullOrWhiteSpace(match.CanonicalTitle) ? match.Title : match.CanonicalTitle,
        match.Artist, match.Album, match.DurationSeconds, match.ArtistAliases,
        string.IsNullOrWhiteSpace(match.CandidateId) ? null : match.TitleAliases);

    public static double Score(LyricsQuery query, string title, string artist, string album, double durationSeconds,
        IReadOnlyList<string>? artistAliases = null) => Score(query, title, artist, album, durationSeconds, artistAliases, null);

    public static double Score(LyricsQuery query, string title, string artist, string album, double durationSeconds,
        IReadOnlyList<string>? artistAliases, IReadOnlyList<string>? titleAliases)
    {
        var titleScore = TitleSimilarity(query.Title, title);
        if (query.ArtistCandidates.Any(value => KnownRecordingAlias(query.Title, value)) && KnownRecordingAlias(title, artist) &&
            query.Duration > TimeSpan.Zero && double.IsFinite(durationSeconds) && durationSeconds > 0 &&
            Math.Abs(query.Duration.TotalSeconds - durationSeconds) <= DurationTolerance(query.Duration.TotalSeconds, durationSeconds))
            titleScore = 1;
        // Some media publishers reverse title and artist fields.
        if (titleScore < 0.4 && TitleSimilarity(query.Title, artist) > 0.8 && ArtistSimilarity(query.ArtistCandidates, title) > 0.8)
        { (title, artist) = (artist, title); titleScore = TitleSimilarity(query.Title, title); }
        // Catalogues can append the English title after the exact Chinese title.
        // This recovery needs independent artist AND duration evidence; substring
        // similarity alone must never authorize a different song or language.
        var artistScore = ArtistSimilarity(query.ArtistCandidates, WithFeaturedArtists(title, artist));
        if (artistAliases is not null)
            foreach (var alias in artistAliases.Take(16))
                artistScore = Math.Max(artistScore, ArtistSimilarity(query.ArtistCandidates, WithFeaturedArtists(title, alias)));
        if (KnownFastXRecordingCredits(query, title, artist, album, durationSeconds)) artistScore = 1;
        if (HasVersionConflict(query.Title, title)) return 0;
        var durationKnown = query.Duration > TimeSpan.Zero && double.IsFinite(durationSeconds) && durationSeconds > 0;
        var durationDelta = durationKnown ? Math.Abs(query.Duration.TotalSeconds - durationSeconds) : double.PositiveInfinity;
        var closeDuration = durationDelta <= 3;
        var albumScore = Similarity(query.Album, album);
        var requestedEdition = Edition(query.Title, contentRating: false);
        var candidateEdition = Edition(title, contentRating: false);
        if (requestedEdition.Length > 0 && candidateEdition != requestedEdition && !closeDuration) return 0;
        // A music video's runtime may include an introduction absent from the studio recording.
        if (DisplayDecoration.Match(Limit(query.Title)).Groups["kind"].Value.EndsWith("video", StringComparison.OrdinalIgnoreCase) &&
            Normalize(query.Title) != Normalize(title) && !closeDuration) return 0;
        // A pinned translated name expands the catalogue lookup only for this artist.
        // It cannot erase recording/language labels or compensate for uncertain credits/time.
        if (artistScore == 1 && !HasVersionConflict(title, query.Title) && durationKnown &&
            durationDelta <= DurationTolerance(query.Duration.TotalSeconds, durationSeconds) &&
            query.ArtistCandidates.Any(value => LyricsNameAliases.AreRecordingTitlesEquivalent(query.Title, title, value)))
            titleScore = 1;
        if (titleScore < 1 && titleAliases is not null && artistScore == 1 && closeDuration &&
            QualifiedRecordingSuffix(query.Title) == QualifiedRecordingSuffix(title) &&
            !HasVersionConflict(title, query.Title) && titleAliases.Take(16).Any(alias => AreTitlesEquivalent(query.Title, alias)))
            titleScore = 1;
        var bilingualMatch = artistScore == 1 && durationKnown &&
            durationDelta <= DurationTolerance(query.Duration.TotalSeconds, durationSeconds) && BilingualBaseMatches(query.Title, title);
        if (bilingualMatch) titleScore = Math.Max(titleScore, 0.95);
        if (titleScore < 1 && !(bilingualMatch && titleScore >= 0.95))
        {
            if (artistScore != 1 || albumScore != 1 || !closeDuration ||
                QualifiedRecordingSuffix(query.Title) != QualifiedRecordingSuffix(title) ||
                !AdjacentTitleTransposition(query.Title, title)) return 0;
            titleScore = 0.9;
        }
        // Album names can contain ordinary words such as "live", "remix" and
        // "acoustic" without describing this track's recording. Hard version
        // and language conflicts belong to the track title; album metadata
        // remains independent corroboration below rather than a version label.
        var durationMatches = durationKnown && durationDelta <= DurationTolerance(query.Duration.TotalSeconds, durationSeconds);
        var artistKnown = query.ArtistCandidates.Count > 0 && !string.IsNullOrWhiteSpace(artist);
        var artistMatches = artistKnown && artistScore == 1;
        var albumMatches = !string.IsNullOrWhiteSpace(query.Album) && !string.IsNullOrWhiteSpace(album) && albumScore >= 0.6;
        if (!artistMatches && !albumMatches && !closeDuration) return 0;

        // Provider catalogues often omit an album, use a compilation/deluxe album, or
        // round duration differently. Keep title mandatory, but let independent artist,
        // album and duration evidence corroborate one another instead of requiring every
        // optional field to be present and identical.
        if (query.ArtistCandidates.Count > 0)
        {
            // A known conflicting artist remains a hard rejection. Cross-publisher performer
            // metadata has priority over narrower album-artist credits. ArtistCandidates
            // uses the album artist only when performer metadata is absent.
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
        if (Sakanaction(left) && Sakanaction(right)) return 1;
        var requested = ArtistCredits(left);
        var candidates = ArtistCredits(right);
        if (requested.Length == 0 || candidates.Length == 0) return 0;
        var overlap = requested.Count(candidates.Contains) / (double)Math.Max(requested.Length, candidates.Length);
        // Extra/missing performers can identify another recording. A subset does
        // not authorize that recording; aliases retain all individual credits.
        if (overlap == 1) return overlap;
        return MatchesSlashCredits(left, candidates) || MatchesSlashCredits(right, requested) ? 1 : overlap;
    }

    // SMTC sometimes joins multiple performers with a tight slash. Only split a field
    // when the other catalogue actually lists the resulting full names independently.
    private static bool MatchesSlashCredits(string value, string[] expected)
    {
        if (expected.Length < 2 || !value.Contains('/') && !value.Contains('／')) return false;
        var parts = ArtistCreditSeparator.Split(Limit(value)).SelectMany(credit =>
        {
            var canonical = CanonicalArtistCredit(credit);
            if (expected.Contains(canonical)) return new[] { canonical };
            return SlashCredits(credit);
        }).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return parts.Length == expected.Length && parts.All(expected.Contains);
    }

    private static string[] SlashCredits(string credit)
    {
        var split = credit.Split(['/', '／'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Preserve compact initialisms such as AC/DC as a single artist name.
        return split.Length > 1 && split.Any(name => name.Length > 2)
            ? split.Select(CanonicalArtistCredit).ToArray() : [CanonicalArtistCredit(credit)];
    }

    private static string[] ArtistCredits(string value) => ArtistCreditSeparator.Split(Limit(value))
        .Select(CanonicalArtistCredit).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static bool KnownFastXRecordingCredits(LyricsQuery query, string title, string artist, string album,
        double durationSeconds)
    {
        // Bounded catalogue equivalence, not a general permission to discard credits.
        // Apple Music lists the franchise entity beside the artists, with G Herbo
        // in the title: https://music.apple.com/us/song/1685731986.
        // The release owner's tracklist confirms all three performers:
        // https://www.universalmusic.com.br/2023/05/22/virgin-artist-partner-group-e-universal-pictures-em-parceria-com-universal-music-group-anunciam-o-lancamento-de-fast-x-original-motion-picture-soundtrack/
        if (ComparableTitle(query.Title) != "mycity" || ComparableTitle(title) != "mycity" ||
            HasVersionConflict(query.Title, title) || HasVersionConflict(title, query.Title) ||
            query.Duration.TotalSeconds is < 148 or > 151 || !double.IsFinite(durationSeconds) || durationSeconds is < 148 or > 151 ||
            !Normalize(album).StartsWith("fastxoriginalmotionpicturesoundtrack", StringComparison.Ordinal)) return false;
        var publisherSeparator = PublisherMetadataSeparator.Match(query.Artist);
        var requestedAlbum = string.IsNullOrWhiteSpace(query.Album) && publisherSeparator.Success
            ? query.Artist[(publisherSeparator.Index + publisherSeparator.Length)..] : query.Album;
        if (!Normalize(requestedAlbum).StartsWith("fastxoriginalmotionpicturesoundtrack", StringComparison.Ordinal)) return false;
        bool CompletePerformers(string credit)
        {
            var values = ArtistCredits(credit);
            return values.Length == 3 && values.Contains("24kgoldn") && values.Contains("kanebrown") && values.Contains("gherbo");
        }
        // Require the exact known entity as well as all actual vocal credits.
        // Neither a narrower AlbumArtist nor an unknown fourth artist is an alias.
        return query.ArtistCandidates.Any(credit => FastSagaReleaseCredit.IsMatch(credit) &&
            CompletePerformers(FastSagaReleaseCredit.Replace(credit, string.Empty))) &&
            CompletePerformers(FastSagaReleaseCredit.Replace(WithFeaturedArtists(title, artist), string.Empty));
    }

    // Exact whole-credit alias, corroborated by Apple Music's Starboy catalogue:
    // https://music.apple.com/qa/song/1677006158. Never infer aliases from substrings.
    private static string CanonicalArtistCredit(string value) => Normalize(value) switch
    {
        "abeltesfaye" or "theweeknd" => "theweeknd",
        // The artist's release credits explicitly use both names; this exact
        // alias does not discard arbitrary Latin suffixes from Chinese names.
        // https://www.bilibili.com/video/BV18s411F7uC/ (official Gujian publisher).
        "孙晔" or "孙晔gary" => "孙晔",
        _ => LyricsNameAliases.CanonicalArtist(value)
    };

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
        return bm.Success && ArtistCreditOrthography.Fold(a) == ArtistCreditOrthography.Fold(bm.Groups[1].Value) ||
            am.Success && ArtistCreditOrthography.Fold(b) == ArtistCreditOrthography.Fold(am.Groups[1].Value);
    }

    private static string ComparableTitle(string value)
    {
        var title = SearchTitle(value).Normalize(NormalizationForm.FormKC);
        var suffix = RecordingSuffix.Match(title);
        if (suffix.Success && suffix.Index > 0)
            title = title[..suffix.Index] + CanonicalRecordingKind(suffix.Groups["kind"].Value) +
                suffix.Groups["before"].Value + suffix.Groups["after"].Value;
        return ArtistCreditOrthography.Fold(title);
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

    // Catalogues round independently; distinguish meaningful timing differences before
    // spending the fixed body-download budget, without ranking sub-second roundoff.
    public static double DurationError(LyricsQuery query, double seconds) =>
        query.Duration > TimeSpan.Zero && double.IsFinite(seconds) && seconds > 0
            ? Math.Max(0, Math.Abs(query.Duration.TotalSeconds - seconds) - 1)
            : double.PositiveInfinity;

    public static bool IsSpellingMatch(LyricsQuery query, LyricsMatchInfo match)
    {
        var title = string.IsNullOrWhiteSpace(match.CanonicalTitle) ? match.Title : match.CanonicalTitle;
        return !string.Equals(query.Title, title, StringComparison.OrdinalIgnoreCase) && AdjacentTitleTransposition(query.Title, title);
    }

    public static bool NeedsRecordingComparison(LyricsQuery query, LyricsMatchInfo match) =>
        query.ArtistCandidates.Count == 0 || query.Duration <= TimeSpan.Zero || IsSpellingMatch(query, match) ||
        Edition(query.Title, contentRating: true).Length == 0 && Edition(match.Title, contentRating: true).Length > 0;

    public static bool ConflictingCandidates(LyricsQuery query, LyricsMatchInfo first, LyricsMatchInfo other)
    {
        if (query.ArtistCandidates.Count == 0 && first.Artist.Length > 0 && other.Artist.Length > 0 &&
            !first.ArtistAliases.Prepend(first.Artist).Any(left =>
                other.ArtistAliases.Prepend(other.Artist).Any(right => AreArtistCreditsCompatible(left, right)))) return true;
        if (query.Duration <= TimeSpan.Zero && first.DurationSeconds > 0 && other.DurationSeconds > 0 &&
            Math.Abs(first.DurationSeconds - other.DurationSeconds) > 2) return true;
        if (IsSpellingMatch(query, first) && IsSpellingMatch(query, other) && !AreTitlesEquivalent(first.Title, other.Title)) return true;
        var rating = Edition(first.Title, contentRating: true);
        var otherRating = Edition(other.Title, contentRating: true);
        return rating.Length > 0 && otherRating.Length > 0 && rating != otherRating;
    }

    private static bool AdjacentTitleTransposition(string left, string right)
    {
        var a = ComparableTitle(left);
        var b = ComparableTitle(right);
        if (a.Length != b.Length || a.Length is < 8 or > 256) return false;
        var first = 0;
        while (first < a.Length && a[first] == b[first]) first++;
        return first + 1 < a.Length && char.IsLetter(a[first]) && char.IsLetter(a[first + 1]) &&
            a[first] == b[first + 1] && a[first + 1] == b[first] &&
            a.AsSpan(first + 2).SequenceEqual(b.AsSpan(first + 2));
    }

    private static string Edition(string title, bool contentRating)
    {
        foreach (Match edition in ReleaseEdition.Matches(Limit(title)))
        {
            var kind = Normalize(edition.Groups["kind"].Value);
            if (contentRating == (kind is "clean" or "explicit")) return kind;
        }
        return string.Empty;
    }

    // The base title may have a declared translation. A venue or remix credit does
    // not automatically translate with it, even when both titles merely say "live".
    private static string QualifiedRecordingSuffix(string value)
    {
        var title = SearchTitle(value).Normalize(NormalizationForm.FormKC);
        var kind = DisambiguatingVersionWords.Match(title);
        if (!kind.Success || kind.Index == 0) return string.Empty;
        var opening = title.LastIndexOfAny(['(', '[', '【', '–', '—'], kind.Index);
        var genericSuffix = RecordingSuffix.Match(title);
        if (genericSuffix.Success && (opening < 0 || genericSuffix.Index <= opening + 1)) return string.Empty;
        return ArtistCreditOrthography.Fold(title[(opening < 0 ? kind.Index : opening)..]);
    }

    public static bool HasVersionConflict(string requested, string candidate)
    {
        var requestedEdition = Edition(requested, contentRating: true);
        var candidateEdition = Edition(candidate, contentRating: true);
        if (requestedEdition.Length > 0 && candidateEdition.Length > 0 &&
            requestedEdition != candidateEdition) return true;
        var requestedLanguage = LanguageVersion(requested);
        var candidateLanguage = LanguageVersion(candidate);
        if (candidateLanguage is not null && requestedLanguage != candidateLanguage)
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

        return !requestedLabels.SetEquals(candidateLabels);
    }

    private static string? LanguageVersion(string value)
    {
        var title = DisplayDecoration.Replace(PlayerSuffix.Replace(Limit(value), string.Empty), string.Empty);
        var match = LanguageDecoration.Match(title);
        if (!match.Success)
        {
            match = EnglishLanguageDecoration.Match(title);
            var language = match.Groups[1].Value + match.Groups[2].Value;
            return language.ToLowerInvariant() switch
            {
                "english" => "en", "japanese" => "ja", "chinese" or "mandarin" => "zh-Hans",
                "cantonese" => "yue", "korean" => "ko", _ => null,
            };
        }
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
        var title = SearchTitle(value).Normalize(NormalizationForm.FormKC);
        var suffix = RecordingSuffix.Match(title);
        if (suffix.Success && suffix.Index > 0)
        {
            labels.Add(CanonicalRecordingKind(suffix.Groups["kind"].Value));
            if (suffix.Groups["before"].Success) labels.Add(suffix.Groups["before"].Value);
            if (suffix.Groups["after"].Success) labels.Add(suffix.Groups["after"].Value);
            title = title[..suffix.Index];
        }
        foreach (Match word in DisambiguatingVersionWords.Matches(title))
            labels.Add(CanonicalRecordingKind(word.Value));
        return labels;
    }

    private static string CanonicalRecordingKind(string value) => value switch
    {
        "现场" or "現場" => "live",
        "混音" => "remix",
        "伴奏" => "instrumental",
        "翻唱" => "cover",
        "不插电" or "不插電" => "acoustic",
        _ => Normalize(value),
    };

    private static string Limit(string value) => string.Concat(value.EnumerateRunes().Take(MaximumMetadataCharacters).Select(rune => rune.ToString()));
}
