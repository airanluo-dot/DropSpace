import Foundation
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

@Test func localLyricsUsesMetadataAndBOMAndSkipsSymbolicReferences() async throws {
    let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: folder) }
    let source = "[ti:Song]\n[ar:Artist]\n[00:01]first\n[00:03]second"
    let file = folder.appendingPathComponent("recording.lrc")
    var bytes = Data([0xff, 0xfe]); bytes.append(try #require(source.data(using: .utf16LittleEndian)))
    try bytes.write(to: file)
    let provider = LocalLyricsProvider(directory: folder)
    let query = LyricsQuery(title: "Song", artist: "Artist", duration: 10, trackIdentity: "recording-1")
    let result = try await provider.query(query)
    #expect(result.lines.count == 2 && result.match?.trackIdentity == "recording-1")
    #expect(try Data(contentsOf: file) == bytes)
    #expect(try await provider.query(.init(title: "Song", artist: "Other")).lines.isEmpty)
    // A link to otherwise perfectly matching bytes must not be treated as a local candidate.
    try FileManager.default.moveItem(at: file, to: folder.appendingPathComponent("source.txt"))
    try FileManager.default.createSymbolicLink(at: file, withDestinationURL: folder.appendingPathComponent("source.txt"))
    #expect(try await provider.query(query).lines.isEmpty)
    #expect(try Data(contentsOf: folder.appendingPathComponent("source.txt")) == bytes)
}

@Test func localLyricsMalformedCodeUnitsPreserveTheMatchingFile() async throws {
    let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: folder) }
    let prefix = "[ti:Song]\n[ar:Artist]\n[00:01]hello "
    let encodings: [(String.Encoding, [UInt8], [UInt8])] = [
        (.utf8, [], [0xff]), (.utf16LittleEndian, [0xff, 0xfe], [0x00, 0xd8]),
        (.utf16BigEndian, [0xfe, 0xff], [0xd8, 0x00]),
        (.utf32LittleEndian, [0xff, 0xfe, 0, 0], [0, 0, 0x11, 0]),
        (.utf32BigEndian, [0, 0, 0xfe, 0xff], [0, 0x11, 0, 0])]
    let file = folder.appendingPathComponent("Song.lrc")
    for (encoding, bom, invalid) in encodings {
        let bytes = Data(bom) + (try #require(prefix.data(using: encoding))) + Data(invalid) + (try #require(" world".data(using: encoding)))
        try bytes.write(to: file)
        let result = try await LocalLyricsProvider(directory: folder).query(.init(title: "Song", artist: "Artist"))
        #expect(result.lines.first?.text == "hello \u{fffd} world")
        #expect(try Data(contentsOf: file) == bytes)
    }
}

