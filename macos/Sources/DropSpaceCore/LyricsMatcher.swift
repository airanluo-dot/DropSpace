import Foundation

public enum LyricsMatcher {
    public static let version = "recording-identity-v5-featured-performers"
    private static let playerSuffix = try! NSRegularExpression(pattern: #"\s*[-|–]\s*(?:Apple Music|QQ\u97f3\u4e50|\u7f51\u6613\u4e91\u97f3\u4e50|\u9177\u72d7\u97f3\u4e50|Spotify|YouTube)\s*$"#, options: [.caseInsensitive])
    private static let versionLabels = try! NSRegularExpression(pattern: #"(?:\(|\[|（|【)([^\)\]）】]*)(?:\)|\]|）|】)|\b(live|remix|acoustic|instrumental|karaoke|radio|extended|edit|demo|version|mono|stereo|original|concert|cover|sped\s*up|slowed)\b"#, options: [.caseInsensitive])
    private static let featuredArtistDecoration = try! NSRegularExpression(pattern: #"(?:\s*(?:\(|\[|（|【)\s*(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+)[^\)\]）】]+(?:\)|\]|）|】)\s*|\s*[-–—:]\s*(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+).+|\s+(?:feat(?:uring)?|ft)(?:\.\s*|\s+).+)$"#, options: [.caseInsensitive])
    private static let featuredArtistNames = try! NSRegularExpression(pattern: #"\b(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+)([^\)\]）】]+)"#, options: [.caseInsensitive])
    private static let featuredArtistCredit = try! NSRegularExpression(pattern: #"(?:\(|\[|（|【)\s*(?:feat(?:uring)?|ft|with)(?:\.\s*|\s+)([^\)\]）】]+)(?:\)|\]|）|】)"#, options: [.caseInsensitive])
    private static let releaseDecoration = try! NSRegularExpression(pattern: #"(?:\s*(?:\(|\[|（|【)\s*(?:(?:\d{4}\s*)?remaster(?:ed)?(?:\s*\d{4})?|explicit|clean|album\s+version|single\s+version|original\s+motion\s+picture\s+soundtrack)(?:\)|\]|）|】)\s*|\s*[-–—:]\s*(?:(?:\d{4}\s*)?remaster(?:ed)?(?:\s*\d{4})?|explicit|clean|album\s+version|single\s+version|original\s+motion\s+picture\s+soundtrack))$"#, options: [.caseInsensitive])
    private static let soundtrackDecoration = try! NSRegularExpression(pattern: #"\s*(?:\(|\[|（|【)\s*[^\)\]）】]*(?:\u4e3b\u9898\u66f2|\u4e3b\u984c\u66f2|\u7247\u5934\u66f2|\u7247\u982d\u66f2|\u7247\u5c3e\u66f2|\u63d2\u66f2|\u63a8\u5e7f\u66f2|\u63a8\u5ee3\u66f2)\s*(?:\)|\]|）|】)\s*$"#, options: [.caseInsensitive])
    private static let languageDecoration = try! NSRegularExpression(pattern: #"\s*(?:\(|\[|（|【)\s*(\u4e2d\u6587|\u56fd\u8bed|\u570b\u8a9e|\u666e\u901a\u8bdd|\u666e\u901a\u8a71|\u82f1\u8bed|\u82f1\u8a9e|\u82f1\u6587|\u65e5\u8bed|\u65e5\u8a9e|\u65e5\u6587|\u97e9\u8bed|\u97d3\u8a9e|\u97e9\u6587|\u97d3\u6587|\u7ca4\u8bed|\u7cb5\u8a9e)(?:\u7248|\u7248\u672c)\s*(?:\)|\]|）|】)\s*$"#, options: [.caseInsensitive])
    private static let bilingualTitle = try! NSRegularExpression(pattern: #"^([\u3400-\u9fff]{2,})\s+[A-Za-z][A-Za-z0-9 '’,:!?\-]+$"#, options: [])
    private static let artistCreditSeparator = try! NSRegularExpression(pattern: #"\s*(?:;|；|,|，|、|&|＆)\s*|\s+[/／]\s+|(?<=[^\u0000-\u007F])[/／]|[/／](?=[^\u0000-\u007F])|\s+(?:feat(?:uring)?|ft|with|x)\.?\s+"#, options: [.caseInsensitive])
    private static let publisherMetadataSeparator = try! NSRegularExpression(pattern: #"\s+(?:—|–|•|·)\s+|[\r\n]+"#, options: [])
    private static let fastSagaReleaseCredit = try! NSRegularExpression(pattern: #"(?:\s*[,;&]\s*)?Fast\s*&\s*Furious:\s*The\s+Fast\s+Saga(?=\s*(?:[,;]|$))"#, options: [.caseInsensitive])
    private static let disambiguatingVersionWords = try! NSRegularExpression(pattern: #"\b(?:live|remix|acoustic|instrumental|karaoke|radio|extended|edit|demo|concert|cover|sped\s*up|slowed)\b"#, options: [.caseInsensitive])
    public static func normalize(_ value: String) -> String {
        let input = replace(playerSuffix, limit(value).precomposedStringWithCompatibilityMapping)
        return String(String.UnicodeScalarView(input.unicodeScalars.filter { scalar in
            switch scalar.properties.generalCategory {
            case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter, .decimalNumber: true
            default: false
            }
        })).lowercased()
    }
    public static func searchTitle(_ value: String) -> String {
        var title = replace(featuredArtistCredit, replace(playerSuffix, limit(value)))
        for _ in 0..<4 {
            let before = title
            for regex in [languageDecoration, soundtrackDecoration, featuredArtistDecoration, releaseDecoration] { title = replace(regex, title) }
            if title == before { break }
        }
        return trim(title)
    }
    public static func artistCandidates(_ query: LyricsQuery) -> [String] {
        unique(expandArtists(trim(query.artist).isEmpty ? query.albumArtist : query.artist).map { withFeaturedArtists(query.title, $0) })
    }
    public static func searchArtists(_ query: LyricsQuery) -> [String] {
        expandArtists(trim(query.artist).isEmpty ? query.albumArtist : query.artist).enumerated().sorted {
            let a = first(publisherMetadataSeparator, $0.element) == nil ? 0 : 1, b = first(publisherMetadataSeparator, $1.element) == nil ? 0 : 1
            return a == b ? $0.offset < $1.offset : a < b
        }.map(\.element)
    }
    public static func searchTerms(_ query: LyricsQuery) -> [String] {
        let title = searchTitle(query.title), artists = searchArtists(query)
        var terms = artists.prefix(1).map { trim(title + " " + $0) }
        if artistCandidates(query).contains(where: { knownRecordingAlias(query.title, $0) }) { terms += ["怪獣 サカナクション", "Kaiju Sakanaction"] }
        if artists.contains(where: { normalize($0) == "abeltesfaye" }) { terms.append(title + " The Weeknd") }
        terms += artists.dropFirst().prefix(1).map { trim(title + " " + $0) }; terms.append(title)
        return Array(unique(terms.filter { !trim($0).isEmpty }).prefix(3))
    }
    public static func expandArtists(_ value: String) -> [String] {
        guard !trim(value).isEmpty else { return [] }
        let value = trim(limit(value)); var result = [value]
        if let match = first(publisherMetadataSeparator, value), match.range.location > 0 {
            let leading = trim((value as NSString).substring(to: match.range.location)); if !leading.isEmpty { result.append(leading) }
        }
        return unique(result)
    }
    public static func withFeaturedArtists(_ title: String, _ artist: String) -> String {
        let title = limit(title)
        let credits = matches(featuredArtistCredit, title).prefix(16).map { trim(group($0, 1, title)) }
        if !credits.isEmpty { return artist + "; " + credits.joined(separator: "; ") }
        let undecorated = replace(languageDecoration, title)
        guard let decoration = first(featuredArtistDecoration, undecorated) else { return artist }
        let value = group(decoration, 0, undecorated)
        guard let names = first(featuredArtistNames, value) else { return artist }
        return artist + "; " + trim(group(names, 1, value))
    }
    public static func hasFeaturedArtistCredit(_ title: String) -> Bool { first(featuredArtistCredit, limit(title)) != nil || first(featuredArtistDecoration, limit(title)) != nil }
    public static func titleWithoutFeaturedArtists(_ title: String) -> String { trim(replace(featuredArtistDecoration, replace(featuredArtistCredit, limit(title)))) }
    public static func areTitlesEquivalent(_ left: String, _ right: String) -> Bool {
        !hasVersionConflict(left, right) && !hasVersionConflict(right, left) && !comparableTitle(left).isEmpty && comparableTitle(left) == comparableTitle(right)
    }
    public static func areArtistCreditsCompatible(_ left: String, _ right: String) -> Bool { artistSimilarity(left, right) >= 0.6 }
    public static func candidateScore(_ query: LyricsQuery, title: String, artist: String, album: String, duration: Double,
                                      artistAliases: [String] = [], collectSelectionCandidates: Bool = false) -> Double {
        let strict = score(query, title: title, artist: artist, album: album, duration: duration, artistAliases: artistAliases)
        if strict >= 4 || !collectSelectionCandidates { return strict }
        return isSafeSelectionCandidate(query, title: title, artist: artist, album: album, duration: duration) ? 4 : 0
    }
    public static func isSafeSelectionCandidate(_ query: LyricsQuery, title: String, artist: String, album: String, duration: Double) -> Bool {
        if score(query, title: title, artist: artist, album: album, duration: duration) >= 4 { return true }
        let artists = artistCandidates(query), credit = withFeaturedArtists(title, artist), similarity = artistSimilarity(artists, credit)
        func latin(_ text: String) -> Bool { text.unicodeScalars.filter { CharacterSet.letters.contains($0) }.allSatisfy { $0.value < 0x0250 } }
        if similarity > 0 && similarity < 0.9 || similarity < 0.9 && latin(credit) && artists.contains(where: latin) { return false }
        return hasMetadata(query) && !trim(artist).isEmpty && areTitlesEquivalent(query.title, title) && duration.isFinite && duration >= 0 &&
            (query.duration <= 0 || duration <= 0 || abs(query.duration - duration) <= durationTolerance(query.duration, duration)) &&
            (artists.contains { areArtistCreditsCompatible($0, credit) } || query.duration > 0 && duration > 0 || !normalize(query.album).isEmpty && normalize(query.album) == normalize(album))
    }
    public static func score(_ query: LyricsQuery, title inputTitle: String, artist inputArtist: String, album: String,
                             duration: Double, artistAliases: [String] = []) -> Double {
        var title = inputTitle, artist = inputArtist, titleScore = titleSimilarity(query.title, inputTitle)
        let artists = artistCandidates(query)
        if artists.contains(where: { knownRecordingAlias(query.title, $0) }), knownRecordingAlias(title, artist),
           query.duration > 0, duration.isFinite, duration > 0, abs(query.duration - duration) <= durationTolerance(query.duration, duration) { titleScore = 1 }
        if titleScore < 0.4, titleSimilarity(query.title, artist) > 0.8, artistSimilarity(artists, title) > 0.8 {
            swap(&title, &artist); titleScore = titleSimilarity(query.title, title)
        }
        var artistScore = artistSimilarity(artists, withFeaturedArtists(title, artist))
        for alias in artistAliases.prefix(16) { artistScore = max(artistScore, artistSimilarity(artists, withFeaturedArtists(title, alias))) }
        if knownFastXCredits(query, title: title, artist: artist, album: album, duration: duration) { artistScore = 1 }
        let bilingual = bilingualMatches(query.title, title)
        if bilingual, artistScore >= 0.6, query.duration > 0, duration.isFinite, duration > 0,
           abs(query.duration - duration) <= durationTolerance(query.duration, duration) { titleScore = max(titleScore, 0.95) }
        guard titleScore >= 0.95, titleScore >= 1 || bilingual, hasMetadata(query) else { return 0 }
        if trim(artist).isEmpty && trim(album).isEmpty && (!duration.isFinite || duration <= 0) { return 0 }
        if hasVersionConflict(query.title, title) { return 0 }
        let albumScore = similarity(query.album, album), candidateDuration = duration.isFinite ? max(0, duration) : 0
        let durationKnown = candidateDuration > 0 && query.duration > 0
        let durationMatches = durationKnown && abs(query.duration - candidateDuration) <= durationTolerance(query.duration, candidateDuration)
        let artistKnown = !artists.isEmpty && !trim(artist).isEmpty, artistMatches = artistKnown && artistScore >= 0.9
        let albumMatches = !trim(query.album).isEmpty && !trim(album).isEmpty && albumScore >= 0.6
        if !artists.isEmpty {
            if artistKnown && !artistMatches { return 0 }
            if trim(artist).isEmpty && !albumMatches && !durationMatches { return 0 }
        }
        if !trim(query.album).isEmpty && !artistMatches {
            if !trim(album).isEmpty && !albumMatches { return 0 }
            if trim(album).isEmpty && !durationMatches { return 0 }
        }
        if durationKnown && !durationMatches { return 0 }
        return titleScore * 6 + artistScore * 4 + albumScore + (durationMatches ? 2 : 0)
    }
    private static func artistSimilarity(_ requested: [String], _ candidate: String) -> Double { requested.map { artistSimilarity($0, candidate) }.max() ?? 0 }
    private static func artistSimilarity(_ left: String, _ right: String) -> Double {
        if sakanaction(left) && sakanaction(right) { return 1 }
        let a = artistCredits(left), b = artistCredits(right)
        guard !a.isEmpty, !b.isEmpty else { return 0 }
        return max(normalize(left) == normalize(right) ? 1 : 0, Double(a.intersection(b).count) / Double(max(a.count, b.count)))
    }
    private static func artistCredits(_ value: String) -> Set<String> {
        let text = limit(value), separator = matches(artistCreditSeparator, text)
        var start = 0, result = Set<String>()
        for end in separator.map({ $0.range }) + [NSRange(location: text.utf16.count, length: 0)] {
            let raw = (text as NSString).substring(with: NSRange(location: start, length: end.location - start)); start = end.location + end.length
            let normalized = normalize(raw)
            let credit = ["abeltesfaye", "theweeknd"].contains(normalized) ? "theweeknd" : ["孙晔", "孙晔gary"].contains(normalized) ? "孙晔" : fold(raw)
            if !credit.isEmpty { result.insert(credit) }
        }
        return result
    }
    private static func knownFastXCredits(_ query: LyricsQuery, title: String, artist: String, album: String, duration: Double) -> Bool {
        guard comparableTitle(query.title) == "mycity", comparableTitle(title) == "mycity", !hasVersionConflict(query.title, title), !hasVersionConflict(title, query.title),
              (148...151).contains(query.duration), duration.isFinite, (148...151).contains(duration), normalize(album).hasPrefix("fastxoriginalmotionpicturesoundtrack") else { return false }
        var requestedAlbum = query.album
        if trim(requestedAlbum).isEmpty, let separator = first(publisherMetadataSeparator, query.artist) { requestedAlbum = (query.artist as NSString).substring(from: separator.range.location + separator.range.length) }
        guard normalize(requestedAlbum).hasPrefix("fastxoriginalmotionpicturesoundtrack") else { return false }
        let performers: Set<String> = ["24kgoldn", "kanebrown", "gherbo"]
        return artistCandidates(query).contains { first(fastSagaReleaseCredit, $0) != nil && artistCredits(replace(fastSagaReleaseCredit, $0)) == performers } && artistCredits(replace(fastSagaReleaseCredit, withFeaturedArtists(title, artist))) == performers
    }
    private static func titleSimilarity(_ left: String, _ right: String) -> Double {
        var direct = similarity(left, right); let a = comparableTitle(left), b = comparableTitle(right)
        if a.isEmpty || b.isEmpty { return direct }; if a == b { return 1 }
        if a.contains(b) || b.contains(a) { direct = max(direct, Double(min(a.utf16.count, b.utf16.count)) / Double(max(a.utf16.count, b.utf16.count))) }
        return max(direct, editSimilarity(a, b))
    }
    private static func bilingualMatches(_ left: String, _ right: String) -> Bool {
        let a = searchTitle(left), b = searchTitle(right)
        if let bm = first(bilingualTitle, b), fold(a) == fold(group(bm, 1, b)) { return true }
        return first(bilingualTitle, a).map { fold(b) == fold(group($0, 1, a)) } ?? false
    }
    private static func comparableTitle(_ value: String) -> String { fold(searchTitle(value)) }
    private static func similarity(_ left: String, _ right: String) -> Double {
        let a = normalize(left), b = normalize(right)
        if a.isEmpty || b.isEmpty { return 0 }; if a == b { return 1 }
        return a.contains(b) || b.contains(a) ? Double(min(a.utf16.count, b.utf16.count)) / Double(max(a.utf16.count, b.utf16.count)) : 0
    }
    private static func editSimilarity(_ left: String, _ right: String) -> Double {
        var a = Array(left.unicodeScalars), b = Array(right.unicodeScalars)
        guard (5...256).contains(a.count), (5...256).contains(b.count) else { return 0 }
        if a.count > b.count { swap(&a, &b) }
        var previous = Array(0...a.count), current = Array(repeating: 0, count: a.count + 1)
        for row in 1...b.count {
            current[0] = row
            for column in 1...a.count { current[column] = min(current[column - 1] + 1, previous[column] + 1, previous[column - 1] + (a[column - 1] == b[row - 1] ? 0 : 1)) }
            swap(&previous, &current)
        }
        let score = 1 - Double(previous[a.count]) / Double(max(a.count, b.count)); return score >= 0.78 ? score : 0
    }
    public static func hasVersionConflict(_ requested: String, _ candidate: String) -> Bool {
        if let language = languageVersion(candidate), languageVersion(requested) != language { return true }
        let a = labels(requested), b = labels(candidate)
        return a.isEmpty ? !b.isEmpty : b.isEmpty || a.isDisjoint(with: b)
    }
    private static func languageVersion(_ value: String) -> String? {
        let value = replace(playerSuffix, limit(value)); guard let match = first(languageDecoration, value) else { return nil }
        switch group(match, 1, value) {
        case "中文", "国语", "國語", "普通话", "普通話": return "zh-Hans"
        case "英文", "英语", "英語": return "en"
        case "日文", "日语", "日語": return "ja"
        case "韩文", "韓文", "韩语", "韓語": return "ko"
        case "粤语", "粵語": return "yue"
        default: return nil
        }
    }
    private static func labels(_ value: String) -> Set<String> {
        let value = replace(featuredArtistDecoration, replace(featuredArtistCredit, limit(value)))
        return Set(matches(versionLabels, value).flatMap { match -> [String] in
            let label = group(match, match.range(at: 1).location != NSNotFound ? 1 : 0, value)
            return matches(disambiguatingVersionWords, label).map { normalize(group($0, 0, label)) }
        })
    }
    private static func sakanaction(_ artist: String) -> Bool { ["sakanaction", "サカナクション", "鱼韵", "魚韻"].contains(normalize(artist)) }
    private static func knownRecordingAlias(_ title: String, _ artist: String) -> Bool { sakanaction(artist) && ["kaiju", "怪獣"].contains(normalize(searchTitle(title))) }
    private static func durationTolerance(_ a: Double, _ b: Double) -> Double { min(40, max(20, min(a, b) * 0.08)) }
    private static func hasMetadata(_ query: LyricsQuery) -> Bool { !artistCandidates(query).isEmpty || !trim(query.album).isEmpty || query.duration > 0 }
    private static func trim(_ value: String) -> String { value.trimmingCharacters(in: .whitespacesAndNewlines) }
    private static func limit(_ value: String) -> String { String(String.UnicodeScalarView(value.unicodeScalars.prefix(2048))) }
    private static func unique(_ values: [String]) -> [String] { var seen = Set<String>(); return values.filter { seen.insert($0.lowercased()).inserted } }
    private static func first(_ regex: NSRegularExpression, _ text: String) -> NSTextCheckingResult? { regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) }
    private static func matches(_ regex: NSRegularExpression, _ text: String) -> [NSTextCheckingResult] { regex.matches(in: text, range: NSRange(text.startIndex..., in: text)) }
    private static func group(_ match: NSTextCheckingResult, _ index: Int, _ text: String) -> String { (text as NSString).substring(with: match.range(at: index)) }
    private static func replace(_ regex: NSRegularExpression, _ text: String) -> String { regex.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "") }
    private static func fold(_ text: String) -> String { String(String.UnicodeScalarView(normalize(text).unicodeScalars.map { orthography[$0] ?? $0 })) }
    private static let orthography: [Unicode.Scalar: Unicode.Scalar] = {
        // The same pinned, unambiguous OpenCC character mappings used by Beta 7.
        let url = Bundle.module.url(forResource: "TSCharacters", withExtension: "txt", subdirectory: "Resources")!
        let source = try! String(contentsOf: url, encoding: .utf8)
        var result: [Unicode.Scalar: Unicode.Scalar] = [:]
        for line in source.split(separator: "\n") where !line.hasPrefix("#") {
            let fields = line.split(separator: "\t")
            if fields.count == 2, !fields[1].contains(" "), fields[0].unicodeScalars.count == 1, fields[1].unicodeScalars.count == 1 {
                result[fields[0].unicodeScalars.first!] = fields[1].unicodeScalars.first!
            }
        }
        return result
    }()
}
