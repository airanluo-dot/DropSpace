import Foundation
import CryptoKit

public struct PlainHyOutput: Codable, Equatable, Sendable {
    public var id: Int
    public var text: String
    public init(id: Int, text: String) { self.id = id; self.text = text }
}
public enum PlainHyLyrics {
    public static let version = "official-plain-per-line-v1"
    public static let mappingVersion = "host-mapped-id-text-v1"
    public static let acceptanceVersion = "unknown-copy-neutral-complete-song-v1"
    public static let sampler = "seed42-temp0.1-topk20-topp0.8-minp0.05-repeat1-frequency0-presence0-t4-tb4-c4096-n2048"
    public static let maximumPromptBytes = 1800, maximumLineBytes = 16384, maximumSongOutputBytes = 65536
    public static let wholeSongSeconds = 600
    public static func prompt(_ source: String, target: String) throws -> String {
        let name: String
        switch LyricsTextPolicy.normalizedLanguage(target) {
        case "en": name = "英语"
        case "zh-Hans": name = "简体中文"
        default: throw DropSpaceError.invalid("Unsupported plaintext translation target")
        }
        guard !trim(source).isEmpty else { throw DropSpaceError.invalid("Empty translation source") }
        let prompt = "将以下文本翻译为\(name)，注意只需要输出翻译后的结果，不要额外解释：\n" + source
        guard prompt.utf8.count <= maximumPromptBytes else { throw DropSpaceError.limit("Lyric line exceeds plaintext context budget") }; return prompt
    }
    public static func removeRuntimeTerminator(_ value: String) -> String {
        let text = trim(value), marker = "[end of text]"
        return text.hasSuffix(marker) ? trim(String(text.dropLast(marker.count))) : text
    }
    public static func completeLine(_ text: String) -> Bool {
        !trim(text).isEmpty && text.utf8.count <= maximumLineBytes && text.utf16.count <= 4096 &&
            !text.unicodeScalars.contains { $0.properties.generalCategory == .control && $0 != "\t" } && !text.contains("```") && !text.contains("\u{fffd}")
    }
    public static func validTranslation(_ text: String) -> Bool {
        guard !trim(text).isEmpty, text.utf16.count <= 4096,
              !text.unicodeScalars.contains(where: { $0.properties.generalCategory == .control && $0 != "\t" }),
              !text.contains("\u{fffd}"), !text.contains("[end of text]"), !text.contains("<|im_start|>"), !text.contains("<|im_end|>") else { return false }
        return !text.lowercased().contains("translateids:") && !text.lowercased().contains("\"translateids\"")
    }
    public static func eligible(_ document: LyricsDocument, target: String) -> [Int] {
        guard LyricsLanguagePolicy.canTranslate(document, target: target), (1...500).contains(document.lines.count) else { return [] }
        let parts = LyricsLanguagePolicy.parts(document)
        guard parts.reduce(0, { $0 + $1.count }) <= 500 else { return [] }
        return parts.indices.filter { !parts[$0].isEmpty }
    }
    public static func admissionKey(_ document: LyricsDocument, index: Int, target: String) throws -> String {
        guard document.lines.indices.contains(index) else { throw DropSpaceError.invalid("Lyric occurrence") }
        struct Identity: Encodable {
            var version = LyricsLanguagePolicy.version; var source: String; var original: String; var native: String
            var occurrence: Int; var start: Double; var end: Double; var text: String; var sourceLanguage: String?; var target: String; var segments: [String]
        }
        let line = document.lines[index]
        return try hash(Identity(source: LyricsLanguagePolicy.sourceIdentity(document), original: LyricsLanguagePolicy.originalRevision(document),
            native: LyricsLanguagePolicy.translationRevision(document), occurrence: index, start: line.start, end: line.end, text: line.text,
            sourceLanguage: line.sourceLanguage, target: LyricsLanguagePolicy.target(target), segments: LyricsLanguagePolicy.parts(document)[index]))
    }
    public static func removeIneligible(_ input: LyricsDocument, target: String) -> LyricsDocument {
        var document = input
        let parts = LyricsLanguagePolicy.canTranslate(input, target: target) ? LyricsLanguagePolicy.parts(input) : input.lines.map { _ in [] }
        for index in document.lines.indices {
            let line = document.lines[index]
            guard line.translationOrigin == .localAI else { continue }
            if parts[index].isEmpty || LyricsLanguagePolicy.target(line.translationLanguage) != LyricsLanguagePolicy.target(target) ||
                line.localAIAdmissionKey != (try? admissionKey(input, index: index, target: target)) { document.lines[index] = restoreNative(line) }
        }
        return document
    }
    public static func markStates(_ input: LyricsDocument, target: String) -> LyricsDocument {
        let source = removeIneligible(input, target: target); var document = source
        let admitted = Set(eligible(source, target: target))
        for index in document.lines.indices {
            let line = source.lines[index]
            let provider = hasTargetProvider(line, target: target) || LyricsLanguagePolicy.current(source, target: target) &&
                source.translationAdmission?.decision == .providerTarget && line.translationOrigin == .provider && !trim(line.secondary ?? "").isEmpty
            let local = admitted.contains(index) && line.translationOrigin == .localAI && !trim(line.secondary ?? "").isEmpty &&
                LyricsLanguagePolicy.target(line.translationLanguage) == LyricsLanguagePolicy.target(target) && line.localAIAdmissionKey == (try? admissionKey(source, index: index, target: target))
            document.lines[index].translationState = provider || local ? .translated : admitted.contains(index) ? .pending : .skipped
            document.lines[index].translationReason = provider ? "provider-target-translation" : local ? "local-ai-complete" :
                admitted.contains(index) ? "awaiting-translation" : LyricsTextPolicy.isCredit(line.text) ? "credit" : source.translationAdmission?.reason ?? "whole-track-not-prepared"
        }
        return document
    }
    public static func apply(_ values: [PlainHyOutput], to source: LyricsDocument, indices: [Int], target: String) throws -> LyricsDocument {
        guard LyricsLanguagePolicy.canTranslate(source, target: target), (1...500).contains(indices.count), Set(indices).count == indices.count,
              indices.allSatisfy({ source.lines.indices.contains($0) }), values.map(\.id) == indices,
              values.allSatisfy({ validTranslation($0.text) }), try JSONEncoder().encode(values).count <= maximumSongOutputBytes else { throw DropSpaceError.invalid("Invalid mapped translation output") }
        var result = source; let parts = LyricsLanguagePolicy.parts(source)
        for value in values {
            let index = value.id; var line = source.lines[index]
            if hasTargetProvider(line, target: target) { continue }
            let copied = exact(trim(line.text), trim(value.text)) || exact(trim(parts[index].joined(separator: " ")), trim(value.text))
            if copied {
                if line.translationOrigin == .localAI { line = restoreNative(line) }
                line.translationState = .skipped; line.translationReason = "copied-source-output"; line.localAIAdmissionKey = nil
            } else {
                line.originalProviderTranslation = LyricsLanguagePolicy.providerTranslation(line)
                line.secondary = value.text; line.translationOrigin = .localAI; line.translationLanguage = target
                line.translationLanguageIsExplicit = false; line.localAIAdmissionKey = try admissionKey(source, index: index, target: target)
                line.translationState = .translated; line.translationReason = "local-ai-complete"
            }
            result.lines[index] = line
        }
        return result
    }
    public static func reconcile(current: LyricsDocument, source: LyricsDocument, generated: LyricsDocument, target: String) -> LyricsDocument {
        guard LyricsLanguagePolicy.sourceIdentity(current) == LyricsLanguagePolicy.sourceIdentity(source),
              LyricsLanguagePolicy.originalRevision(current) == LyricsLanguagePolicy.originalRevision(source),
              LyricsLanguagePolicy.originalRevision(generated) == LyricsLanguagePolicy.originalRevision(source),
              LyricsLanguagePolicy.sourceIdentity(generated) == LyricsLanguagePolicy.sourceIdentity(source),
              LyricsLanguagePolicy.translationRevision(current) == LyricsLanguagePolicy.translationRevision(source),
              generated.lines.count == source.lines.count else { return removeIneligible(current, target: target) }
        var result = markStates(current, target: target)
        guard LyricsLanguagePolicy.canTranslate(result, target: target) else { return removeIneligible(result, target: target) }
        let parts = LyricsLanguagePolicy.parts(result)
        for index in result.lines.indices {
            let output = generated.lines[index], line = result.lines[index]
            guard !parts[index].isEmpty, !hasTargetProvider(line, target: target) else { continue }
            if output.translationOrigin == .localAI, !trim(output.secondary ?? "").isEmpty,
               LyricsLanguagePolicy.target(output.translationLanguage) == LyricsLanguagePolicy.target(target),
               let key = try? admissionKey(result, index: index, target: target), output.localAIAdmissionKey == key {
                result.lines[index].secondary = output.secondary
                result.lines[index].translationOrigin = output.translationOrigin
                result.lines[index].translationLanguage = output.translationLanguage
                result.lines[index].translationLanguageIsExplicit = false
                result.lines[index].originalProviderTranslation = output.originalProviderTranslation
                result.lines[index].localAIAdmissionKey = output.localAIAdmissionKey
                result.lines[index].translationState = output.translationState
                result.lines[index].translationReason = output.translationReason
            } else if output.translationState == .failed || output.translationState == .skipped, line.translationOrigin != .localAI {
                result.lines[index].translationState = output.translationState; result.lines[index].translationReason = output.translationReason
            }
        }
        return result
    }
    public static func useful(_ document: LyricsDocument, target: String) -> Bool {
        document.lines.contains { $0.translationOrigin == .localAI && !trim($0.secondary ?? "").isEmpty &&
            LyricsLanguagePolicy.target($0.translationLanguage) == LyricsLanguagePolicy.target(target) && !exact(trim($0.text), trim($0.secondary ?? "")) }
    }
    public static func nextLine(_ document: LyricsDocument, pending: Set<Int>, position: Double) -> Int? {
        let current = document.lines.lastIndex { $0.start <= position }
        if let current, pending.contains(current), document.lines[current].end <= document.lines[current].start || position < document.lines[current].end { return current }
        return pending.sorted { left, right in
            let a = document.lines[left], b = document.lines[right]
            if (a.start > position) != (b.start > position) { return a.start > position }
            return a.start == b.start ? left < right : a.start < b.start
        }.first
    }
    public static func cacheIdentity(query: LyricsQuery, source: LyricsDocument, target: String, inference: String) throws -> String {
        guard inference.range(of: "^[0-9a-fA-F]{64}$", options: .regularExpression) != nil else { throw DropSpaceError.invalid("Pinned inference identity is required") }
        struct Recording: Encodable { var title: String; var artist: String; var albumArtist: String; var album: String; var trackIdentity: String }
        struct SourceLine: Encodable {
            var text: String; var start: Double; var end: Double; var secondary: String?; var language: String?
            var origin: LyricsTranslationOrigin; var explicit: Bool?
        }
        struct Identity: Encodable {
            var version = "mac-plain-hy-whole-track-v1"; var policy = LyricsLanguagePolicy.version; var protocolVersion = PlainHyLyrics.version
            var query: Recording; var source: String; var original: String; var native: String; var target: String; var inference: String
            var ids: [Int]; var segments: [[String]]; var lines: [SourceLine]
        }
        let recording = Recording(title: query.title, artist: query.artist, albumArtist: query.albumArtist, album: query.album, trackIdentity: query.trackIdentity)
        let lines = source.lines.map { $0.translationOrigin == .localAI ? restoreNative($0) : $0 }.map {
            SourceLine(text: $0.text, start: $0.start, end: $0.end, secondary: $0.secondary, language: $0.translationLanguage, origin: $0.translationOrigin, explicit: $0.translationLanguageIsExplicit)
        }
        return try hash(Identity(query: recording, source: LyricsLanguagePolicy.sourceIdentity(source), original: LyricsLanguagePolicy.originalRevision(source),
            native: LyricsLanguagePolicy.translationRevision(source), target: LyricsLanguagePolicy.target(target), inference: inference,
            ids: eligible(source, target: target), segments: LyricsLanguagePolicy.parts(source), lines: lines))
    }
    private static func restoreNative(_ input: LyricLine) -> LyricLine {
        var line = input; let native = line.originalProviderTranslation
        line.secondary = native?.text; line.translationOrigin = native == nil ? .none : .provider
        line.translationLanguage = native?.language; line.translationLanguageIsExplicit = native?.languageIsExplicit
        line.originalProviderTranslation = nil; line.localAIAdmissionKey = nil; return line
    }
    private static func hasTargetProvider(_ line: LyricLine, target: String) -> Bool {
        guard line.translationOrigin == .provider, line.translationLanguageIsExplicit == true else { return false }
        return LyricsLanguagePolicy.trustedProviderTarget(LyricsDocument(lines: [line], provider: .localLrc), target: target)
    }
    private static func trim(_ value: String) -> String { value.trimmingCharacters(in: .whitespacesAndNewlines) }
    private static func exact(_ left: String, _ right: String) -> Bool { left.utf8.elementsEqual(right.utf8) }
    private static func hash<T: Encodable>(_ value: T) throws -> String {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        return SHA256.hash(data: try encoder.encode(value)).map { String(format: "%02x", $0) }.joined()
    }
}
