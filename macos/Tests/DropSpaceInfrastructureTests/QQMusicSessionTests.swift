import Foundation
import Testing
@testable import DropSpaceInfrastructure

private actor QQSessionMemoryStorage: QQMusicCredentialStorage {
    var bytes: Data?
    var failWrites = false
    var failRemoval = false
    func read() -> Data? { bytes }
    func write(_ bytes: Data) throws {
        if failWrites { throw CocoaError(.fileWriteNoPermission) }
        self.bytes = bytes
    }
    func remove() throws {
        if failRemoval { throw CocoaError(.fileWriteNoPermission) }
        bytes = nil
    }
    func failures(write: Bool = false, remove: Bool = false) { failWrites = write; failRemoval = remove }
}

@Test func qqCookieIdentityAndHeadersMatchExecutedFrozenWindowsRules() throws {
    struct Fixture: Decodable {
        struct Case: Decodable {
            struct Header: Decodable { let url: String; let header: String }
            let input: [QQMusicCookie]; let accepted: [QQMusicCookie]; let usable: Bool
            let uin: String; let gtk: Int; let headers: [Header]
        }
        let baseline: String; let cases: [Case]
    }
    let url = try #require(Bundle.module.url(forResource: "qq-session-baseline", withExtension: "json", subdirectory: "Fixtures"))
    let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url))
    #expect(fixture.baseline == "1cd4620dd58a67b0963b73487743137813b8864a")
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
    for entry in fixture.cases {
        let accepted = QQMusicCookiePolicy.validated(entry.input)
        #expect(try encoder.encode(accepted) == encoder.encode(entry.accepted))
        #expect(QQMusicCookiePolicy.usable(entry.input) == entry.usable)
        for header in entry.headers {
            let credentials = QQMusicCookiePolicy.request(accepted, url: URL(string: header.url)!, generation: 0, now: Date())
            #expect(credentials.uin == entry.uin)
            #expect(credentials.gtk == entry.gtk)
            #expect(credentials.cookieHeader == header.header)
        }
    }
}

@Test func qqSessionBoundedCredentialsNeverEscapeMusicOrigins() async throws {
    let now = Date(timeIntervalSince1970: 1_700_000_000)
    let storage = QQSessionMemoryStorage(), session = QQMusicSession(storage: storage)
    let url = URL(string: "https://u.y.qq.com/cgi-bin/musicu.fcg")!
    let proposed = [
        QQMusicCookie(name: "qqmusic_key", value: "synthetic-old", domain: ".qq.com"),
        QQMusicCookie(name: "uin", value: "oo123456", domain: ".qq.com"),
        QQMusicCookie(name: "qqmusic_key", value: "synthetic-new", domain: ".qq.com"),
        QQMusicCookie(name: "skey", value: "must-not-retain", domain: ".qq.com"),
        QQMusicCookie(name: "qqmusic_key", value: "must-not-retain", domain: ".qq.com.attacker.test"),
        QQMusicCookie(name: "login_type", value: "1\r\nInjected:value", domain: ".qq.com"),
        QQMusicCookie(name: "wxuin", value: "2", domain: "u.y.qq.com", path: "/other")
    ]
    try await session.save(proposed, now: now)
    let credentials = try await session.credentials(for: url, now: now)
    #expect(credentials.uin == "123456")
    #expect(credentials.cookieHeader == "qqmusic_key=synthetic-new; uin=oo123456")
    #expect(!String(describing: credentials).contains("synthetic"))
    #expect(!String(reflecting: proposed[0]).contains("synthetic"))
    #expect(Mirror(reflecting: proposed[0]).children.isEmpty)
    for invalid in ["https://y.qq.com/", "http://u.y.qq.com/", "https://u.y.qq.com:444/", "https://user@u.y.qq.com/", "https://u.y.qq.com.attacker.test/"] {
        await #expect(throws: (any Error).self) {
            _ = try await session.credentials(for: URL(string: invalid)!, now: now)
        }
    }
    #expect(QQMusicCookiePolicy.validated(Array(repeating: proposed[3], count: 64) + [proposed[0]]).isEmpty)
    let exactHost = QQMusicCookie(name: "qqmusic_key", value: "host-only", domain: "y.qq.com")
    #expect(QQMusicCookiePolicy.request([exactHost], url: url, generation: 0, now: now).cookieHeader.isEmpty)
    let reopened = QQMusicSession(storage: storage)
    #expect(try await reopened.credentials(for: url, now: now).uin == "123456")
    #expect(await reopened.status.state == .saved)
}

