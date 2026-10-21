import Foundation
import Synchronization
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private final class QQFixtureProtocol: URLProtocol, @unchecked Sendable {
    struct Reply { var status = 200; var headers: [String: String] = [:]; var body: Data }
    struct State { var replies: [Reply] = []; var requests: [URLRequest] = []; var bodies: [Data] = [] }
    static let observations = Mutex(State())
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        var body = request.httpBody ?? Data()
        if body.isEmpty, let stream = request.httpBodyStream {
            stream.open(); defer { stream.close() }
            var buffer = [UInt8](repeating: 0, count: 4096)
            while body.count <= 16384 {
                let count = stream.read(&buffer, maxLength: buffer.count)
                if count <= 0 { break }; body.append(contentsOf: buffer.prefix(count))
            }
        }
        let reply = Self.observations.withLock {
            $0.requests.append(request)
            $0.bodies.append(body)
            return $0.replies.isEmpty ? nil : $0.replies.removeFirst()
        }
        guard let reply, let url = request.url,
              let response = HTTPURLResponse(url: url, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: reply.headers) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse)); return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: reply.body)
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}
private actor QQTransportMemoryStorage: QQMusicCredentialStorage {
    private var value: Data?
    func read() -> Data? { value }
    func write(_ bytes: Data) { value = bytes }
    func remove() { value = nil }
}

@Test func qqAuthenticatedProviderRenewsTicketsAndRetiresQueuedRequests() async throws {
    func bytes(_ json: String) -> Data { Data(json.utf8) }
    let line = Data("[00:00.00]Native lyric fixture\n[00:30.00]Second native lyric fixture".utf8).base64EncodedString()
    QQFixtureProtocol.observations.withLock {
        $0 = .init(replies: [
            .init(headers: ["Set-Cookie": "qqmusic_key=synthetic-renewed; Domain=.qq.com; Path=/; Secure; HttpOnly"], body: bytes("""
            {"code":0,"req_1":{"code":0,"data":{"body":{"song":{"list":[{"mid":"fixture","name":"Fixture Track","singer":[{"name":"Fixture Artist"}],"album":{"name":"Fixture Album"},"interval":90}]}}}}}
            """)),
            .init(body: bytes("{\"code\":0,\"req_1\":{\"code\":0,\"data\":{\"lyric\":\"\(line)\",\"trans\":\"\"}}}")),
            .init(body: bytes("{}")),
            .init(status: 307, headers: ["Location": "https://attacker.test/"], body: Data()),
            .init(status: 429, body: bytes("{}"))
        ])
    }
    let budget = DownloadBudget(), session = QQMusicSession(storage: QQTransportMemoryStorage())
    let http = LyricsHTTP(budget: budget, testingProtocolClasses: [QQFixtureProtocol.self])
    try await session.save([.init(name: "qqmusic_key", value: "synthetic-original", domain: ".qq.com"),
                            .init(name: "qqmusic_uin", value: "1234", domain: ".qq.com")])
    let endpoint = URL(string: "https://u.y.qq.com/cgi-bin/musicu.fcg")!
    let initialCredentials = try await session.credentials(for: endpoint)
    let provider = QQMusicLyricsProvider(http: http, session: session)
    var query = LyricsQuery(title: "Fixture Track", artist: "Fixture Artist", album: "Fixture Album",
                            duration: 90, trackIdentity: "qq-session-fixture")
    query.preferredTranslationLanguage = ""
    let document = try await provider.query(query)
    #expect(document.lines.count == 2)
    #expect(await session.status.state == .connected)
    let requests = QQFixtureProtocol.observations.withLock { $0.requests }
    #expect(requests.count == 2)
    #expect(requests[0].value(forHTTPHeaderField: "Cookie")?.contains("synthetic-original") == true)
    #expect(requests[1].value(forHTTPHeaderField: "Cookie")?.contains("synthetic-renewed") == true)
    let bodies = try QQFixtureProtocol.observations.withLock { try $0.bodies.prefix(2).map { try JSONDecoder().decode(LyricsJSON.self, from: $0) } }
    let renewedCredentials = try await session.credentials(for: endpoint)
    #expect(bodies[0]["comm"]["uin"].text == "1234")
    #expect(bodies[0]["comm"]["g_tk"].number == Double(initialCredentials.gtk))
    #expect(bodies[1]["comm"]["g_tk_new_20200303"].number == Double(renewedCredentials.gtk))
    #expect(initialCredentials.gtk != renewedCredentials.gtk)
    // Other source requests use the same budget/transport, but never the QQ ticket.
    _ = try await http.request(URL(string: "https://lrclib.net/api/search?q=fixture")!)
    #expect(QQFixtureProtocol.observations.withLock { $0.requests[2].value(forHTTPHeaderField: "Cookie") } == nil)
    let credentials = try await session.credentials(for: endpoint)
    await #expect(throws: (any Error).self) {
        _ = try await http.requestQQMusic(json: bytes("{}"), credentials: credentials, session: session)
    }
    #expect(QQFixtureProtocol.observations.withLock { $0.requests.count } == 4)
    #expect(QQFixtureProtocol.observations.withLock { $0.requests.allSatisfy { $0.url?.host != "attacker.test" } })
    await #expect(throws: LyricsHTTPError.self) {
        _ = try await http.requestQQMusic(json: bytes("{}"), credentials: credentials, session: session)
    }
    await #expect(throws: QQMusicAccessError.self) { _ = try await session.credentials(for: endpoint) }
    // A signed-out request waiting on the actual shared connection permit never
    // reaches URLSession or releases ownership as a successful old response.
    let slot = try await budget.connections.acquire()
    try await budget.connections.setLimit(1)
    let waiting = Task { try await http.requestQQMusic(json: bytes("{}"), credentials: credentials, session: session) }
    try await session.signOut()
    await budget.connections.release(slot)
    await #expect(throws: CancellationError.self) { _ = try await waiting.value }
    #expect(QQFixtureProtocol.observations.withLock { $0.requests.count } == 5)
    #expect(await budget.connections.activeCount == 0)
}
