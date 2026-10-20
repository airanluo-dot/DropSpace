import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private actor ResponseFixture: LyricsTransport {
    var calls: [URL] = []
    var pending: [Int: CheckedContinuation<LyricsJSON, any Error>] = [:]
    func request(_ url: URL, json: Data?, referer: URL?) async throws -> LyricsJSON {
        let index = calls.count; calls.append(url)
        return try await withCheckedThrowingContinuation { pending[index] = $0 }
    }
    func succeed(_ index: Int, _ payload: String) throws {
        let value = try JSONDecoder().decode(LyricsJSON.self, from: Data(payload.utf8))
        pending.removeValue(forKey: index)?.resume(returning: value)
    }
    func fail(_ index: Int) { pending.removeValue(forKey: index)?.resume(throwing: URLError(.networkConnectionLost)) }
    func drain() { let owned = pending.values; pending = [:]; for item in owned { item.resume(throwing: CancellationError()) } }
}
private final class ResponseClock: @unchecked Sendable {
    private let lock = NSLock()
    private var instant = ContinuousClock.now
    var now: ContinuousClock.Instant { lock.withLock { instant } }
    func advance(_ duration: Duration) { lock.withLock { instant = instant.advanced(by: duration) } }
}
private func awaitRequests(_ fixture: ResponseFixture, _ count: Int) async throws {
    let deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while await fixture.calls.count < count, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(5)) }
    #expect(await fixture.calls.count == count)
}
private let searchURL = URL(string: "https://music.163.com/api/search/get/web?s=Song")!
private let lyricURL = URL(string: "https://music.163.com/api/song/lyric?id=1")!
private let oldSearch = #"{"code":200,"result":{"songs":[{"id":1,"name":"Old recording"}]}}"#
private let freshSearch = #"{"code":200,"result":{"songs":[{"id":2,"name":"New recording"}]}}"#
private let originalOnly = #"{"code":200,"lrc":{"lyric":"[00:01]这是需要翻译的原文"},"tlyric":null}"#

@Test func netEaseResponseCacheCoalescesWaitersAndTransfersCancelledOwnership() async throws {
    let fixture = ResponseFixture(), cache = NetEaseResponseCache(http: fixture)
    let owner = Task { try await cache.get(searchURL, refresh: false, target: "en") }
    try await awaitRequests(fixture, 1)
    let survivor = Task { try await cache.get(searchURL, refresh: false, target: "en") }
    let waiter = Task { try await cache.get(searchURL, refresh: false, target: "en") }
    try await Task.sleep(for: .milliseconds(30)); waiter.cancel()
    do { _ = try await waiter.value; Issue.record("Cancelled cache waiter returned a value") } catch is CancellationError {}
    #expect(await fixture.calls.count == 1)
    owner.cancel()
    do { _ = try await owner.value; Issue.record("Cancelled cache owner returned a value") } catch is CancellationError {}
    try await awaitRequests(fixture, 2)
    // Old HTTP ignores cancellation. Its result cannot overwrite the new owner.
    try await fixture.succeed(0, oldSearch); try await fixture.succeed(1, freshSearch)
    #expect(try await survivor.value["result"]["songs"].array.first?["id"].text == "2")
    #expect(try await cache.get(searchURL, refresh: false, target: "zh")["result"]["songs"].array.first?["id"].text == "2")
    #expect(await fixture.calls.count == 2)
    await fixture.drain()
}

