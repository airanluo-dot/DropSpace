import Foundation
import DropSpaceCore

/// Credential values must not appear in diagnostics, reflection or view state.
public struct QQMusicCookie: Codable, Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    let name: String
    let value: String
    let domain: String
    let path: String
    let expires: Double?
    let httpOnly: Bool
    public init(name: String, value: String, domain: String, path: String = "/", expires: Double? = nil, httpOnly: Bool = false) {
        self.name = name; self.value = value; self.domain = domain
        self.path = path; self.expires = expires; self.httpOnly = httpOnly
    }
    public var description: String { "QQMusicCookie(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: [:]) }
}

public struct QQMusicRequestCredentials: Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    public let generation: UInt64
    let uin: String
    let gtk: Int
    let cookieHeader: String
    public var description: String { "QQMusicRequestCredentials(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: [:]) }
}

public enum QQMusicCookiePolicy {
    static let names: Set<String> = ["qqmusic_key", "qqmusic_uin", "qm_keyst", "uin", "wxuin", "login_type"]
    private struct Key: Hashable { let name: String; let domain: String; let path: String }
    public static func validated(_ cookies: [QQMusicCookie]) -> [QQMusicCookie] {
        var result: [QQMusicCookie] = [], indexes: [Key: Int] = [:]
        // Match the frozen Take(64), before filtering, and last duplicate wins.
        for cookie in cookies.prefix(64) {
            let host = cookie.domain.drop(while: { $0 == "." })
            guard names.contains(cookie.name), ["qq.com", "y.qq.com", "c.y.qq.com", "u.y.qq.com"].contains(String(host)),
                  cookie.path == "/", (1...4096).contains(cookie.value.utf16.count),
                  cookie.value.utf8.allSatisfy({ (33...126).contains($0) && ![59, 44, 34, 92].contains($0) }),
                  cookie.expires.map({ $0.isFinite && (0...253_402_300_799).contains($0) }) ?? true else { continue }
            let key = Key(name: cookie.name, domain: cookie.domain, path: cookie.path)
            if let index = indexes[key] { result[index] = cookie }
            else { indexes[key] = result.count; result.append(cookie) }
        }
        return result
    }
    static func current(_ cookie: QQMusicCookie, now: Date) -> Bool {
        cookie.expires.map { $0 > floor(now.timeIntervalSince1970) } ?? true
    }
    public static func usable(_ cookies: [QQMusicCookie], now: Date = Date()) -> Bool {
        validated(cookies).contains { ["qqmusic_key", "qm_keyst"].contains($0.name) && current($0, now: now) }
    }
    static func request(_ cookies: [QQMusicCookie], url: URL, generation: UInt64, now: Date) -> QQMusicRequestCredentials {
        let valid = cookies.filter { current($0, now: now) }
        func first(_ name: String) -> String { valid.first { $0.name == name }?.value ?? "" }
        let rawUser = ["qqmusic_uin", "uin", "wxuin"].map(first).first { !$0.isEmpty } ?? ""
        let user = String(rawUser.drop(while: { $0 == "o" }))
        let uin = (1...20).contains(user.count) && user.utf8.allSatisfy { (48...57).contains($0) } ? user : "0"
        let hash = first("qqmusic_key").utf8.reduce(UInt32(5381)) { ($0 &* 33) &+ UInt32($1) }
        var header = ""
        if trusted(url), let host = url.host?.lowercased() {
            // Frozen CookieContainer groups same-path cookies by exact host,
            // then .qq.com, then .y.qq.com, independent of insertion order.
            // This matters when two allowed domains supply the same cookie name.
            header = [host, ".qq.com", ".y.qq.com"].flatMap { domain in
                valid.filter { $0.domain.lowercased() == domain }
            }.map { $0.name + "=" + $0.value }.joined(separator: "; ")
        }
        return .init(generation: generation, uin: uin, gtk: Int(hash & 0x7fff_ffff), cookieHeader: header)
    }
    public static func trusted(_ url: URL) -> Bool {
        url.scheme == "https" && ["u.y.qq.com", "c.y.qq.com"].contains(url.host?.lowercased() ?? "") &&
            (url.port == nil || url.port == 443) && url.user == nil && url.password == nil
    }
    static func responseCookies(_ saved: [QQMusicCookie], headers: [String], url: URL, now: Date) -> [QQMusicCookie]? {
        guard trusted(url), let host = url.host?.lowercased() else { return nil }
        var merged = saved.filter { current($0, now: now) }, changed = false
        for header in headers.prefix(16) {
            guard header.utf16.count <= 8192, !header.contains("\r"), !header.contains("\n") else { continue }
            // URLSession can coalesce multiple Set-Cookie fields. Let the native
            // parser handle Expires commas before applying the field allowlist.
            let parsedCookies = HTTPCookie.cookies(withResponseHeaderFields: ["Set-Cookie": header], for: url)
            guard parsedCookies.count <= 16 else { continue }
            for parsed in parsedCookies {
                // Foundation parses cookies without enforcing origin trust for us.
                let domain = parsed.domain.lowercased()
                guard names.contains(parsed.name), parsed.path == "/",
                      ["qq.com", "y.qq.com", "c.y.qq.com", "u.y.qq.com"].contains(String(domain.drop(while: { $0 == "." }))),
                      domain.hasPrefix(".") ? host.hasSuffix(domain) : host == domain else { continue }
                let replacement = QQMusicCookie(name: parsed.name, value: parsed.value, domain: domain,
                    expires: parsed.expiresDate?.timeIntervalSince1970, httpOnly: parsed.isHTTPOnly)
                let deletion = parsed.expiresDate.map { $0 <= now } ?? false
                guard deletion || !validated([replacement]).isEmpty else { continue }
                merged.removeAll { $0.name == parsed.name && $0.domain.lowercased() == domain && $0.path == "/" }
                if !deletion { merged.append(replacement) }
                changed = true
            }
        }
        return changed ? validated(merged) : nil
    }
}

