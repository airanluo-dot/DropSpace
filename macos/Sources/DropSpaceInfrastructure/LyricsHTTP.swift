import Foundation
import DropSpaceCore

public indirect enum LyricsJSON: Codable, Sendable {
    case object([String: LyricsJSON]), array([LyricsJSON]), string(String), number(Decimal), boolean(Bool), null
    public init(from decoder: any Decoder) throws {
        let limit = decoder.userInfo[CodingUserInfoKey(rawValue: "lyricsMaximumDepth")!] as? Int ?? 32
        guard decoder.codingPath.count <= limit else { throw DropSpaceError.limit("Lyrics JSON nesting") }
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null }
        else if let value = try? container.decode(Bool.self) { self = .boolean(value) }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else if let value = try? container.decode(Decimal.self) { self = .number(value) }
        else if let value = try? container.decode([String: LyricsJSON].self) { self = .object(value) }
        else { self = .array(try container.decode([LyricsJSON].self)) }
    }
    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .object(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .boolean(let value): try container.encode(value)
        case .null: try container.encodeNil()
        }
    }
    public subscript(_ key: String) -> LyricsJSON { if case .object(let values) = self { return values[key] ?? .null }; return .null }
    public var text: String { switch self { case .string(let text): text; case .number(let number): NSDecimalNumber(decimal: number).stringValue; default: "" } }
    public var number: Double { let value = Double(text) ?? 0; return value.isFinite ? value : 0 }
    public var array: [LyricsJSON] { if case .array(let items) = self { return Array(items.prefix(100)) }; return [] }
    var allArray: [LyricsJSON]? { if case .array(let items) = self { return items }; return nil }
    public var isObject: Bool { if case .object = self { return true }; return false }
    public var isNull: Bool { if case .null = self { return true }; return false }
    public var flag: Bool? { if case .boolean(let flag) = self { return flag }; return nil }
}
public struct LyricsHTTPError: Error, LocalizedError, Sendable {
    public let status: Int
    public var errorDescription: String? { "Lyrics source returned HTTP \(status)" }
}
public protocol LyricsTransport: Sendable {
    func request(_ url: URL, json: Data?, referer: URL?) async throws -> LyricsJSON
}
public protocol QQMusicAuthenticatedTransport: LyricsTransport {
    func requestQQMusic(json: Data, credentials: QQMusicRequestCredentials, session: QQMusicSession) async throws -> LyricsJSON
}
public extension LyricsTransport {
    func get(_ url: URL, referer: URL? = nil) async throws -> LyricsJSON { try await request(url, json: nil, referer: referer) }
}
private final class LyricsRedirectDelegate: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) { completionHandler(nil) }
}
public actor LyricsHTTP: LyricsTransport, QQMusicAuthenticatedTransport {
    private struct QQAuthorization: Sendable {
        let credentials: QQMusicRequestCredentials
        let session: QQMusicSession
    }
    private let budget: DownloadBudget
    private let session: URLSession
    private let netEaseGate = DownloadPermitPool(limit: 1)
    private var netEaseNext = ContinuousClock.now
    private var searchCooldown = ContinuousClock.now
    private var lyricCooldown = ContinuousClock.now
    private static let hosts: Set<String> = ["music.163.com", "c.y.qq.com", "u.y.qq.com", "lyrics.kugou.com", "songsearch.kugou.com", "lrclib.net", "api.amll.dev"]
    public init(budget: DownloadBudget) {
        self.budget = budget
        session = Self.makeSession()
    }
    init(budget: DownloadBudget, testingProtocolClasses: [AnyClass]) {
        self.budget = budget
        session = Self.makeSession(protocolClasses: testingProtocolClasses)
    }
    private static func makeSession(protocolClasses: [AnyClass]? = nil) -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.urlCredentialStorage = nil; config.urlCache = nil
        config.timeoutIntervalForRequest = 8; config.timeoutIntervalForResource = 8
        if let protocolClasses { config.protocolClasses = protocolClasses }
        return URLSession(configuration: config, delegate: LyricsRedirectDelegate(), delegateQueue: nil)
    }
    public func request(_ url: URL, json: Data? = nil, referer: URL? = nil) async throws -> LyricsJSON {
        try await request(url, json: json, referer: referer, qq: nil)
    }
    public func requestQQMusic(json: Data, credentials: QQMusicRequestCredentials, session: QQMusicSession) async throws -> LyricsJSON {
        try await request(URL(string: "https://u.y.qq.com/cgi-bin/musicu.fcg")!, json: json,
                          referer: URL(string: "https://y.qq.com/"), qq: .init(credentials: credentials, session: session))
    }
    private func request(_ url: URL, json: Data?, referer: URL?, qq: QQAuthorization?) async throws -> LyricsJSON {
        guard Self.trusted(url) else { throw DropSpaceError.invalid("Untrusted lyrics host") }
        let netEase = url.host == "music.163.com", search = url.path.localizedCaseInsensitiveContains("search")
        if netEase {
            let permit = try await netEaseGate.acquire()
            do {
                guard ContinuousClock.now >= (search ? searchCooldown : lyricCooldown) else { throw DropSpaceError.unavailable("NetEase request cooldown is active") }
                if ContinuousClock.now < netEaseNext { try await Task.sleep(until: netEaseNext, clock: .continuous) }
                try Task.checkCancellation()
                guard ContinuousClock.now >= (search ? searchCooldown : lyricCooldown) else { throw DropSpaceError.unavailable("NetEase request cooldown is active") }
                netEaseNext = .now.advanced(by: .milliseconds(500)); await netEaseGate.release(permit)
            } catch { await netEaseGate.release(permit); throw error }
        }
        let connection = try await budget.connections.acquire()
        do {
            let result = try await withThrowingTaskGroup(of: LyricsJSON.self) { group in
                group.addTask { try await self.fetch(url, json: json, referer: referer, qq: qq) }
                group.addTask { try await Task.sleep(for: .seconds(8)); throw URLError(.timedOut) }
                defer { group.cancelAll() }
                return try await group.next()!
            }
            if netEase, [405, 429].contains(Int(result["code"].number)) { cooldown(search: search) }
            await budget.connections.release(connection); return result
        } catch {
            if netEase, (error as? LyricsHTTPError)?.status == 429 { cooldown(search: search) }
            await budget.connections.release(connection); throw error
        }
    }
    private func cooldown(search: Bool) {
        if search { searchCooldown = .now.advanced(by: .seconds(30)) } else { lyricCooldown = .now.advanced(by: .seconds(30)) }
    }
    private func fetch(_ source: URL, json: Data?, referer: URL?, qq: QQAuthorization?) async throws -> LyricsJSON {
        var url = source
        let deadline = ContinuousClock.now.advanced(by: .seconds(8))
        for redirects in 0...3 {
            try Task.checkCancellation()
            let remaining = ContinuousClock.now.duration(to: deadline)
            guard remaining > .zero else { throw URLError(.timedOut) }
            var request = URLRequest(url: url)
            request.timeoutInterval = Double(remaining.components.seconds) + Double(remaining.components.attoseconds) / 1e18
            request.setValue("DropSpace/0.1 macOS (+https://github.com/airanluo-dot/DropSpace)", forHTTPHeaderField: "User-Agent")
            if let referer { request.setValue(referer.absoluteString, forHTTPHeaderField: "Referer") }
            if let json { request.httpMethod = "POST"; request.httpBody = json; request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type") }
            if let qq {
                guard QQMusicCookiePolicy.trusted(url), await qq.session.isCurrent(qq.credentials.generation) else { throw CancellationError() }
                if !qq.credentials.cookieHeader.isEmpty { request.setValue(qq.credentials.cookieHeader, forHTTPHeaderField: "Cookie") }
            }
            let (stream, response) = try await session.bytes(for: request)
            defer { stream.task.cancel() }
            guard let http = response as? HTTPURLResponse else { throw DropSpaceError.invalid("Invalid lyrics response") }
            if let qq {
                try Task.checkCancellation()
                guard await qq.session.isCurrent(qq.credentials.generation) else { throw CancellationError() }
                if http.statusCode == 429 || !qq.credentials.cookieHeader.isEmpty && [401, 403].contains(http.statusCode) {
                    await qq.session.reportAccess(generation: qq.credentials.generation, accepted: false, code: http.statusCode)
                }
                if !qq.credentials.cookieHeader.isEmpty, let header = http.value(forHTTPHeaderField: "Set-Cookie") {
                    try await qq.session.applyResponseCookies([header], from: url, generation: qq.credentials.generation)
                }
            }
            if [301, 302, 303, 307, 308].contains(http.statusCode) {
                guard json == nil, redirects < 3, let location = http.value(forHTTPHeaderField: "Location"), let next = URL(string: location, relativeTo: url)?.absoluteURL,
                      Self.trusted(next), next.host?.lowercased() == url.host?.lowercased() else { throw DropSpaceError.invalid("Untrusted or excessive lyrics redirect") }
                url = next; continue
            }
            guard (200..<300).contains(http.statusCode) else { throw LyricsHTTPError(status: http.statusCode) }
            guard response.expectedContentLength <= 3 * 1024 * 1024 else { throw DropSpaceError.limit("Lyrics response exceeds limit") }
            var bytes = Data(), charged = 0
            for try await byte in stream {
                bytes.append(byte)
                if bytes.count > 3 * 1024 * 1024 { throw DropSpaceError.limit("Lyrics response exceeds limit") }
                if bytes.count - charged >= 16384 {
                    try await budget.bandwidth.consume(bytes.count - charged); charged = bytes.count
                    if ContinuousClock.now >= deadline { throw URLError(.timedOut) }
                }
            }
            try await budget.bandwidth.consume(bytes.count - charged); try Task.checkCancellation()
            guard ContinuousClock.now < deadline else { throw URLError(.timedOut) }
            var encoding = String.Encoding.utf8
            if let charset = response.textEncodingName {
                let code = CFStringConvertIANACharSetNameToEncoding(charset as CFString)
                if code != kCFStringEncodingInvalidId { encoding = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(code)) }
            }
            guard var text = String(data: bytes, encoding: encoding)?.trimmingCharacters(in: .whitespacesAndNewlines) else { throw DropSpaceError.invalid("Invalid lyrics response encoding") }
            text = String(text.drop(while: { $0 == "\u{FEFF}" }))
            if !text.hasPrefix("{") && !text.hasPrefix("[") {
                guard let start = text.firstIndex(of: "("), let end = text.lastIndex(of: ")"), start < end else { throw DropSpaceError.invalid("Invalid lyrics JSONP response") }
                text = String(text[text.index(after: start)..<end])
            }
            if let qq, await !qq.session.isCurrent(qq.credentials.generation) { throw CancellationError() }
            return try JSONDecoder().decode(LyricsJSON.self, from: Data(text.utf8))
        }
        throw DropSpaceError.invalid("Excessive lyrics redirects")
    }
    public static func trusted(_ url: URL) -> Bool {
        url.scheme == "https" && hosts.contains(url.host?.lowercased() ?? "") && (url.port == nil || url.port == 443) && url.user == nil && url.password == nil
    }
    public static func url(_ base: String, _ parameters: [(String, String)]) -> URL {
        var parts = URLComponents(string: base)!
        let unreserved = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~")
        func escaped(_ value: String) -> String { String(String.UnicodeScalarView(value.unicodeScalars.prefix(2048))).addingPercentEncoding(withAllowedCharacters: unreserved)! }
        parts.percentEncodedQuery = parameters.map { escaped($0.0) + "=" + escaped($0.1) }.joined(separator: "&")
        return parts.url!
    }
}
