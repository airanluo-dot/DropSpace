import Foundation
import DropSpaceCore

public struct LRCLibLyricsProvider: LyricsSource {
    public let kind = LyricsProvider.lrclib
    private let http: any LyricsTransport
    public init(http: any LyricsTransport) { self.http = http }
    public func query(_ query: LyricsQuery) async throws -> LyricsDocument {
        guard query.duration.isFinite, query.duration < Double(Int64.max) else { throw DropSpaceError.invalid("Invalid recording duration") }
        var attempted = Set<String>(), remaining = 3
        var confirmedInstrumental = LyricsDocument(lines: [], provider: kind)
        var lastFailure: (any Error)?
        if query.duration > 0, !LyricsMatcher.artistCandidates(query).isEmpty, !query.album.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            for artist in LyricsMatcher.searchArtists(query).prefix(2) {
                do {
                    let response = try await http.get(LyricsHTTP.url("https://lrclib.net/api/get", [("track_name", query.title), ("artist_name", artist), ("album_name", query.album), ("duration", String(Int64(query.duration)))]))
                    guard response.isObject, !response["id"].text.isEmpty, case .string = response["trackName"] else { throw DropSpaceError.invalid("Unsupported LRCLIB exact response") }
                    let score = score(query, response)
                    guard score >= 4 else { continue }
                    let parsed = try parse(response, query: query, score: score)
                    if parsed.bodyQuality == .confirmedInstrumental { confirmedInstrumental = parsed }
                    if !parsed.lines.isEmpty, attempted.insert(response["id"].text).inserted { remaining -= 1; return parsed }
                } catch is CancellationError { throw CancellationError() }
                catch let error as LyricsHTTPError where [400, 404].contains(error.status) { continue }
                catch { if !LyricsCandidateFailure.canContinue(error) { throw error }; lastFailure = error }
            }
        }
        for term in LyricsMatcher.searchTerms(query) {
            if remaining == 0 { break }
            let response = try await http.get(LyricsHTTP.url("https://lrclib.net/api/search", [("q", term)]))
            guard case .array = response else { throw DropSpaceError.invalid("Unsupported LRCLIB search response") }
            var ranked: [(value: LyricsJSON, score: Double, order: Int)] = []
            for (index, item) in response.array.enumerated() {
                let hasBody = !item["syncedLyrics"].text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !item["plainLyrics"].text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                guard hasBody || item["instrumental"].flag == true, !item["id"].text.isEmpty else { continue }
                let value = score(query, item)
                if value >= 4 { ranked.append((item, value, index)) }
            }
            let candidates = ranked.sorted { $0.score == $1.score ? $0.order < $1.order : $0.score > $1.score }.prefix(3)
            for entry in candidates {
                let candidate = entry.value, score = entry.score
                try Task.checkCancellation()
                if remaining == 0 { break }
                guard attempted.insert(candidate["id"].text).inserted else { continue }; remaining -= 1
                do {
                    let document = try parse(candidate, query: query, score: score)
                    if !document.lines.isEmpty { return document }
                    if document.bodyQuality == .confirmedInstrumental { confirmedInstrumental = document }
                } catch { if !LyricsCandidateFailure.canContinue(error) { throw error }; lastFailure = error }
            }
        }
        if let lastFailure { throw lastFailure }
        return confirmedInstrumental
    }
    private func score(_ query: LyricsQuery, _ value: LyricsJSON) -> Double {
        LyricsMatcher.score(query, title: value["trackName"].text, artist: value["artistName"].text, album: value["albumName"].text, duration: value["duration"].number)
    }
    private func parse(_ value: LyricsJSON, query: LyricsQuery, score: Double) throws -> LyricsDocument {
        var text = value["syncedLyrics"].text
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { text = value["plainLyrics"].text }
        var document = try LyricsParser.parse(text, provider: kind)
        if document.lines.isEmpty, value["instrumental"].flag == true { document.bodyQuality = .confirmedInstrumental }
        return document.binding(.init(title: value["trackName"].text, artist: value["artistName"].text, album: value["albumName"].text, duration: value["duration"].number, score: score, candidateID: value["id"].text), to: query)
    }
}
enum LyricsCandidateFailure {
    static func canContinue(_ error: any Error) -> Bool {
        if error is CancellationError || Task.isCancelled { return false }
        if let http = error as? LyricsHTTPError { return [404, 408].contains(http.status) || (500...599).contains(http.status) }
        if error is DecodingError || error is URLError || error is POSIXError { return true }
        if let native = error as? DropSpaceError { switch native { case .invalid, .limit, .storage: return true; default: return false } }
        return (error as NSError).domain == XMLParser.errorDomain
    }
}
