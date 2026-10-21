import Foundation
import DropSpaceCore

public actor AMLLLyricsProvider: LyricsSource {
    public nonisolated let kind = LyricsProvider.amll
    private let http: any LyricsTransport
    private var cache: [String: (text: String, bytes: Int, order: Int)] = [:]
    private var cacheBytes = 0, order = 0, generation = 0
    public init(http: any LyricsTransport) { self.http = http }
    public func clearResponseCache() async { cache.removeAll(); cacheBytes = 0; generation += 1 }
    public func query(_ query: LyricsQuery) async throws -> LyricsDocument {
        try await self.query(query, progress: .init())
    }
    public func query(_ query: LyricsQuery, progress: LyricsSourceProgress) async throws -> LyricsDocument {
        try Task.checkCancellation()
        let parameters = [("musicName", LyricsMatcher.searchTitle(query.title)), ("pageSize", "100")]
        let wide = try Self.data(await http.get(LyricsHTTP.url("https://api.amll.dev/v1/lyrics/search", parameters)))
        var candidates = try Self.candidates(wide, query: query)
        if candidates.isEmpty, try Self.hasMore(wide), let artist = LyricsMatcher.searchArtists(query).first, !artist.isEmpty {
            let narrowed = try Self.data(await http.get(LyricsHTTP.url("https://api.amll.dev/v1/lyrics/search", parameters + [("artistName", artist)])))
            candidates = try Self.candidates(narrowed, query: query)
        }
        var failure: (any Error)?
        var original = LyricsDocument(lines: [], provider: kind)
        for candidate in candidates {
            try Task.checkCancellation()
            do {
                let id = candidate.candidateID!, run = generation
                var text: String
                if !query.bypassProviderResponseCache, let saved = cache[id] { order += 1; cache[id] = (saved.text, saved.bytes, order); text = saved.text }
                else {
                    let reply = try Self.data(await http.get(LyricsHTTP.url("https://api.amll.dev/v1/lyrics/get", [("id", id)])))
                    guard reply["id"].text == id, case .string(let value) = reply["lyrics"] else { throw DropSpaceError.invalid("AMLL returned a different ID or unsupported payload") }
                    if !reply["format"].isNull { guard case .string("ttml") = reply["format"] else { throw DropSpaceError.invalid("AMLL returned unsupported lyric format") } }
                    guard value.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("<"), value.range(of: "<tt", options: .caseInsensitive) != nil else { throw DropSpaceError.invalid("AMLL lyric format is not TTML") }
                    text = value
                }
                try Task.checkCancellation()
                let document = try await progress.prepare(LyricsParser.parse(text, provider: kind).binding(candidate, to: query), query: query)
                if !document.lines.isEmpty {
                    store(id, text, generation: run)
                    if !LyricsSelection.needsLookup(document, target: query.preferredTranslationLanguage) { return document }
                    if original.lines.isEmpty { original = document }
                }
            } catch { if !LyricsCandidateFailure.canContinue(error) { throw error }; failure = error }
        }
        if let failure { throw failure }; return original
    }
    private static func data(_ root: LyricsJSON) throws -> LyricsJSON {
        guard root.isObject, case .number = root["status"] else { throw DropSpaceError.invalid("Unsupported AMLL response wrapper") }
        guard root["status"].number == 200 else { throw DropSpaceError.unavailable("AMLL rejected the request (\(root["status"].text))") }
        guard root["data"].isObject else { throw DropSpaceError.invalid("Unsupported AMLL response data") }; return root["data"]
    }
    private static func hasMore(_ value: LyricsJSON) throws -> Bool {
        if value["pagination"].isNull { return false }
        guard value["pagination"].isObject, let more = value["pagination"]["hasMore"].flag else { throw DropSpaceError.invalid("Unsupported AMLL pagination") }
        return more
    }
    private static func candidates(_ value: LyricsJSON, query: LyricsQuery) throws -> [LyricsMatch] {
        guard case .array = value["items"] else { throw DropSpaceError.invalid("Unsupported AMLL catalogue") }
        var matches: [LyricsMatch] = []
        for item in value["items"].array {
            try Task.checkCancellation()
            func strings(_ field: String) -> [String] { item[field].array.prefix(16).compactMap { if case .string(let text) = $0 { return text }; return nil } }
            let titles = strings("musicNames"), artist = strings("artistNames").joined(separator: "; "), album = strings("albumNames").joined(separator: "; ")
            guard LyricsMatcher.artistCandidates(query).contains(where: { LyricsMatcher.areArtistCreditsCompatible($0, artist) }),
                  !titles.contains(where: { LyricsMatcher.hasVersionConflict(query.title, $0) }) else { continue }
            var best: LyricsMatch?
            for title in titles where LyricsMatcher.areTitlesEquivalent(query.title, title) {
                let score = LyricsMatcher.score(query, title: title, artist: artist, album: album, duration: 0)
                if score > (best?.score ?? -1) { best = .init(title: title, artist: artist, album: album, duration: 0, score: score, candidateID: item["id"].text) }
            }
            if let best, best.score >= 4, !item["id"].text.isEmpty { matches.append(best) }
        }
        var seen = Set<String>()
        let ordered = matches.enumerated().sorted { $0.element.score == $1.element.score ? $0.offset < $1.offset : $0.element.score > $1.element.score }.map(\.element)
        return Array(ordered.filter { seen.insert($0.candidateID!).inserted }.prefix(3))
    }
    private func store(_ id: String, _ text: String, generation run: Int) {
        let bytes = text.utf16.count * 2
        guard generation == run, bytes <= 4 * 1024 * 1024 else { return }
        if let old = cache.removeValue(forKey: id) { cacheBytes -= old.bytes }
        while cache.count >= 128 || cacheBytes + bytes > 16 * 1024 * 1024 {
            guard let oldest = cache.min(by: { $0.value.order < $1.value.order }) else { break }
            cacheBytes -= oldest.value.bytes; cache.removeValue(forKey: oldest.key)
        }
        order += 1; cache[id] = (text, bytes, order); cacheBytes += bytes
    }
}
