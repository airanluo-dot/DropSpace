import Foundation
import CryptoKit

public enum LyricsLanguageRole: String, Codable, Sendable { case original, providerTranslation }
public enum LyricsWholeTrackDecision: String, Codable, Sendable { case noContent, originalTarget, providerTarget, allowAI }
public struct LyricsLanguagePrediction: Codable, Equatable, Sendable {
    public var label: String?
    public var score: Double = 0
    public var runnerUpScore: Double = 0
    public var modelIdentity = ""
    public var cacheHit = false
    public var diagnostic: String?
    public init(label: String? = nil, score: Double = 0, runnerUpScore: Double = 0, modelIdentity: String = "", diagnostic: String? = nil) {
        self.label = label; self.score = score; self.runnerUpScore = runnerUpScore; self.modelIdentity = modelIdentity; self.diagnostic = diagnostic
    }
}
public struct LyricsWholeTrackAdmission: Codable, Equatable, Sendable {
    public var decision: LyricsWholeTrackDecision
    public var target: String
    public var sourceIdentity: String
    public var originalRevision: String
    public var translationRevision: String
    public var ruleVersion: String
    public var reason: String
    public var originalPrediction: LyricsLanguagePrediction
    public var translationPrediction: LyricsLanguagePrediction?
    public var generation: UInt64
}
public protocol LyricsLanguageIdentifying: Sendable {
    func prepare(_ document: LyricsDocument, target: String) async throws -> LyricsDocument
}