@Test func qqSessionDurableRemovalGenerationExpiryAndCooldown() async throws {
    let now = Date(timeIntervalSince1970: 1_700_000_000), url = URL(string: "https://u.y.qq.com/cgi-bin/musicu.fcg")!
    let storage = QQSessionMemoryStorage(), session = QQMusicSession(storage: storage)
    let first = QQMusicCookie(name: "qqmusic_key", value: "ticket-one", domain: ".qq.com", expires: now.timeIntervalSince1970 + 60)
    try await session.save([first], now: now)
    let old = try await session.credentials(for: url, now: now)
    await session.reportAccess(generation: old.generation, accepted: true, now: now)
    #expect(await session.status.state == .connected)
    await storage.failures(remove: true)
    await #expect(throws: (any Error).self) { try await session.signOut() }
    #expect(await session.status.state == .connected)
    await storage.failures()
    try await session.signOut()
    await session.reportAccess(generation: old.generation, accepted: true, now: now)
    try await session.applyResponseCookies(["qqmusic_key=late; Domain=.qq.com; Path=/"], from: url, generation: old.generation, now: now)
    let signedOut = await session.status
    #expect(signedOut.state == .signedOut && !signedOut.hasSavedSession)
    #expect(try await session.credentials(for: url, now: now).cookieHeader.isEmpty)
    try await session.save([first], now: now)
    let current = try await session.credentials(for: url, now: now)
    await session.reportAccess(generation: current.generation, accepted: false, code: 429, now: now)
    await #expect(throws: QQMusicAccessError.self) { _ = try await session.credentials(for: url, now: now.addingTimeInterval(29)) }
    _ = try await session.credentials(for: url, now: now.addingTimeInterval(30))
    await session.reportAccess(generation: current.generation, accepted: false, code: 403, now: now.addingTimeInterval(30))
    #expect(await session.status.state == .saved)
    await session.reportAccess(generation: current.generation, accepted: false, code: 401, now: now.addingTimeInterval(30))
    #expect(await session.status.state == .expired)
    await session.allowExplicitRetry(now: now.addingTimeInterval(31))
    #expect(await session.status.state == .saved)
    await #expect(throws: QQMusicAccessError.self) { _ = try await session.credentials(for: url, now: now.addingTimeInterval(60)) }
    #expect(await session.status.state == .expired)
}

@Test func qqResponseRenewalAndDeletionPersistWithoutForgedExpiry() async throws {
    let now = Date(), url = URL(string: "https://u.y.qq.com/cgi-bin/musicu.fcg")!
    let storage = QQSessionMemoryStorage(), session = QQMusicSession(storage: storage)
    try await session.save([.init(name: "qqmusic_key", value: "initial-ticket", domain: ".qq.com")], now: now)
    let first = try await session.credentials(for: url, now: now)
    try await session.applyResponseCookies(["skey=must-not-retain; Domain=.qq.com; Path=/, qqmusic_key=renewed-ticket; Domain=.qq.com; Path=/; Secure; HttpOnly",
        "uin=must-not-retain; Domain=attacker.test; Path=/",
        "skey=must-not-retain; Domain=.qq.com; Path=/"], from: url, generation: first.generation, now: now)
    #expect(try await session.credentials(for: url, now: now).cookieHeader == "qqmusic_key=renewed-ticket")
    await storage.failures(write: true)
    try await session.applyResponseCookies(["qqmusic_key=failed-write; Domain=.qq.com; Path=/"], from: url, generation: first.generation, now: now)
    #expect(await session.status.state == .storageError)
    #expect(try await session.credentials(for: url, now: now).cookieHeader == "qqmusic_key=renewed-ticket")
    await storage.failures()
    try await session.applyResponseCookies(["qqmusic_key=deleted; Domain=.qq.com; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT"], from: url, generation: first.generation, now: now)
    #expect(await session.status.state == .expired)
    let reopened = QQMusicSession(storage: storage)
    try await reopened.load(now: now)
    #expect(await reopened.status.state == .expired)
    #expect(await reopened.status.hasSavedSession == false)
}

@Test func qqSessionCorruptStorageAndOversizedHandoffDoNotReplaceUsableState() async throws {
    let storage = QQSessionMemoryStorage(), url = URL(string: "https://u.y.qq.com/")!
    try await storage.write(Data("malformed session".utf8))
    let session = QQMusicSession(storage: storage)
    try await session.load()
    let failed = await session.status
    #expect(failed.state == .storageError && failed.hasSavedSession)
    try await session.save([.init(name: "qqmusic_key", value: "synthetic-valid", domain: ".qq.com")])
    let before = try await session.credentials(for: url)
    // Distinct allowed domains/names produce a valid field set larger than the
    // native envelope budget, which must fail before changing durable state.
    let oversized = [".qq.com", ".y.qq.com", "u.y.qq.com", "c.y.qq.com"].flatMap { domain in
        ["qqmusic_key", "qqmusic_uin", "qm_keyst", "uin", "wxuin", "login_type"].map {
            QQMusicCookie(name: $0, value: String(repeating: "x", count: 4096), domain: domain)
        }
    }
    await #expect(throws: (any Error).self) { try await session.save(oversized) }
    let after = try await session.credentials(for: url)
    #expect(after.generation == before.generation && after.cookieHeader == before.cookieHeader)
    let reopened = QQMusicSession(storage: storage)
    #expect(try await reopened.credentials(for: url).cookieHeader == before.cookieHeader)
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_QQ_KEYCHAIN"] == "1"))
func qqSyntheticSessionActuallyPersistsAndRemovesOwnKeychainItem() async throws {
    let keychain = KeychainStore(service: "io.github.airanluo-dot.DropSpace.validation.qq-session." + UUID().uuidString)
    let storage = QQMusicKeychainStorage(keychain: keychain), url = URL(string: "https://u.y.qq.com/")!
    let session = QQMusicSession(storage: storage)
    do {
        try await session.save([.init(name: "qqmusic_key", value: "DropSpace-local-test-not-an-account", domain: ".qq.com"),
                                .init(name: "qqmusic_uin", value: "123456", domain: ".qq.com")])
        let reopened = QQMusicSession(storage: storage)
        let restored = try await reopened.credentials(for: url)
        #expect(restored.uin == "123456")
        #expect(restored.cookieHeader.contains("DropSpace-local-test-not-an-account"))
        try await reopened.signOut()
        #expect(try await storage.read() == nil)
        #expect(await reopened.status.state == .signedOut)
    } catch {
        try await storage.remove()
        throw error
    }
}
