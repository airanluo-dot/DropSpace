import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func clipboardTransferRetainsRawTextAndAutomaticLimits() throws {
    let peer = UUID(), now = Date()
    let url = try ClipboardEnvelopePolicy.text(origin: peer, sequence: 0, text: "HTTPS://EXAMPLE.TEST:443/#key", kind: .url, now: now)
    try ClipboardEnvelopePolicy.validate(url)
    #expect(url.text == "HTTPS://EXAMPLE.TEST:443/#key")
    let text = try ClipboardEnvelopePolicy.text(origin: peer, sequence: 1, text: "a\r\nb\rc", now: now)
    #expect(text.text == "a\r\nb\rc")
    let threshold = try ClipboardEnvelopePolicy.text(origin: peer, sequence: 2, text: String(repeating: "x", count: 256 * 1024))
    #expect(ClipboardEnvelopePolicy.allowedAutomatically(threshold, mode: .textAndURL))
    #expect(!ClipboardEnvelopePolicy.allowedAutomatically(threshold, mode: .manual))
    var large = threshold; large.byteLength += 1
    #expect(!ClipboardEnvelopePolicy.allowedAutomatically(large, mode: .textURLAndImage))
    let image = try ClipboardEnvelopePolicy.image(origin: peer, sequence: 3, bytes: Data([0, 1, 2, 3]), mime: "image/png")
    try ClipboardEnvelopePolicy.validate(image)
    #expect(ClipboardEnvelopePolicy.allowedAutomatically(image, mode: .textURLAndImage))
    #expect(!ClipboardEnvelopePolicy.allowedAutomatically(image, mode: .textAndURL))
    var tampered = image; tampered.imageBytes = Data([0, 1, 2, 4])
    #expect(throws: (any Error).self) { try ClipboardEnvelopePolicy.validate(tampered) }
    var offset = text; offset.createdAtUtc = "2026-10-20T08:00:00+08:00"
    #expect(throws: (any Error).self) { try ClipboardEnvelopePolicy.validate(offset) }
    var old = text; old.createdAtUtc = "2000-01-01T00:00:00Z"
    try ClipboardEnvelopePolicy.validate(old) // Baseline has no creation-age rejection here.
    let wire = try DropLinkClipboardJSON.encode(ClipboardSyncRequest(peer: peer, envelope: image))
    let altered = String(decoding: wire, as: UTF8.self).replacingOccurrences(of: "\"originSequence\":3", with: "\"ORIGINSEQUENCE\":\"3\"").replacingOccurrences(of: "AAECAw==", with: "AAEC Aw==")
    let decoded = try DropLinkClipboardJSON.decode(ClipboardSyncRequest.self, from: Data(altered.utf8))
    #expect(decoded.envelope?.imageBytes == image.imageBytes)
    try ClipboardEnvelopePolicy.validate(#require(decoded.envelope))
}

@Test func clipboardLoopGuardIdentityExpiryAndFailedSendTombstones() async throws {
    let guarder = try ClipboardLoopGuard(maximum: 2, lifetime: 300), now = Date()
    let first = try ClipboardEnvelopePolicy.text(origin: UUID(), sequence: 1, text: "same")
    #expect(await guarder.accept(first, now: now))
    #expect(!(await guarder.accept(first, now: now.addingTimeInterval(300))))
    #expect(await guarder.accept(first, now: now.addingTimeInterval(300.001)))
    var second = first; second.eventId = UUID()
    #expect(await guarder.accept(second, now: now.addingTimeInterval(301)))
    var third = first; third.originDeviceId = UUID()
    #expect(await guarder.accept(third, now: now.addingTimeInterval(302)))
    #expect(await guarder.accept(first, now: now.addingTimeInterval(303))) // Oldest receipt was evicted.
    for _ in 0..<1000 {
        await guarder.remove(first, now: now.addingTimeInterval(304))
        #expect(await guarder.accept(first, now: now.addingTimeInterval(304)))
    }
    let counts = await guarder.counts()
    #expect(counts.entries == 2 && counts.order == 2)
}

private actor PropagationFixture {
    private var pending: CheckedContinuation<Void, Never>?
    var values: [String] = []
    func receive(_ item: DropItem) async throws {
        values.append(item.title)
        if item.title == "0" { await withCheckedContinuation { pending = $0 } }
        try Task.checkCancellation()
    }
    var blocked: Bool { pending != nil }
    func release() { pending?.resume(); pending = nil }
}
@Test func clipboardPropagationDropsOldestAndRetiresItsOwnGeneration() async throws {
    let fixture = PropagationFixture()
    let queue = ClipboardPropagationQueue(propagate: { try await fixture.receive($0) }, failure: { _ in Issue.record("Unexpected propagation failure") })
    func item(_ number: Int) -> DropItem { .init(source: .clipboard, kind: .text, title: String(number), text: String(number)) }
    await queue.enqueue(item(0))
    while !(await fixture.blocked) { await Task.yield() }
    for n in 1...20 { await queue.enqueue(item(n)) }
    await fixture.release()
    let deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while await fixture.values.count < 17, ContinuousClock.now < deadline { await Task.yield() }
    #expect(await fixture.values == ["0"] + (5...20).map(String.init))
    await queue.stop()
    #expect(!(await queue.enqueue(item(21))))
}

@Test func clipboardHostBindsOriginAndDistinguishesPausedUnavailableInvalid() async throws {
    let peer = UUID(), envelope = try ClipboardEnvelopePolicy.text(origin: peer, sequence: 1, text: "fixture")
    func request(_ value: ClipboardEnvelope) throws -> DropLinkHTTPRequest { .init(head: .init(method: "POST", path: DropLinkRoute.clipboard, headers: [:]), body: try DropLinkClipboardJSON.encode(ClipboardSyncRequest(peer: peer, envelope: value)), peerID: peer, address: "127.0.0.1") }
    let paused = DropLinkClipboardHost { _ in throw ClipboardReceiveError.paused }
    let unavailable = DropLinkClipboardHost(receive: nil)
    let failure = DropLinkClipboardHost { _ in throw DropSpaceError.storage("Injected write failure") }
    let pausedResponse = try await paused.handle(request(envelope))
    #expect(pausedResponse.status == 200)
    #expect(try DropLinkClipboardJSON.decode(ClipboardSyncResponse.self, from: pausedResponse.body).errorCategory == "clipboard-paused")
    let unavailableResponse = try await unavailable.handle(request(envelope))
    #expect(try DropLinkClipboardJSON.decode(ClipboardSyncResponse.self, from: unavailableResponse.body).errorCategory == "clipboard-receiver-unavailable")
    #expect(try await failure.handle(request(envelope)).status == 400)
    var wrong = envelope; wrong.originDeviceId = UUID()
    let origin = try await paused.handle(request(wrong))
    #expect(origin.status == 400)
    #expect(try DropLinkClipboardJSON.decode(ClipboardSyncResponse.self, from: origin.body).errorCategory == "origin-device-mismatch")
}

@Test func clipboardFixedWindowsPolicyVectors() throws {
    struct Input: Decodable { let name: String; let text: String?; let kind: Int?; let `repeat`: Int?; let image: Bool?; let mutation: String? }
    struct Row: Decodable { let input: Input; let created: Bool; let valid: Bool; let hash: String?; let bytes: Int64?; let mime: String?; let automatic: [Bool]? }
    struct Fixture: Decodable { let baseline: String; let cases: [Row] }
    let data = try Data(contentsOf: #require(Bundle.module.url(forResource: "clipboard-baseline", withExtension: "json", subdirectory: "Fixtures")))
    let fixture = try JSONDecoder().decode(Fixture.self, from: data)
    #expect(fixture.baseline == Baseline.commit)
    for row in fixture.cases {
        let input = row.input
        var value: ClipboardEnvelope
        do {
            if input.image == true { value = try ClipboardEnvelopePolicy.image(origin: UUID(), sequence: 1, bytes: Data(repeating: 65, count: input.repeat ?? 1), mime: "image/png") }
            else { value = try ClipboardEnvelopePolicy.text(origin: UUID(), sequence: 1, text: String(repeating: input.text ?? "", count: input.repeat ?? 1), kind: #require(ClipboardPayloadKind(rawValue: input.kind ?? 1))) }
        } catch { #expect(!row.created, "Unexpected create rejection: \(input.name)"); continue }
        #expect(row.created, "Unexpected create acceptance: \(input.name)")
        switch input.mutation {
        case "empty-event": value.eventId = .zero
        case "empty-origin": value.originDeviceId = .zero
        case "negative-sequence": value.originSequence = -1
        case "zero-bytes": value.byteLength = 0
        case "bad-hash": value.sha256 = String(repeating: "0", count: 64)
        case "upper-hash": value.sha256 = value.sha256.uppercased()
        case "blank-mime": value.mime = " "
        case "long-mime": value.mime = String(repeating: "a", count: 129)
        case "offset": value.createdAtUtc = "2026-10-20T08:00:00+08:00"
        case "ancient": value.createdAtUtc = "0001-01-01T00:00:00+00:00"
        case "opposite-payload": value.imageBytes = Data([1, 2])
        default: break
        }
        #expect(value.sha256 == row.hash && value.byteLength == row.bytes && value.mime == row.mime, "Payload contract: \(input.name)")
        #expect(ClipboardSyncMode.allCases.map { ClipboardEnvelopePolicy.allowedAutomatically(value, mode: $0) } == row.automatic, "Automatic policy: \(input.name)")
        do { try ClipboardEnvelopePolicy.validate(value); #expect(row.valid, "Unexpected validation acceptance: \(input.name)") }
        catch { #expect(!row.valid, "Unexpected validation rejection: \(input.name)") }
    }
}
