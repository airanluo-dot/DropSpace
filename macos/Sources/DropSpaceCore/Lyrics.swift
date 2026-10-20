import Foundation

public struct LyricsQuery: Codable, Equatable, Sendable {
    public var title: String
    public var artist: String
    public var album: String
    public var duration: Double
    public var trackIdentity: String
    public var albumArtist: String
    public var preferredTranslationLanguage: String = ""
    public var bypassProviderResponseCache = false
    public init(title: String, artist: String = "", album: String = "", duration: Double = 0,
                trackIdentity: String = "", albumArtist: String = "") {
        self.title = title; self.artist = artist; self.album = album; self.duration = duration
        self.trackIdentity = trackIdentity; self.albumArtist = albumArtist
    }
}
public enum LyricsBodyQuality: String, Codable, Sendable { case usable, placeholderOnly, noLyrics, confirmedInstrumental, requestFailed }
public enum LyricsTranslationOrigin: String, Codable, Sendable { case none, provider, localAI }
public enum LyricsLineTranslationState: String, Codable, Sendable { case pending, translated, skipped, failed }
public enum LyricsTimeBasis: String, Codable, Sendable { case timed, estimatedEnd, untimed }
public struct LyricsProviderTranslation: Codable, Equatable, Sendable {
    public var text: String
    public var language: String?
    public var languageIsExplicit: Bool?
    public init(text: String, language: String? = nil, languageIsExplicit: Bool? = nil) {
        self.text = text; self.language = language; self.languageIsExplicit = languageIsExplicit
    }
}
public struct LyricWord: Codable, Equatable, Sendable {
    public var text: String
    public var start: Double
    public var end: Double
    public init(_ text: String, start: Double, end: Double) { self.text = text; self.start = start; self.end = end }
}
public struct LyricLine: Codable, Equatable, Sendable {
    public var start: Double
    public var end: Double
    public var text: String
    public var secondary: String?
    public var words: [LyricWord]
    public var sourceLanguage: String?
    public var translationOrigin: LyricsTranslationOrigin = .none
    public var translationLanguage: String?
    public var translationLanguageIsExplicit: Bool?
    public var originalProviderTranslation: LyricsProviderTranslation?
    public var localAIAdmissionKey: String?
    public var translationState: LyricsLineTranslationState?
    public var translationReason: String?
    // Missing in older cached documents: unknown timing provenance, not proof
    // that a synthetic whole-track interval is a real lyric sentence.
    public var timeBasis: LyricsTimeBasis? = .timed
    public init(start: Double, end: Double, text: String, secondary: String? = nil, words: [LyricWord] = []) {
        self.start = start; self.end = end; self.text = text; self.secondary = secondary; self.words = words
    }
}
public struct LyricsMatch: Codable, Equatable, Sendable {
    public var title: String
    public var artist: String
    public var album: String
    public var duration: Double
    public var score: Double
    public var candidateID: String?
    public var trackIdentity: String
    public var artistAliases: [String] = []
    public var canonicalTitle = ""
    public var titleAliases: [String] = []
    public init(title: String, artist: String, album: String, duration: Double, score: Double, candidateID: String? = nil, trackIdentity: String = "") {
        self.title = title; self.artist = artist; self.album = album; self.duration = duration; self.score = score
        self.candidateID = candidateID; self.trackIdentity = trackIdentity
    }
}
public struct LyricsDocument: Codable, Equatable, Sendable {
    public var lines: [LyricLine]
    public var provider: LyricsProvider
    public var match: LyricsMatch?
    public var providerDataRevision = 0
    public var bodyQuality: LyricsBodyQuality
    public var translationAdmission: LyricsWholeTrackAdmission?
    // Like the baseline JsonIgnore contract, admission is session-only. Reading
    // a persistent source document must re-enter the current model and rules.
    private enum CodingKeys: String, CodingKey { case lines, provider, match, providerDataRevision, bodyQuality }
    public init(lines: [LyricLine], provider: LyricsProvider) {
        self.lines = lines; self.provider = provider; bodyQuality = LyricsTextPolicy.quality(lines)
        if bodyQuality != .usable { self.lines = [] }
    }
    public func binding(_ match: LyricsMatch, to query: LyricsQuery) -> LyricsDocument {
        var value = self; value.match = match; value.match?.trackIdentity = query.trackIdentity
        if query.duration > 0, value.lines.count == 1, value.lines[0].start == 0, value.lines[0].end >= 86400 {
            value.lines[0].end = query.duration
            value.lines[0].words = value.lines[0].words.compactMap { word in
                guard query.duration > word.start else { return nil }
                var bounded = word; bounded.end = query.duration; return bounded
            }
        }
        return value
    }
}
public struct LyricsFrame: Equatable, Sendable {
    public var lineIndex: Int?
    public var wordIndex: Int?
    public var wordProgress: Double
}
public enum LyricsTimeline {
    public static func frame(_ document: LyricsDocument, position: Double, delayMilliseconds: Int) -> LyricsFrame {
        let empty = LyricsFrame(lineIndex: nil, wordIndex: nil, wordProgress: 0)
        guard position.isFinite else { return empty }
        let effective = position + Double(min(30000, max(-30000, delayMilliseconds))) / 1000
        var low = 0, high = document.lines.count - 1, found = -1
        while low <= high {
            let mid = low + (high - low) / 2
            if document.lines[mid].start <= effective { found = mid; low = mid + 1 } else { high = mid - 1 }
        }
        guard found >= 0 else { return empty }
        let line = document.lines[found]
        guard line.end <= line.start || effective < line.end else { return empty }
        var result = LyricsFrame(lineIndex: found, wordIndex: nil, wordProgress: 0)
        for (index, word) in line.words.enumerated() {
            if word.start > effective { break }
            result.wordIndex = index; result.wordProgress = min(1, max(0, (effective - word.start) / max(0.001, word.end - word.start)))
        }
        return result
    }
}
