import Testing
import Foundation
import CryptoKit
import DropSpaceCore
@testable import DropSpaceInfrastructure

private struct PairingVector: Decodable {
    let local: PairingHello; let remote: PairingHello
    let secret: String; let sas: Int; let nonce: String; let bodyHash: String; let authentication: String
}
@Test func dropLinkPinnedTranscriptAndDotNetDerivationVector() throws {
    let vector = try JSONDecoder().decode(PairingVector.self,from: Data(contentsOf: Bundle.module.url(forResource:"droplink-v1",withExtension:"json",subdirectory:"Fixtures")!))
    let key = try P256.KeyAgreement.PrivateKey(rawRepresentation: Data(Array(repeating:UInt8(0),count:31)+[1]))
    let secret = try DropLinkCrypto.deriveSecret(privateKey:key,local:vector.local,remote:vector.remote)
    let hex = secret.withUnsafeBytes { $0.map { String(format:"%02x",$0) }.joined() }
    #expect(hex == vector.secret)
    #expect(DropLinkCrypto.sas(secret:secret,first:vector.local,second:vector.remote) == vector.sas)
    #expect(DropLinkCrypto.sas(secret:secret,first:vector.remote,second:vector.local) == vector.sas)
    #expect(DropLinkCrypto.authentication(secret:secret,method:"post",path:"/v1/clipboard",nonce:vector.nonce,bodyHash:vector.bodyHash) == vector.authentication)
    #expect(DropLinkCrypto.verifyAuthentication(vector.authentication,secret:secret,method:"POST",path:"/v1/clipboard",nonce:vector.nonce,bodyHash:vector.bodyHash))
    #expect(!DropLinkCrypto.verifyAuthentication(vector.authentication,secret:secret,method:"POST",path:"/v1/clipboard/",nonce:vector.nonce,bodyHash:vector.bodyHash))
    #expect(vector.remote.platform == .macOS)
    #expect(try JSONSerialization.jsonObject(with: JSONEncoder().encode(vector.remote)) as? [String:Any] != nil)
}
@Test func dropLinkNonceReservationAndRoutingBoundaries() async throws {
    let cache = DropLinkNonceCache(), peer = UUID(), now = Date()
    #expect(await cache.reserve(peer:peer,nonce:"abc",now:now))
    #expect(!(await cache.reserve(peer:peer,nonce:"abc",now:now.addingTimeInterval(600))))
    #expect(await cache.reserve(peer:peer,nonce:"abc",now:now.addingTimeInterval(601)))
    await cache.release(peer:peer,nonce:"abc")
    #expect(await cache.reserve(peer:peer,nonce:"abc",now:now))
    #expect(!(await cache.reserve(peer:.zero,nonce:"abc",now:now)))
    #expect(DropLinkRoute.requiresAuthentication("/V1/CLIPBOARD///"))
    #expect(!DropLinkRoute.requiresAuthentication("/v1/clipboard-evil"))
    #expect(DropLinkRoute.isPairing("/V1/PAIRING/HELLO/"))
    #expect(DropLinkRoute.bodyLimit("/v1/clipboard/") == 69_970_604)
}
@Test func retentionCannotDestroyActiveUnpinUndo() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at:root) }
    let store = try ItemStore(directory:root)
    var item = DropItem(source:.clipboard,kind:.text,title:"protected",text:"protected"); item.isPinned = true; item.createdAt = Date.distantPast
    try await store.insert([item]); let now = Date()
    let pending = try await store.setPinned([item.id],pinned:false,now:now); let receipt = try #require(pending)
    try await store.applyRetention(days:1,count:1,now:now)
    try await store.undo(receipt.id,now:now.addingTimeInterval(1))
    #expect(try await store.items().first?.isPinned == true)
}
@Test func atomicPublishPreservesExistingDestination() throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
    defer { try? FileManager.default.removeItem(at:root) }
    let target = root.appendingPathComponent("out.txt")
    try AtomicFile.writeNew(Data("original".utf8),to:target)
    #expect(throws:(any Error).self) { try AtomicFile.writeNew(Data("replacement".utf8),to:target) }
    #expect(try String(contentsOf:target,encoding:.utf8) == "original")
    #expect(try FileManager.default.contentsOfDirectory(atPath:root.path) == ["out.txt"])
}