public enum LyricsLanguagePolicy {
    public static let version = "beta16-whole-track-admission-v1"
    public static let preprocessingVersion = "original-space-sample-v1"
    public static let minimumLetters = 8
    public static let minimumTopScore = 0.60
    public static let minimumScoreMargin = 0.20
    private static let timestamp = try! NSRegularExpression(pattern: #"(?:\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]|<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>)"#)
    private static let whitespace = try! NSRegularExpression(pattern: #"\s+"#)
    public static func target(_ language: String?) -> String {
        let normalized = LyricsTextPolicy.normalizedLanguage(language) ?? ""
        return normalized.hasPrefix("zh-") ? "zh" : normalized
    }
    public static func isLetter(_ value: Unicode.Scalar) -> Bool {
        switch value.properties.generalCategory {
        case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter: true
        default: false
        }
    }
    private static func physicalLines(_ text: String) -> [String] {
        text.components(separatedBy: CharacterSet(charactersIn: "\r\n\u{85}\u{2028}\u{2029}")).map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
    }
    public static func parts(_ document: LyricsDocument) -> [[String]] {
        var rows = document.lines.map { line in
            physicalLines(line.text).map { timestamp.stringByReplacingMatches(in: $0, range: NSRange($0.startIndex..., in: $0), withTemplate: "").trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty && !LyricsTextPolicy.isCredit($0) }
        }
        if let match = document.match, match.score >= 4, let header = rows.first?.first, header.utf16.count <= 2048 {
            let title = LyricsMatcher.normalize(match.title), artist = LyricsMatcher.normalize(match.artist)
            if !title.isEmpty && !artist.isEmpty {
                for index in header.indices where ["-", "–", "—"].contains(String(header[index])) {
                    let left = LyricsMatcher.normalize(String(header[..<index])), right = LyricsMatcher.normalize(String(header[header.index(after: index)...]))
                    if left == artist && right == title || left == title && right == artist { rows[0].removeFirst(); break }
                }
            }
        }
        return rows
    }
    public static func providerTranslation(_ line: LyricLine) -> LyricsProviderTranslation? {
        if line.translationOrigin == .provider, let text = line.secondary, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return .init(text: text, language: line.translationLanguage, languageIsExplicit: line.translationLanguageIsExplicit)
        }
        return line.originalProviderTranslation
    }
    public static func sample(_ document: LyricsDocument, role: LyricsLanguageRole) -> String {
        let pieces: [String]
        if role == .original { pieces = parts(document).flatMap { $0 } }
        else { pieces = document.lines.filter { !LyricsTextPolicy.isCredit($0.text) }.compactMap(providerTranslation).flatMap { physicalLines($0.text) } }
        let text = pieces.joined(separator: " ")
        return whitespace.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: " ").trimmingCharacters(in: .whitespacesAndNewlines)
    }
    public static func predictionMatches(_ prediction: LyricsLanguagePrediction, sample: String, target language: String) -> Bool {
        sample.unicodeScalars.filter(isLetter).count >= minimumLetters && prediction.score.isFinite && prediction.runnerUpScore.isFinite && prediction.score >= minimumTopScore && prediction.score - prediction.runnerUpScore >= minimumScoreMargin && target(prediction.label?.replacingOccurrences(of: "__label__", with: "")) == target(language)
    }
    public static func trustedProviderTarget(_ document: LyricsDocument, target language: String) -> Bool {
        let normalized = target(language)
        guard !normalized.isEmpty else { return false }
        let text = document.lines.filter { !LyricsTextPolicy.isCredit($0.text) }.compactMap(providerTranslation).filter { $0.languageIsExplicit == true && target($0.language) == normalized }.map(\.text).joined(separator: " ")
        let letters = text.unicodeScalars.filter(isLetter)
        guard !letters.isEmpty else { return false }
        switch normalized {
        case "zh": return letters.contains { (0x3400...0x9fff).contains($0.value) || (0x20000...0x323af).contains($0.value) } && !letters.contains { (0x3040...0x30ff).contains($0.value) }
        case "en": let latin = letters.filter { (0x41...0x24f).contains($0.value) }.count; return latin > 0 && Double(latin) >= Double(letters.count) / 2
        default: return true
        }
    }
    public static func prepare(_ input: LyricsDocument, target language: String, original: LyricsLanguagePrediction,
                               translation: LyricsLanguagePrediction? = nil, generation: UInt64 = 0) -> LyricsDocument {
        var document = input
        let originalSample = sample(document, role: .original), normalized = target(language)
        let predictedProvider = translation.map { predictionMatches($0, sample: sample(document, role: .providerTranslation), target: language) } ?? false
        let decision: LyricsWholeTrackDecision
        if LyricsTextPolicy.quality(document.lines) != .usable || originalSample.isEmpty { decision = .noContent }
        else if predictionMatches(original, sample: originalSample, target: language) { decision = .originalTarget }
        else if trustedProviderTarget(document, target: language) || predictedProvider { decision = .providerTarget }
        else { decision = .allowAI }
        if decision == .providerTarget && predictedProvider {
            for index in document.lines.indices {
                let line = document.lines[index]
                if line.translationOrigin == .provider, !(line.secondary ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                   line.translationLanguageIsExplicit == false || (line.translationLanguage ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    document.lines[index].translationLanguage = normalized; document.lines[index].translationLanguageIsExplicit = false
                }
            }
        }
        let reason: String
        switch decision {
        case .noContent: reason = "no-lyric-body"
        case .originalTarget: reason = "original-target-language"
        case .providerTarget: reason = "provider-target-translation"
        case .allowAI: reason = original.diagnostic.map { "unknown-original-" + $0 } ?? "unknown-original-no-target-provider"
        }
        document.translationAdmission = .init(decision: decision, target: normalized, sourceIdentity: sourceIdentity(document), originalRevision: originalRevision(document), translationRevision: translationRevision(document), ruleVersion: version, reason: reason, originalPrediction: original, translationPrediction: translation, generation: generation)
        return document
    }
    public static func current(_ document: LyricsDocument, target language: String) -> Bool {
        guard let admission = document.translationAdmission else { return false }
        guard ![admission.sourceIdentity, admission.originalRevision, admission.translationRevision].contains("invalid-revision") else { return false }
        return admission.ruleVersion == version && admission.target == target(language) && admission.sourceIdentity == sourceIdentity(document) && admission.originalRevision == originalRevision(document) && admission.translationRevision == translationRevision(document)
    }
    public static func hasMatchingProvider(_ document: LyricsDocument, target language: String) -> Bool {
        trustedProviderTarget(document, target: language) || current(document, target: language) && document.translationAdmission?.decision == .providerTarget
    }
    public static func completeTarget(_ document: LyricsDocument, target language: String) -> Bool {
        if current(document, target: language), document.translationAdmission?.decision == .originalTarget { return true }
        return hasMatchingProvider(document, target: language) && document.lines.filter { !LyricsTextPolicy.isCredit($0.text) && !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.allSatisfy {
            guard let native = providerTranslation($0) else { return false }; return target(native.language) == target(language)
        }
    }
    public static func canTranslate(_ document: LyricsDocument, target language: String) -> Bool {
        current(document, target: language) && document.translationAdmission?.decision == .allowAI && !trustedProviderTarget(document, target: language)
    }
    public static func sourceIdentity(_ document: LyricsDocument) -> String {
        struct Identity: Encodable {
            var provider: LyricsProvider; var candidateID: String?; var trackIdentity: String?; var title: String?
            var artist: String?; var album: String?; var duration: Double?; var canonicalTitle: String?; var revision: Int
        }
        let match = document.match
        return hash(Identity(provider: document.provider, candidateID: match?.candidateID, trackIdentity: match?.trackIdentity, title: match?.title, artist: match?.artist, album: match?.album, duration: match?.duration, canonicalTitle: match?.canonicalTitle, revision: document.providerDataRevision))
    }
    public static func originalRevision(_ document: LyricsDocument) -> String {
        struct Line: Encodable { var occurrence: Int; var text: String; var start: Double; var end: Double; var language: String? }
        return hash(document.lines.enumerated().map { Line(occurrence: $0.offset, text: $0.element.text, start: $0.element.start, end: $0.element.end, language: $0.element.sourceLanguage) })
    }
    public static func translationRevision(_ document: LyricsDocument) -> String {
        struct Line: Encodable { var occurrence: Int; var translation: LyricsProviderTranslation }
        return hash(document.lines.enumerated().compactMap { item in providerTranslation(item.element).map { Line(occurrence: item.offset, translation: $0) } })
    }
    private static func hash<T: Encodable>(_ value: T) -> String {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        guard let bytes = try? encoder.encode(value) else { return "invalid-revision" }
        return SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
    }
}
