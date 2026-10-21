import Foundation
import CryptoKit
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func dropLinkHandoffFixedBaselineVectors() throws {
    struct Fixture: Decodable {
        struct Row: Decodable { let kind: Int; let payload: String; let accepted: Bool; let normalized: String?; let bytes: Int64?; let hash: String? }
        let baseline: String; let cases: [Row]
    }
    let data = try Data(contentsOf: #require(Bundle.module.url(forResource: "handoff-baseline", withExtension: "json", subdirectory: "Fixtures")))
    let fixture = try JSONDecoder().decode(Fixture.self, from: data)
    #expect(fixture.baseline == "1cd4620dd58a67b0963b73487743137813b8864a")
    for row in fixture.cases {
        do {
            let message = try HandoffMessagePolicy.create(sender: UUID(), name: " Fixture ", kind: #require(HandoffMessageKind(rawValue: row.kind)), payload: row.payload, label: " label ")
            #expect(row.accepted, "Unexpected acceptance: \(row.payload)")
            #expect(message.utf8Payload.utf8.elementsEqual((row.normalized ?? "").utf8), "Normalization: \(row.payload)")
            #expect(message.byteLength == row.bytes, "Byte count: \(row.payload)")
            #expect(message.sha256 == row.hash, "Hash: \(row.payload)")
        } catch { #expect(!row.accepted, "Unexpected rejection: \(row.payload): \(error)") }
    }
}

@Test func dropLinkHandoffIntegrityWireAndUTF16Limits() throws {
    let valid = try HandoffMessagePolicy.create(sender: UUID(), name: String(repeating: "😀", count: 32), kind: .text, payload: "é e\u{301}\r\n\u{0}tail", label: String(repeating: "😀", count: 80))
    let data = try DropLinkHandoffJSON.encode(HandoffMessageRequest(peerId: valid.senderDeviceId, message: valid))
    let alteredNames = String(decoding: data, as: UTF8.self).replacingOccurrences(of: "\"byteLength\":\(valid.byteLength)", with: "\"BYTELENGTH\":\"\(valid.byteLength)\"")
    let decoded = try DropLinkHandoffJSON.decode(HandoffMessageRequest.self, from: Data(alteredNames.utf8))
    try HandoffMessagePolicy.validate(#require(decoded.message))
    var changed = valid; changed.byteLength += 1
    #expect(throws: (any Error).self) { try HandoffMessagePolicy.validate(changed) }
    changed = valid; changed.sha256 = String(repeating: "0", count: 64)
    #expect(throws: (any Error).self) { try HandoffMessagePolicy.validate(changed) }
    changed = valid; changed.createdAtUtc = "2026-10-20T08:00:00+08:00"
    #expect(throws: (any Error).self) { try HandoffMessagePolicy.validate(changed) }
    #expect(throws: (any Error).self) { try HandoffMessagePolicy.create(sender: UUID(), name: String(repeating: "😀", count: 33), kind: .text, payload: "a") }
    #expect(throws: (any Error).self) { try HandoffMessagePolicy.create(sender: UUID(), name: "x", kind: .text, payload: "a", label: String(repeating: "😀", count: 81)) }
    #expect(throws: (any Error).self) { try HandoffMessagePolicy.create(sender: UUID(), name: "x", kind: .text, payload: String(repeating: "a", count: 1048577)) }
}

@Test func dropLinkHandoffReplayCapacityAndExactExpiry() async {
    let cache = DropLinkHandoffReplayCache(), now = Date(), peer = UUID(), session = UUID()
    #expect(await cache.reserve(peer: peer, session: session, now: now))
    #expect(!(await cache.reserve(peer: peer, session: session, now: now)))
    for _ in 1..<256 { #expect(await cache.reserve(peer: peer, session: UUID(), now: now)) }
    #expect(!(await cache.reserve(peer: peer, session: UUID(), now: now)))
    for _ in 1..<16 { let other = UUID(); for _ in 0..<256 { #expect(await cache.reserve(peer: other, session: UUID(), now: now)) } }
    #expect(!(await cache.reserve(peer: UUID(), session: UUID(), now: now)))
    #expect(!(await cache.reserve(peer: peer, session: session, now: now.addingTimeInterval(600))))
    #expect(await cache.reserve(peer: peer, session: session, now: now.addingTimeInterval(600.001)))
    #expect(!(await cache.reserve(peer: .zero, session: UUID(), now: now)))
}

@Test func dropLinkHandoffHostRejectionOriginExpiryAndFailedCommit() async throws {
    let peer = UUID(), now = Date(), message = try HandoffMessagePolicy.create(sender: peer, name: "Peer", kind: .text, payload: "hello", now: now)
    func request(_ message: HandoffMessage, claimed: UUID? = nil) throws -> DropLinkHTTPRequest {
        .init(head: .init(method: "POST", path: DropLinkRoute.handoffText, headers: [:]), body: try DropLinkHandoffJSON.encode(HandoffMessageRequest(peerId: claimed ?? peer, message: message)), peerID: peer, address: "127.0.0.1")
    }
    func category(_ response: DropLinkHTTPResponse) throws -> String? { try DropLinkHandoffJSON.decode(HandoffMessageResponse.self, from: response.body).errorCategory }
    let reject = DropLinkHandoffHost { _ in false }
    #expect(try category(await reject.handle(request(message), now: now)) == "rejected")
    #expect(try category(await reject.handle(request(message), now: now)) == "duplicate-session")
    let fail = DropLinkHandoffHost { _ in throw DropSpaceError.storage("Injected disk failure") }
    #expect(try category(await fail.handle(request(message), now: now)) == "handoff-invalid")
    #expect(try category(await DropLinkHandoffHost(receive: nil).handle(request(message), now: now)) == "handoff-receiver-unavailable")
    #expect(try await reject.handle(request(message, claimed: UUID()), now: now).status == 401)
    var spoof = message; spoof.senderDeviceId = UUID()
    #expect(try await reject.handle(request(spoof), now: now).status == 400)
    #expect(try category(await DropLinkHandoffHost { _ in true }.handle(request(message), now: now.addingTimeInterval(600.001))) == "expired")
    #expect(try category(await DropLinkHandoffHost { _ in true }.handle(request(message), now: now.addingTimeInterval(-60.001))) == "expired")
}