@Test func netEaseResponseCacheRefreshClearAndFailureFenceOldResponses() async throws {
    let fixture = ResponseFixture(), cache = NetEaseResponseCache(http: fixture)
    let old = Task { try await cache.get(searchURL, refresh: false, target: "en") }
    try await awaitRequests(fixture, 1)
    let fresh = Task { try await cache.get(searchURL, refresh: true, target: "en") }
    try await awaitRequests(fixture, 2)
    try await fixture.succeed(1, freshSearch); _ = try await fresh.value
    try await fixture.succeed(0, oldSearch); _ = try await old.value
    #expect(try await cache.get(searchURL, refresh: false, target: "en")["result"]["songs"].array.first?["id"].text == "2")
    let interrupted = Task { try await cache.get(searchURL, refresh: true, target: "en") }
    try await awaitRequests(fixture, 3); await cache.clear(); try await fixture.succeed(2, oldSearch); _ = try await interrupted.value
    let afterClear = Task { try await cache.get(searchURL, refresh: false, target: "en") }
    try await awaitRequests(fixture, 4); await fixture.fail(3)
    do { _ = try await afterClear.value; Issue.record("Network failure returned a response") } catch is URLError {}
    let retry = Task { try await cache.get(searchURL, refresh: false, target: "en") }
    try await awaitRequests(fixture, 5); try await fixture.succeed(4, freshSearch); _ = try await retry.value
    await fixture.drain()
}

@Test func netEaseResponseCacheUsesShortIncompleteAndChangedTargetLifetimes() async throws {
    let fixture = ResponseFixture(), clock = ResponseClock()
    let cache = NetEaseResponseCache(http: fixture, now: { clock.now })
    let first = Task { try await cache.get(lyricURL, refresh: false, target: "en") }
    try await awaitRequests(fixture, 1); try await fixture.succeed(0, originalOnly); _ = try await first.value
    clock.advance(.milliseconds(999)); _ = try await cache.get(lyricURL, refresh: false, target: "en")
    #expect(await fixture.calls.count == 1)
    clock.advance(.milliseconds(1))
    let expired = Task { try await cache.get(lyricURL, refresh: false, target: "") }
    try await awaitRequests(fixture, 2); try await fixture.succeed(1, originalOnly); _ = try await expired.value
    clock.advance(.seconds(2)); _ = try await cache.get(lyricURL, refresh: false, target: "")
    #expect(await fixture.calls.count == 2)
    let changed = Task { try await cache.get(lyricURL, refresh: false, target: "zh-Hans") }
    try await awaitRequests(fixture, 3); try await fixture.succeed(2, originalOnly); _ = try await changed.value
    clock.advance(.seconds(1))
    let denied = Task { try await cache.get(lyricURL, refresh: false, target: "zh-Hans") }
    try await awaitRequests(fixture, 4); try await fixture.succeed(3, #"{"code":429,"lrc":{"lyric":"[00:01]denied"}}"#); _ = try await denied.value
    let recovered = Task { try await cache.get(lyricURL, refresh: false, target: "zh-Hans") }
    try await awaitRequests(fixture, 5); try await fixture.succeed(4, originalOnly); _ = try await recovered.value
    await fixture.drain()
}

@Test func netEaseResponseCacheDoesNotEvictActiveOwnersOrRetainOversizedPayload() async throws {
    let fixture = ResponseFixture(), cache = NetEaseResponseCache(http: fixture, maximumEntries: 2, maximumBytes: 128)
    let urls = (0..<3).map { URL(string: "https://music.163.com/api/search/get/web?s=\($0)")! }
    var tasks: [Task<LyricsJSON, any Error>] = []
    for (index, url) in urls.enumerated() {
        tasks.append(Task { try await cache.get(url, refresh: false, target: "en") })
        try await awaitRequests(fixture, index + 1)
    }
    for index in 0..<3 { try await fixture.succeed(index, freshSearch); _ = try await tasks[index].value }
    let uncached = Task { try await cache.get(urls[2], refresh: false, target: "en") }
    try await awaitRequests(fixture, 4)
    let oversized = #"{"code":200,"result":{"songs":[{"id":9,"name":""# + String(repeating: "x", count: 200) + #""}]}}"#
    try await fixture.succeed(3, oversized); _ = try await uncached.value
    let retry = Task { try await cache.get(urls[2], refresh: false, target: "en") }
    try await awaitRequests(fixture, 5); try await fixture.succeed(4, freshSearch); _ = try await retry.value
    await fixture.drain()
}
