import Foundation
import DropSpaceCore

public struct KugouLyricsProvider: LyricsSource {
    public let kind = LyricsProvider.kugou
    private let http: any LyricsTransport
    public init(http: any LyricsTransport) { self.http = http }
    public func query(_ query: LyricsQuery) async throws -> LyricsDocument {
        try await self.query(query, progress: .init())
    }
    public func query(_ query: LyricsQuery, progress: LyricsSourceProgress) async throws -> LyricsDocument {
        guard query.duration.isFinite, query.duration < Double(Int64.max / 1000) else { throw DropSpaceError.invalid("Invalid recording duration") }
        var remaining = 3, catalogRemaining = 3, attempted = Set<String>(), hashes = Set<String>()
        var failure: (any Error)?
        var best = LyricsDocument(lines: [], provider: kind)
        func readCandidates(_ root: LyricsJSON, recording: LyricsMatch? = nil) async throws -> LyricsDocument {
            for (candidate, key) in Self.choose(root, query: query, recording: recording) {
                if remaining == 0 { break }
                let id = candidate.candidateID!
                guard attempted.insert(id).inserted else { continue }; remaining -= 1
                do {
                    let parameters = [("ver", "1"), ("client", "pc"), ("id", id), ("accesskey", key)]
                    var document: LyricsDocument
                    do {
                        let reply = try await http.get(LyricsHTTP.url("https://lyrics.kugou.com/download", parameters + [("fmt", "krc"), ("charset", "utf8")]))
                        try Self.validate(reply, field: "content", shape: .text)
                        document = try KugouKRCParser.parse(reply["content"].text)
                    } catch let error as DropSpaceError where Self.canRetryPlain(error) && remaining > 0 {
                        remaining -= 1
                        let reply = try await http.get(LyricsHTTP.url("https://lyrics.kugou.com/download", parameters + [("fmt", "lrc"), ("charset", "utf8")]))
                        try Self.validate(reply, field: "content", shape: .text)
                        let content = reply["content"].text
                        guard content.utf16.count <= 2 * 1024 * 1024, let bytes = Data(base64Encoded: content.filter { !$0.isWhitespace }) else { throw DropSpaceError.invalid("Invalid or oversized Kugou lyric content") }
                        document = try LyricsParser.parse(String(decoding: bytes, as: UTF8.self), provider: kind)
                    }
                    try Task.checkCancellation(); document.providerDataRevision = 1
                    if !document.lines.isEmpty {
                        document = try await progress.prepare(document.binding(candidate, to: query), query: query)
                        if !LyricsSelection.needsLookup(document, target: query.preferredTranslationLanguage) { return document }
                        best = LyricsSelection.choose(best, document, target: query.preferredTranslationLanguage, primary: kind, backup: nil)
                    }
                } catch { if !LyricsCandidateFailure.canContinue(error) { throw error }; failure = error }
            }
            return LyricsDocument(lines: [], provider: kind)
        }
        let direct = try await http.get(LyricsHTTP.url("https://lyrics.kugou.com/search", [("ver", "1"), ("man", "yes"), ("client", "pc"), ("keyword", query.title), ("duration", String(Int64(max(0, query.duration) * 1000)))]))
        try Self.validate(direct, field: "candidates", shape: .array)
        let first = try await readCandidates(direct); if !first.lines.isEmpty { return first }
        for terms in LyricsMatcher.searchTerms(query) {
            if remaining == 0 || catalogRemaining == 0 { break }
            let search = try await http.get(LyricsHTTP.url("https://songsearch.kugou.com/song_search_v2", [("keyword", terms), ("page", "1"), ("pagesize", "20"), ("platform", "WebFilter"), ("filter", "2"), ("iscorrection", "1"), ("privilege_filter", "0")]))
            try Self.validate(search, field: "data", shape: .object, catalog: true)
            guard case .array = search["data"]["lists"] else { throw DropSpaceError.invalid("Unsupported Kugou catalogue list") }
            var matches: [LyricsMatch] = []
            for item in search["data"]["lists"].array {
                let title = Self.catalogTitle(item), artist = item["SingerName"].text, album = item["AlbumName"].text, duration = item["Duration"].number
                let score = LyricsMatcher.score(query, title: title, artist: artist, album: album, duration: duration)
                if score >= 4, !item["FileHash"].text.isEmpty { matches.append(.init(title: title, artist: artist, album: album, duration: duration, score: score, candidateID: item["FileHash"].text)) }
            }
            let ordered = matches.enumerated().sorted { $0.element.score == $1.element.score ? $0.offset < $1.offset : $0.element.score > $1.element.score }.map(\.element)
            for recording in ordered {
                if remaining == 0 || catalogRemaining == 0 { break }
                let hash = recording.candidateID!
                guard hashes.insert(hash.lowercased()).inserted else { continue }; catalogRemaining -= 1
                do {
                    let reply = try await http.get(LyricsHTTP.url("https://lyrics.kugou.com/search", [("ver", "1"), ("man", "yes"), ("client", "pc"), ("hash", hash)]))
                    try Self.validate(reply, field: "candidates", shape: .array)
                    let result = try await readCandidates(reply, recording: recording)
                    if !result.lines.isEmpty { return result }
                } catch { if !LyricsCandidateFailure.canContinue(error) { throw error }; failure = error }
            }
        }
        if let failure { throw failure }; return best
    }
    private static func choose(_ root: LyricsJSON, query: LyricsQuery, recording: LyricsMatch?) -> [(LyricsMatch, String)] {
        var candidates: [(LyricsMatch, String)] = []
        for item in root["candidates"].array {
            let title = item["song"].text, artist = item["singer"].text
            var album = item["album"].text, duration = item["duration"].number / 1000
            if let recording, LyricsMatcher.areTitlesEquivalent(recording.title, title), LyricsMatcher.areArtistCreditsCompatible(recording.artist, artist) {
                if duration <= 0 { duration = recording.duration }
                if album.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { album = recording.album }
            }
            let score = LyricsMatcher.score(query, title: title, artist: artist, album: album, duration: duration)
            if score >= 4, !item["id"].text.isEmpty, !item["accesskey"].text.isEmpty {
                candidates.append((.init(title: title, artist: artist, album: album, duration: duration, score: score, candidateID: item["id"].text), item["accesskey"].text))
            }
        }
        return candidates.enumerated().sorted { $0.element.0.score == $1.element.0.score ? $0.offset < $1.offset : $0.element.0.score > $1.element.0.score }.map(\.element)
    }
    private enum Shape { case array, object, text }
    private static func validate(_ value: LyricsJSON, field: String, shape: Shape, catalog: Bool = false) throws {
        guard value.isObject else { throw DropSpaceError.invalid("Unsupported Kugou response shape") }
        for property in ["error_code", "errcode", "status"] where !value[property].isNull {
            guard case .number = value[property], value[property].number.rounded() == value[property].number else { throw DropSpaceError.invalid("Unsupported Kugou response status") }
            let code = value[property].number
            let success = property == "status" ? code == (catalog ? 1 : 200) : property == "errcode" && !catalog ? [0, 200].contains(code) : code == 0
            if !success { throw DropSpaceError.unavailable("Kugou rejected the request (\(value[property].text))") }
        }
        switch (shape, value[field]) {
        case (.array, .array), (.object, .object), (.text, .string): break
        default: throw DropSpaceError.invalid("Unsupported Kugou response payload")
        }
    }
    private static func canRetryPlain(_ error: DropSpaceError) -> Bool { switch error { case .invalid, .limit: true; default: false } }
    private static func catalogTitle(_ item: LyricsJSON) -> String {
        let title = item["SongName"].text.trimmingCharacters(in: .whitespacesAndNewlines), suffix = item["Suffix"].text.trimmingCharacters(in: .whitespacesAndNewlines)
        return title.isEmpty || suffix.isEmpty || LyricsMatcher.normalize(title).hasSuffix(LyricsMatcher.normalize(suffix)) ? title : title + " " + suffix
    }
}
