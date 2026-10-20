import Foundation
import DropSpaceCore

public struct NetEaseLyricsProvider: LyricsSource {
    public let kind = LyricsProvider.netEase
    private let responses: NetEaseResponseCache
    public init(http: any LyricsTransport) { self.responses = NetEaseResponseCache(http: http) }
    public func clearResponseCache() async { await responses.clear() }
    public func query(_ query: LyricsQuery) async throws -> LyricsDocument {
        try await self.query(query, progress: .init())
    }
    public func query(_ query: LyricsQuery, progress: LyricsSourceProgress) async throws -> LyricsDocument {
        var attempted = Set<String>(), remaining = 3
        var failure: (any Error)?
        var best = LyricsDocument(lines: [], provider: kind)
        for terms in LyricsMatcher.searchTerms(query) {
            if remaining == 0 { break }
            let search = try await responses.get(LyricsHTTP.url("https://music.163.com/api/search/get/web", [("s", terms), ("type", "1"), ("offset", "0"), ("total", "true"), ("limit", "30")]), refresh: query.bypassProviderResponseCache, target: query.preferredTranslationLanguage)
            try Self.validate(search)
            if !search["result"].isNull && !search["result"].isObject { throw DropSpaceError.invalid("NetEase search returned an unsupported result shape") }
            var songs = search["result"]["songs"].array
            if songs.isEmpty { songs = search["songs"].array }
            var candidates: [LyricsMatch] = []
            for song in songs {
                try Task.checkCancellation()
                let id = song["id"].text, title = song["name"].text
                guard !id.isEmpty, !title.isEmpty, !LyricsMatcher.hasVersionConflict(query.title, title) else { continue }
                var artists = song["artists"].array; if artists.isEmpty { artists = song["ar"].array }
                let artist = artists.map { $0["name"].text }.filter { !$0.isEmpty }.joined(separator: "; ")
                let aliases = LyricsMatcher.artistCandidates(query).prefix(4).map { requested in
                    artists.map { item in
                        let canonical = item["name"].text
                        let alternatives = [canonical] + Self.strings(item, ["alias", "alia", "transNames", "tns"])
                        return alternatives.prefix(65).first { LyricsMatcher.areArtistCreditsCompatible(requested, $0) } ?? canonical
                    }.joined(separator: "; ")
                }.filter { !$0.isEmpty }
                var album = song["album"]["name"].text; if album.isEmpty { album = song["al"]["name"].text }
                let duration = (song["duration"].number > 0 ? song["duration"].number : song["dt"].number) / 1000
                let score = LyricsMatcher.score(query, title: title, artist: artist, album: album, duration: duration, artistAliases: aliases)
                if score >= 4, attempted.insert(id).inserted {
                    var match = LyricsMatch(title: title, artist: artist, album: album, duration: duration, score: score, candidateID: id)
                    match.artistAliases = aliases; match.canonicalTitle = title; match.titleAliases = Self.strings(song, ["alias", "alia", "transNames", "tns"]).filter { $0 != title }
                    candidates.append(match)
                }
            }
            let ordered = candidates.enumerated().sorted { $0.element.score == $1.element.score ? $0.offset < $1.offset : $0.element.score > $1.element.score }.map(\.element)
            for candidate in ordered {
                if remaining == 0 { break }; remaining -= 1
                do {
                    let response = try await responses.get(LyricsHTTP.url("https://music.163.com/api/song/lyric", [("id", candidate.candidateID!), ("lv", "1"), ("kv", "1"), ("tv", "-1"), ("yv", "1"), ("ytv", "1")]), refresh: query.bypassProviderResponseCache, target: query.preferredTranslationLanguage)
                    try Self.validate(response)
                    let document = try await progress.prepare(Self.parse(response).binding(candidate, to: query), query: query)
                    if !document.lines.isEmpty {
                        if !LyricsSelection.needsLookup(document, target: query.preferredTranslationLanguage) { return document }
                        best = LyricsSelection.choose(best, document, target: query.preferredTranslationLanguage, primary: kind, backup: nil)
                    }
                } catch { if !LyricsCandidateFailure.canContinue(error) { throw error }; failure = error }
            }
        }
        if let failure { throw failure }; return best
    }
    public static func parse(_ response: LyricsJSON) throws -> LyricsDocument {
        let yrc = response["yrc"]["lyric"].text, lrc = response["lrc"]["lyric"].text
        let yrcTranslation = response["ytlrc"]["lyric"].text, lrcTranslation = response["tlyric"]["lyric"].text
        let hasYrc = !yrc.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        var document = try LyricsParser.parse(hasYrc ? yrc : lrc, provider: .netEase, secondary: hasYrc ? yrcTranslation : lrcTranslation)
        func translated(_ doc: LyricsDocument) -> Bool { doc.lines.contains { $0.translationOrigin == .provider && !LyricsTextPolicy.isCredit($0.text) && !($0.secondary ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } }
        if hasYrc, !lrc.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, document.lines.isEmpty || !translated(document) {
            let paired = try LyricsParser.parse(lrc, provider: .netEase, secondary: lrcTranslation)
            if document.lines.isEmpty || translated(paired) { document = paired }
        }
        document.providerDataRevision = 3; return document
    }
    private static func strings(_ value: LyricsJSON, _ fields: [String]) -> [String] {
        fields.flatMap { field in value[field].array.prefix(16).compactMap { item in
            if case .string(let text) = item, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return text }; return nil
        } }
    }
    private static func validate(_ root: LyricsJSON) throws {
        guard root.isObject else { throw DropSpaceError.invalid("Unsupported NetEase response") }
        let code = root["code"].number
        if code > 0 && code != 200 { throw DropSpaceError.unavailable("NetEase rejected the request (\(Int(code)))") }
    }
}
