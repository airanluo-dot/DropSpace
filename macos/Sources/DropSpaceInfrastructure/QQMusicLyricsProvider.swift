import Foundation
import DropSpaceCore

public struct QQMusicLyricsProvider: LyricsSource {
    public let kind = LyricsProvider.qqMusic
    private let http: any LyricsTransport
    private let session: QQMusicSession?
    public init(http: any LyricsTransport, session: QQMusicSession? = nil) { self.http = http; self.session = session }
    public func query(_ query: LyricsQuery) async throws -> LyricsDocument {
        try await self.query(query, progress: .init())
    }
    public func query(_ query: LyricsQuery, progress: LyricsSourceProgress) async throws -> LyricsDocument {
        var attempted = Set<String>(), remaining = 3, failure: (any Error)?
        var best = LyricsDocument(lines: [], provider: kind)
        for terms in LyricsMatcher.searchTerms(query) {
            if remaining == 0 { break }
            let search = try await call(module: "music.search.SearchCgiService", method: "DoSearchForQQMusicDesktop", parameters: ["query": terms, "search_type": 0, "num_per_page": 20, "page_num": 1])
            let songs = try Self.searchSongs(search)
            var candidates: [LyricsMatch] = []
            for song in songs {
                let title = Self.field(song, "name", "songname"), artist = song["singer"].array.map { $0["name"].text }.joined(separator: "; ")
                let album = song["album"]["name"].text.isEmpty ? song["albumname"].text : song["album"]["name"].text
                let duration = song["interval"].number, id = Self.field(song, "mid", "songmid")
                let score = LyricsMatcher.score(query, title: title, artist: artist, album: album, duration: duration)
                var probe = query; probe.title = LyricsMatcher.titleWithoutFeaturedArtists(query.title)
                let needsVocal = LyricsMatcher.hasFeaturedArtistCredit(query.title) && query.duration > 0 && duration > 0 && abs(query.duration - duration) <= 3 && LyricsMatcher.score(probe, title: title, artist: artist, album: album, duration: duration) >= 4
                if score >= 4 || needsVocal { candidates.append(.init(title: title, artist: artist, album: album, duration: duration, score: score, candidateID: id)) }
            }
            let ranked = candidates.enumerated().sorted { $0.element.score == $1.element.score ? $0.offset < $1.offset : $0.element.score > $1.element.score }.map(\.element)
            for var candidate in ranked {
                guard let id = candidate.candidateID, !id.isEmpty, attempted.insert(id).inserted else { continue }
                if remaining == 0 { break }; remaining -= 1
                do {
                    let response = try await call(module: "music.musichallSong.PlayLyricInfo", method: "GetPlayLyricInfo", parameters: ["songMID": id, "trans": 1])
                    let parsed = try Self.parse(response)
                    guard !parsed.lines.isEmpty else { continue }
                    var credits: [String] = [], seen = Set<String>()
                    for line in parsed.lines.prefix(24) where line.start <= 10 {
                        let text = line.text.trimmingCharacters(in: .whitespacesAndNewlines)
                        if let match = Self.vocalCredit.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) {
                            let value = (text as NSString).substring(with: match.range(at: 1)).trimmingCharacters(in: .whitespacesAndNewlines)
                            if seen.insert(value.lowercased()).inserted { credits.append(value); if credits.count == 4 { break } }
                        }
                    }
                    if !credits.isEmpty {
                        let complete = candidate.artist + "; " + credits.joined(separator: "; ")
                        candidate.score = LyricsMatcher.score(query, title: candidate.title, artist: complete, album: candidate.album, duration: candidate.duration)
                        candidate.artistAliases = [complete]
                    }
                    guard candidate.score >= 4 else { continue }
                    let document = try await progress.prepare(parsed.binding(candidate, to: query), query: query)
                    if !LyricsSelection.needsLookup(document, target: query.preferredTranslationLanguage) { return document }
                    best = LyricsSelection.choose(best, document, target: query.preferredTranslationLanguage, primary: kind, backup: nil)
                } catch { if !LyricsCandidateFailure.canContinue(error) { throw error }; failure = error }
            }
        }
        if let failure { throw failure }; return best
    }
    private func call(module: String, method: String, parameters: [String: any Sendable]) async throws -> LyricsJSON {
        let url = URL(string: "https://u.y.qq.com/cgi-bin/musicu.fcg")!
        let credentials = try await session?.credentials(for: url)
        let body: [String: Any] = [
            "comm": ["ct": 24, "cv": 4747474, "format": "json", "inCharset": "utf-8", "outCharset": "utf-8", "notice": 0, "platform": "yqq.json", "needNewCode": 1, "uin": credentials?.uin ?? "0", "g_tk": credentials?.gtk ?? 5381, "g_tk_new_20200303": credentials?.gtk ?? 5381] as [String: Any],
            "req_1": ["module": module, "method": method, "param": parameters]
        ]
        do {
            let bytes = try JSONSerialization.data(withJSONObject: body), result: LyricsJSON
            if let session, let credentials {
                guard let transport = http as? any QQMusicAuthenticatedTransport else {
                    throw DropSpaceError.unavailable("This QQ transport cannot accept a session")
                }
                result = try await transport.requestQQMusic(json: bytes, credentials: credentials, session: session)
                guard await session.isCurrent(credentials.generation) else { throw CancellationError() }
            } else { result = try await http.request(url, json: bytes, referer: URL(string: "https://y.qq.com/")) }
            try Self.validate(result)
            if !result["req_1"].isNull { try Self.validate(result["req_1"]) }
            if method == "DoSearchForQQMusicDesktop", !(try Self.searchSongs(result)).isEmpty,
               let session, let credentials {
                await session.reportAccess(generation: credentials.generation, accepted: true)
            }
            return result
        } catch let error as QQMusicResponseError {
            if let session, let credentials { await session.reportAccess(generation: credentials.generation, accepted: false, code: error.code) }
            throw error
        }
    }
    public static func parse(_ root: LyricsJSON) throws -> LyricsDocument {
        try validate(root)
        let encoded = !root["req_1"].isNull
        let reply = encoded ? root["req_1"] : root
        try validate(reply)
        let body = encoded ? reply["data"] : root
        guard body.isObject, case .string = body["lyric"] else { throw DropSpaceError.invalid("QQ lyrics returned an unsupported lyric shape") }
        func decode(_ value: String) throws -> String {
            if !encoded || value.isEmpty { return LyricsEntityDecoder.decode(value) }
            let clean = value.filter { !$0.isWhitespace }
            guard let bytes = Data(base64Encoded: clean), let decoded = String(data: bytes, encoding: .utf8) else { throw DropSpaceError.invalid("QQ lyrics returned invalid encoded text") }
            return LyricsEntityDecoder.decode(decoded)
        }
        var document = try LyricsParser.parse(decode(body["lyric"].text), provider: .qqMusic, secondary: decode(body["trans"].text))
        document.providerDataRevision = 3
        for index in document.lines.indices where document.lines[index].secondary?.trimmingCharacters(in: .whitespacesAndNewlines) == "//" {
            document.lines[index].secondary = nil; document.lines[index].translationOrigin = .none
            document.lines[index].translationLanguage = nil; document.lines[index].translationLanguageIsExplicit = nil
        }
        return document
    }
    private static func searchSongs(_ root: LyricsJSON) throws -> [LyricsJSON] {
        try validate(root)
        let modern = !root["req_1"].isNull, reply = modern ? root["req_1"] : root
        try validate(reply); let data = reply["data"]
        guard data.isObject else { throw DropSpaceError.invalid("QQ search returned unsupported data") }
        if data["meta"].isObject, data["meta"]["is_filter"].number < 0 || data["meta"]["ret"].number != 0 { throw DropSpaceError.unavailable("QQ search result access was rejected") }
        let body = modern ? data["body"] : data
        guard body.isObject, body["song"].isObject, case .array = body["song"]["list"] else { throw DropSpaceError.invalid("QQ search returned an unsupported result shape") }
        return body["song"]["list"].array
    }
    private static func field(_ item: LyricsJSON, _ primary: String, _ legacy: String) -> String { item[primary].text.isEmpty ? item[legacy].text : item[primary].text }
    private static func validate(_ value: LyricsJSON) throws {
        guard value.isObject else { throw DropSpaceError.invalid("QQ returned an unsupported response shape") }
        for property in ["code", "retcode", "subcode"] where !value[property].isNull {
            guard case .number = value[property], value[property].number.rounded() == value[property].number else { throw DropSpaceError.invalid("QQ returned an unsupported status code") }
            if value[property].number != 0 {
                guard let code = Int(value[property].text) else { throw DropSpaceError.invalid("QQ returned an out-of-range status code") }
                throw QQMusicResponseError(code: code)
            }
        }
    }
    private static let vocalCredit = try! NSRegularExpression(pattern: #"^(?:演唱(?:\s*Artist)?|歌手|Artist|Vocals?|Singer)\s*[:：]\s*(.{1,128})$"#, options: .caseInsensitive)
}
public struct QQMusicResponseError: Error, Sendable, LocalizedError {
    public let code: Int
    public var errorDescription: String? { "QQ rejected the request (\(code))" }
}