private actor LRCLibFixture: LyricsTransport {
    var requests = 0
    func request(_ url: URL, json: Data?, referer: URL?) throws -> LyricsJSON {
        requests += 1
        // A nearer spelling and a wrong performer both lose to an exact recording.
        return try JSONDecoder().decode(LyricsJSON.self, from: Data(#"[{"id":1,"trackName":"Song Remix","artistName":"Artist","duration":180,"syncedLyrics":"[00:01]wrong"},{"id":2,"trackName":"Song","artistName":"Other","duration":180,"syncedLyrics":"[00:01]wrong"},{"id":3,"trackName":"Song","artistName":"Artist","duration":180,"syncedLyrics":"[00:01]first\n[00:03]second"}]"#.utf8))
    }
}
@Test func lrclibRecordingSelectionAndTransportBoundaries() async throws {
    let fixture = LRCLibFixture()
    let result = try await LRCLibLyricsProvider(http: fixture).query(.init(title: "Song", artist: "Artist", duration: 180, trackIdentity: "new-track"))
    #expect(result.match?.candidateID == "3" && result.match?.trackIdentity == "new-track" && result.lines.count == 2)
    #expect(await fixture.requests == 1)
    #expect(!LyricsHTTP.trusted(URL(string: "https://lrclib.net.evil.invalid/api/search")!))
    #expect(!LyricsHTTP.trusted(URL(string: "http://lrclib.net/api/search")!))
    #expect(!LyricsHTTP.trusted(URL(string: "https://user:password@lrclib.net/api/search")!))
    #expect(LyricsHTTP.url("https://lrclib.net/api/search", [("q", "AC/DC + Artist")]).absoluteString == "https://lrclib.net/api/search?q=AC%2FDC%20%2B%20Artist")
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_LYRICS_LIVE"] == "1"))
func actualLRCLibPublicSearchAndParse() async throws {
    let http = LyricsHTTP(budget: DownloadBudget())
    let document = try await LRCLibLyricsProvider(http: http).query(.init(title: "Never Gonna Give You Up", artist: "Rick Astley", trackIdentity: "live-validation"))
    #expect(!document.lines.isEmpty)
    #expect(document.match?.trackIdentity == "live-validation")
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_LYRICS_LIVE"] == "1"), arguments: [LyricsProvider.netEase, .qqMusic, .kugou, .amll])
func actualAdditionalLyricsSources(kind: LyricsProvider) async throws {
    let http = LyricsHTTP(budget: DownloadBudget())
    let source: any LyricsSource
    switch kind {
    case .netEase: source = NetEaseLyricsProvider(http: http)
    case .qqMusic: source = QQMusicLyricsProvider(http: http)
    case .kugou: source = KugouLyricsProvider(http: http)
    default: source = AMLLLyricsProvider(http: http)
    }
    let result = try await source.query(.init(title: "夜に駆ける", artist: "YOASOBI", trackIdentity: "live-source-validation"))
    #expect(!result.lines.isEmpty)
    #expect(result.match?.trackIdentity == "live-source-validation")
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_LYRICS_LIVE"] == "1"))
func actualKugouChineseRecording() async throws {
    let source = KugouLyricsProvider(http: LyricsHTTP(budget: DownloadBudget()))
    let result = try await source.query(.init(title: "七里香", artist: "周杰伦", trackIdentity: "live-kugou-validation"))
    #expect(!result.lines.isEmpty && result.match?.trackIdentity == "live-kugou-validation")
}

private actor SourceFixture: LyricsTransport {
    let response: @Sendable (URL, Data?) throws -> Data
    var paths: [String] = []
    init(_ response: @escaping @Sendable (URL, Data?) throws -> Data) { self.response = response }
    func request(_ url: URL, json: Data?, referer: URL?) throws -> LyricsJSON {
        paths.append(url.path)
        return try JSONDecoder().decode(LyricsJSON.self, from: response(url, json))
    }
}
private func sourceJSON(_ value: Any) throws -> Data { try JSONSerialization.data(withJSONObject: value) }

private struct SourceLanguageFixture: LyricsLanguageIdentifying {
    func prepare(_ document: LyricsDocument, target: String) async throws -> LyricsDocument {
        LyricsLanguagePolicy.prepare(document, target: target, original: .init(label: "zh", score: 1),
            translation: document.lines.contains(where: { $0.secondary != nil }) ? .init(label: "en", score: 1) : nil)
    }
}
private actor SourcePreviewFixture {
    var ids: [String] = []
    func record(_ value: LyricsDocument) { ids.append(value.match?.candidateID ?? "") }
}
@Test func netEaseProgressiveCandidatesKeepSearchingForTargetAndRebindPublicResponses() async throws {
    let fixture = SourceFixture { url, _ in
        if url.path.contains("search") {
            return try sourceJSON(["code": 200, "result": ["songs": (1...2).map { ["id": $0, "name": "Song", "artists": [["name": "Artist"]]] as [String: Any] }]])
        }
        let translated = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "id" })?.value == "2"
        return try sourceJSON(["code": 200, "lrc": ["lyric": "[00:01]这是一句原始歌词"], "tlyric": ["lyric": translated ? "[00:01]This is a native translated lyric" : ""]])
    }
    let provider = NetEaseLyricsProvider(http: fixture), previews = SourcePreviewFixture()
    var query = LyricsQuery(title: "Song", artist: "Artist", trackIdentity: "first-playback"); query.preferredTranslationLanguage = "en"
    let progress = LyricsSourceProgress(identifier: SourceLanguageFixture()) { await previews.record($0) }
    let result = try await provider.query(query, progress: progress)
    #expect(result.match?.candidateID == "2" && result.translationAdmission?.decision == .providerTarget)
    #expect(await previews.ids == ["1", "2"])
    #expect(await fixture.paths.count == 3)
    query.trackIdentity = "second-playback"
    let rebound = try await provider.query(query, progress: progress)
    #expect(rebound.match?.trackIdentity == "second-playback")
    #expect(LyricsLanguagePolicy.current(rebound, target: "en"))
    #expect(await fixture.paths.count == 3) // Same public bytes, newly matched document.
}

@Test func nativeProviderPayloadsPreservePairingStatusAndResponseCache() async throws {
    let netEase = try JSONDecoder().decode(LyricsJSON.self, from: sourceJSON([
        "yrc": ["lyric": "[1000,1000](1000,1000,0)Word"], "ytlrc": ["lyric": ""],
        "lrc": ["lyric": "[00:02]Original line"], "tlyric": ["lyric": "[00:02]原生译文"]]))
    let paired = try NetEaseLyricsProvider.parse(netEase)
    #expect(paired.lines.first?.start == 2 && paired.lines.first?.secondary == "原生译文")
    #expect(paired.lines.first?.words.isEmpty == true && paired.providerDataRevision == 3)
    let qq = try JSONDecoder().decode(LyricsJSON.self, from: sourceJSON(["code": 0, "req_1": ["code": 0, "data": [
        "lyric": Data("[00:01]Fish &amp; chips\n[00:02]Second line".utf8).base64EncodedString(),
        "trans": Data("[00:01]//\n[00:02]第二行".utf8).base64EncodedString()]]]))
    let decoded = try QQMusicLyricsProvider.parse(qq)
    #expect(decoded.lines[0].text == "Fish & chips" && decoded.lines[0].secondary == nil)
    #expect(decoded.lines[1].secondary == "第二行" && decoded.lines[1].translationOrigin == .provider)
    let denied = try JSONDecoder().decode(LyricsJSON.self, from: Data(#"{"code":1000,"lyric":"[00:01]must not pass"}"#.utf8))
    #expect(throws: (any Error).self) { try QQMusicLyricsProvider.parse(denied) }
    let fixture = SourceFixture { url, _ in
        if url.path.hasSuffix("search") { return try sourceJSON(["status": 200, "data": ["items": [["id": "own-1", "musicNames": ["Song"], "artistNames": ["Artist"], "albumNames": []]]]]) }
        return try sourceJSON(["status": 200, "data": ["id": "own-1", "format": "ttml", "lyrics": "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"1s\" end=\"3s\">Original line</p></div></body></tt>"]])
    }
    let provider = AMLLLyricsProvider(http: fixture)
    var query = LyricsQuery(title: "Song", artist: "Artist", trackIdentity: "track")
    #expect(try await provider.query(query).lines.count == 1)
    _ = try await provider.query(query)
    #expect(await fixture.paths.filter { $0.hasSuffix("get") }.count == 1)
    query.bypassProviderResponseCache = true; _ = try await provider.query(query)
    #expect(await fixture.paths.filter { $0.hasSuffix("get") }.count == 2)
    await provider.clearResponseCache(); query.bypassProviderResponseCache = false; _ = try await provider.query(query)
    #expect(await fixture.paths.filter { $0.hasSuffix("get") }.count == 3)
}

@Test func kugouCompressedWordsTranslationAndInflationLimit() throws {
    let fixtures = Bundle.module.resourceURL!.appendingPathComponent("Fixtures")
    let encoded = try String(contentsOf: fixtures.appendingPathComponent("lyrics-known.krc-base64"), encoding: .utf8)
    let result = try KugouKRCParser.parse(encoded)
    #expect(result.lines.count == 2)
    #expect(result.lines[0].start == 1 && result.lines[0].end == 3.5)
    #expect(result.lines[0].words.map(\.text) == ["First ", "line"])
    #expect(result.lines[0].words[1].start == 2 && result.lines[0].words[1].end == 3.5)
    #expect(result.lines[0].secondary == "第一行" && result.lines[1].secondary == "第二行")
    #expect(result.lines[0].translationLanguage == nil && result.lines[0].translationOrigin == .provider)
    let excessive = try String(contentsOf: fixtures.appendingPathComponent("lyrics-excessive.krc-base64"), encoding: .utf8)
    #expect(throws: (any Error).self) { try KugouKRCParser.parse(excessive) }
}
