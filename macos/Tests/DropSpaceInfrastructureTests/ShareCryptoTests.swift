import Foundation
import CryptoKit
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func shareCryptoFixedBaselineBidirectionalWireAndTamper() throws {
    struct Fixture: Decodable {
        struct Chunk: Decodable { let index: Int32; let plain: Data; let wire: Data }
        let baseline: String, key: Data, share: UUID, file: UUID, prefix: Data, nonce: Data
        let items: [EncryptedShareManifestItem], chunks: [Chunk], manifestWire: Data, guidWire: Data, fragment: String
    }
    let url = try #require(Bundle.module.url(forResource: "share-crypto-baseline", withExtension: "json", subdirectory: "Fixtures"))
    let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url)), key = SymmetricKey(data: fixture.key)
    #expect(fixture.baseline == Baseline.commit)
    #expect(ShareCrypto.guidWireBytes(fixture.share) == fixture.guidWire)
    #expect(try ShareCrypto.fragment(key) == fixture.fragment)
    #expect(try ShareCrypto.fragment(ShareCrypto.key(fragment: "#k=" + fixture.fragment)) == fixture.fragment)
    #expect(try ShareCrypto.decryptManifest(key: key, share: fixture.share, wire: fixture.manifestWire) == fixture.items)
    for chunk in fixture.chunks {
        #expect(try ShareCrypto.encryptChunk(key: key, share: fixture.share, file: fixture.file, index: chunk.index, plaintext: chunk.plain, noncePrefix: fixture.prefix) == chunk.wire)
        #expect(try ShareCrypto.decryptChunk(key: key, share: fixture.share, file: fixture.file, index: chunk.index, plainLength: chunk.plain.count, wire: chunk.wire, noncePrefix: fixture.prefix) == chunk.plain)
        var corrupt = chunk.wire; corrupt[corrupt.count - 1] ^= 1
        #expect(throws: (any Error).self) { try ShareCrypto.decryptChunk(key: key, share: fixture.share, file: fixture.file, index: chunk.index, plainLength: chunk.plain.count, wire: corrupt, noncePrefix: fixture.prefix) }
    }
    let native = try ShareCrypto.encryptManifest(key: key, share: fixture.share, items: fixture.items, nonce: fixture.nonce)
    #expect(try ShareCrypto.decryptManifest(key: key, share: fixture.share, wire: native) == fixture.items)
    #expect(throws: (any Error).self) { try ShareCrypto.decryptManifest(key: key, share: UUID(), wire: native) }
    #expect(throws: (any Error).self) { try ShareCrypto.key(fragment: "#k=bad") }
    if let output = ProcessInfo.processInfo.environment["DROPSPACE_SHARE_CRYPTO_EXPORT"] {
        let first = try #require(fixture.chunks.first)
        let chunk = try ShareCrypto.encryptChunk(key: key, share: fixture.share, file: fixture.file, index: first.index, plaintext: first.plain, noncePrefix: fixture.prefix)
        try JSONSerialization.data(withJSONObject: ["manifestWire": native.base64EncodedString(), "chunkWire": chunk.base64EncodedString()]).write(to: URL(fileURLWithPath: output), options: .atomic)
    }
}