public protocol QQMusicCredentialStorage: Sendable {
    func read() async throws -> Data?
    func write(_ bytes: Data) async throws
    func remove() async throws
}
public struct QQMusicKeychainStorage: QQMusicCredentialStorage {
    private let keychain: KeychainStore
    public init(keychain: KeychainStore = KeychainStore()) { self.keychain = keychain }
    public func read() async throws -> Data? { try await keychain.read("qqmusic.session.v1") }
    public func write(_ bytes: Data) async throws { try await keychain.save(bytes, account: "qqmusic.session.v1") }
    public func remove() async throws { try await keychain.remove("qqmusic.session.v1") }
}
public struct QQMusicSessionStatus: Sendable, Equatable {
    public enum State: Sendable { case signedOut, saved, connected, expired, rejected, storageError }
    public let state: State
    public let hasSavedSession: Bool
    public let generation: UInt64
}
public struct QQMusicAccessError: Error, Sendable, LocalizedError {
    public let code: Int
    public var errorDescription: String? { code == 429 ? "QQ Music request cooldown is active." : "QQ Music session needs attention." }
}

/// A session is accepted only through an explicit bounded handoff. This owner
/// never reads a browser profile, cookies database, clipboard or account password.
public actor QQMusicSession {
    private let storage: any QQMusicCredentialStorage
    private let gate = DownloadPermitPool(limit: 1)
    private var cookies: [QQMusicCookie] = []
    private var loaded = false
    private var state = QQMusicSessionStatus.State.signedOut
    private var generation: UInt64 = 0
    private var rateLimitUntil = Date.distantPast
    public init(storage: any QQMusicCredentialStorage = QQMusicKeychainStorage()) { self.storage = storage }
    public var status: QQMusicSessionStatus {
        .init(state: state, hasSavedSession: !cookies.isEmpty || state == .storageError, generation: generation)
    }
    public func isCurrent(_ run: UInt64) -> Bool { generation == run }
    public func load(now: Date = Date()) async throws {
        let token = try await gate.acquire()
        do {
            try Task.checkCancellation()
            if !loaded {
                do {
                    if var data = try await storage.read() {
                        defer { data.resetBytes(in: data.startIndex..<data.endIndex) }
                        try Task.checkCancellation()
                        guard data.count <= 64 * 1024 else { throw DropSpaceError.limit("QQ session storage size") }
                        cookies = QQMusicCookiePolicy.validated(try JSONDecoder().decode([QQMusicCookie].self, from: data))
                        state = QQMusicCookiePolicy.usable(cookies, now: now) ? .saved : .expired
                    }
                    loaded = true
                } catch is CancellationError { throw CancellationError() }
                catch { loaded = true; state = .storageError }
            }
            await gate.release(token)
        } catch { await gate.release(token); throw error }
    }
    public func save(_ proposed: [QQMusicCookie], now: Date = Date()) async throws {
        let accepted = QQMusicCookiePolicy.validated(proposed)
        guard QQMusicCookiePolicy.usable(accepted, now: now) else { throw DropSpaceError.invalid("QQ Music has not supplied a usable session.") }
        let token = try await gate.acquire()
        do {
            try Task.checkCancellation()
            try await persist(accepted)
            // A completed durable write wins over cancellation arriving after it.
            cookies = accepted; state = .saved; generation &+= 1; loaded = true
            await gate.release(token)
        } catch { await gate.release(token); throw error }
    }
    public func signOut() async throws {
        let token = try await gate.acquire()
        do {
            try Task.checkCancellation(); try await storage.remove()
            cookies = []; state = .signedOut; generation &+= 1; loaded = true
            await gate.release(token)
        } catch { await gate.release(token); throw error }
    }
    public func allowExplicitRetry(now: Date = Date()) {
        if QQMusicCookiePolicy.usable(cookies, now: now) { state = .saved }
    }
    public func credentials(for url: URL, now: Date = Date()) async throws -> QQMusicRequestCredentials {
        guard QQMusicCookiePolicy.trusted(url) else { throw DropSpaceError.invalid("Untrusted QQ Music credential destination") }
        try await load(now: now)
        try Task.checkCancellation()
        if now < rateLimitUntil { throw QQMusicAccessError(code: 429) }
        if !cookies.isEmpty && !QQMusicCookiePolicy.usable(cookies, now: now) { state = .expired }
        if state == .expired || state == .rejected { throw QQMusicAccessError(code: 2001) }
        return QQMusicCookiePolicy.request(cookies, url: url, generation: generation, now: now)
    }
    public func reportAccess(generation run: UInt64, accepted: Bool, code: Int? = nil, now: Date = Date()) {
        guard generation == run else { return }
        if !accepted && code == 429 { rateLimitUntil = now.addingTimeInterval(30); return }
        guard QQMusicCookiePolicy.usable(cookies, now: now), state != .storageError else { return }
        if accepted { state = .connected }
        else if [1000, 2001, 101010, 401].contains(code ?? 0) { state = .expired }
    }
    public func applyResponseCookies(_ headers: [String], from url: URL, generation run: UInt64, now: Date = Date()) async throws {
        guard QQMusicCookiePolicy.trusted(url) else { return }
        let token = try await gate.acquire()
        do {
            try Task.checkCancellation()
            if generation == run, !cookies.isEmpty,
               let replacement = QQMusicCookiePolicy.responseCookies(cookies, headers: headers, url: url, now: now) {
                do {
                    try await persist(replacement)
                    cookies = replacement
                    if !QQMusicCookiePolicy.usable(replacement, now: now) { state = .expired }
                } catch is CancellationError { throw CancellationError() }
                catch { state = .storageError }
            }
            await gate.release(token)
        } catch { await gate.release(token); throw error }
    }
    private func persist(_ value: [QQMusicCookie]) async throws {
        var data = try JSONEncoder().encode(value)
        defer { data.resetBytes(in: data.startIndex..<data.endIndex) }
        // Native handoff/storage are bounded before writing, so the next launch
        // cannot inherit a session larger than the frozen 64KiB load budget.
        guard data.count <= 64 * 1024 else { throw DropSpaceError.limit("QQ session storage size") }
        try Task.checkCancellation(); try await storage.write(data)
    }
}
