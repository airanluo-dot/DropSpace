import Foundation

public enum LyricsSelection {
    public static let online: [LyricsProvider] = [.netEase, .qqMusic, .kugou, .lrclib, .amll]
    public static func usable(_ document: LyricsDocument) -> Bool {
        !document.lines.isEmpty || document.bodyQuality == .confirmedInstrumental && document.match != nil
    }
    public static func validate(_ document: LyricsDocument, query: LyricsQuery) -> LyricsDocument {
        let empty = LyricsDocument(lines: [], provider: document.provider)
        guard var match = document.match, !(match.candidateID ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              query.trackIdentity.isEmpty || match.trackIdentity.isEmpty || query.trackIdentity == match.trackIdentity else { return empty }
        match.score = LyricsMatcher.score(query, title: match.canonicalTitle.isEmpty ? match.title : match.canonicalTitle,
                                         artist: match.artist, album: match.album, duration: match.duration, artistAliases: match.artistAliases)
        guard match.score >= 4 else { return empty }
        var value = document; value.bodyQuality = LyricsTextPolicy.quality(value.lines, confirmedInstrumental: document.bodyQuality == .confirmedInstrumental)
        if value.bodyQuality != .usable { value.lines = [] }
        match.trackIdentity = query.trackIdentity; value.match = match; return value
    }
    public static func target(_ language: String?) -> String {
        LyricsLanguagePolicy.target(language)
    }
    // Script validation applies only to explicitly attributed provider translations.
    // It is never used to guess the original language or authorize local inference.
    public static func trustedTarget(_ document: LyricsDocument, target language: String) -> Bool {
        LyricsLanguagePolicy.hasMatchingProvider(document, target: language)
    }
    public static func completeTarget(_ document: LyricsDocument, target language: String) -> Bool {
        LyricsLanguagePolicy.completeTarget(document, target: language)
    }
    public static func needsLookup(_ document: LyricsDocument, target language: String) -> Bool {
        if document.lines.isEmpty { return document.bodyQuality != .confirmedInstrumental }
        if target(language).isEmpty || trustedTarget(document, target: language) { return false }
        return !LyricsLanguagePolicy.current(document, target: language) || document.translationAdmission?.decision == .allowAI
    }
    public static func sourceRank(_ provider: LyricsProvider, primary: LyricsProvider, backup: LyricsProvider?) -> Int {
        if provider == primary { return 0 }; if provider == backup { return 1 }
        return (online + [.localLrc]).firstIndex(of: provider).map { $0 + 2 } ?? Int.max
    }
    public static func wordCoverage(_ document: LyricsDocument) -> Double {
        var text = 0, timed = 0
        for line in document.lines where !LyricsTextPolicy.isCredit(line.text) && !line.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            // The Windows contract counts UTF-16 non-whitespace code units.
            func count(_ value: String) -> Int { value.unicodeScalars.filter { !CharacterSet.whitespacesAndNewlines.contains($0) }.reduce(0) { $0 + $1.utf16.count } }
            let characters = count(line.text); text += characters
            timed += min(characters, line.words.filter { $0.end > $0.start && $0.start >= line.start && $0.end <= line.end }.reduce(0) { $0 + count($1.text) })
        }
        return text == 0 ? 0 : Double(timed) / Double(text)
    }
    public static func choose(_ current: LyricsDocument, _ candidate: LyricsDocument, target: String, primary: LyricsProvider, backup: LyricsProvider?) -> LyricsDocument {
        guard usable(candidate) else { return current }; guard usable(current) else { return candidate }
        let left = [candidate.lines.isEmpty ? 0 : 1, completeTarget(candidate, target: target) ? 1 : 0, trustedTarget(candidate, target: target) ? 1 : 0, -sourceRank(candidate.provider, primary: primary, backup: backup)]
        let right = [current.lines.isEmpty ? 0 : 1, completeTarget(current, target: target) ? 1 : 0, trustedTarget(current, target: target) ? 1 : 0, -sourceRank(current.provider, primary: primary, backup: backup)]
        for (a, b) in zip(left, right) where a != b { return a > b ? candidate : current }
        let a = wordCoverage(candidate), b = wordCoverage(current)
        if a != b { return a > b ? candidate : current }
        if candidate.match?.score != current.match?.score { return (candidate.match?.score ?? 0) > (current.match?.score ?? 0) ? candidate : current }
        return (candidate.match?.candidateID ?? "").utf16.lexicographicallyPrecedes((current.match?.candidateID ?? "").utf16) ? candidate : current
    }
}
