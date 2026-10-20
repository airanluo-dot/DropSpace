import Foundation
import Darwin
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func transferFixedBaselinePolicyAndOrdinalVectors() throws {
    struct Fixture: Decodable {
        struct Row: Decodable { let name: String; let manifest: TransferManifest; let valid: Bool }
        struct Path: Decodable { let input: String; let result: String? }
        struct Comparison: Decodable { let left: String; let right: String; let equal: Bool }
        let baseline: String; let cases: [Row]; let paths: [Path]; let comparisons: [Comparison]
    }
    let data = try Data(contentsOf: #require(Bundle.module.url(forResource: "transfer-baseline", withExtension: "json", subdirectory: "Fixtures")))
    let fixture = try JSONDecoder().decode(Fixture.self, from: data)
    #expect(fixture.baseline == "1cd4620dd58a67b0963b73487743137813b8864a")
    for row in fixture.cases {
        do { try TransferManifestPolicy.validate(row.manifest, filenames: .macOS); #expect(row.valid, "Accepted \(row.name)") }
        catch { #expect(!row.valid, "Rejected \(row.name): \(error)") }
    }
    for row in fixture.paths {
        let actual = try? TransferManifestPolicy.normalizeRelativePath(row.input, filenames: .macOS)
        #expect(actual == row.result, "Path \(row.input)")
    }
    for row in fixture.comparisons {
        #expect((TransferManifestPolicy.ordinalIgnoreCaseKey(row.left) == TransferManifestPolicy.ordinalIgnoreCaseKey(row.right)) == row.equal, "Ordinal \(row.left) / \(row.right)")
    }
    // Windows-specific restrictions are a source-contract assertion, not a
    // claim that the C# fixture ran on a Windows filesystem.
    for path in ["a:colon", "a?query", "a\u{1f}b", "a<b", "a|b"] {
        #expect(throws: (any Error).self) { try TransferManifestPolicy.normalizeRelativePath(path, filenames: .windows) }
    }
}

@Test func transferWireArraysDefaultsAndQuotedIntegers() throws {
    let json = """
    {"PEERID":"11111111-2222-3333-4444-555555555555","MANIFEST":{"SESSIONID":"22222222-2222-3333-4444-555555555555","PROTOCOL":{"MAJOR":"1","MINOR":"0"},"ITEMS":[{"ID":"33333333-2222-3333-4444-555555555555","KIND":"1","DISPLAYNAME":"a","RELATIVEPATH":"wrong","relativePath":"a","SIZE":"0","SHA256":"\(Digests.sha256(Data()))","MIMETYPE":"text/plain","CHUNKCOUNT":"0"}],"TOTALBYTES":"0","CREATEDATUTC":"2026-10-20T08:00:00+08:00"}}
    """
    let request = try DropLinkTransferJSON.decode(TransferOfferRequest.self, from: Data(json.utf8))
    try TransferManifestPolicy.validate(request.manifest)
    #expect(request.manifest.items[0].relativePath == "a")
    #expect(request.manifest.chunkBytes == 4_194_304)
    let response = TransferStatusResponse(session: request.manifest.sessionId, state: .accepted, bytes: 0, chunks: [request.manifest.items[0].id.dropLinkD: [0, 2]], completed: [])
    let wire = try ModuleJSON.decode(DropLinkTransferJSON.encode(response))
    guard case .object = wire["receivedChunks"] else { Issue.record("Chunk map must be a JSON object"); return }
    let id = request.manifest.items[0].id.dropLinkD
    let status = """
    {"sessionId":"\(request.manifest.sessionId.dropLinkD)","state":"4","transferredBytes":"1","receivedChunks":{"\(id.uppercased())":["1"],"\(id)":["2"]},"completedRelativePaths":[]}
    """
    let parsed = try DropLinkTransferJSON.decode(TransferStatusResponse.self, from: Data(status.utf8))
    #expect(parsed.receivedChunks == [id: [2]])
    #expect(throws: (any Error).self) { try DropLinkTransferJSON.decode(TransferOfferRequest.self, from: Data(json.replacingOccurrences(of: "\"0\",\"SHA256\"", with: "\"9223372036854775808\",\"SHA256\"").utf8)) }
}

@Test func transferFixedBaselineMissingNullAndTimestampVectors() throws {
    struct Fixture: Decodable {
        struct Row: Decodable { let name: String; let json: String; let valid: Bool }
        let wires: [Row]
    }
    let data = try Data(contentsOf: #require(Bundle.module.url(forResource: "transfer-baseline", withExtension: "json", subdirectory: "Fixtures")))
    for row in try JSONDecoder().decode(Fixture.self, from: data).wires {
        do {
            let manifest = try DropLinkTransferJSON.decode(TransferManifest.self, from: Data(row.json.utf8))
            try TransferManifestPolicy.validate(manifest, filenames: .macOS)
            #expect(row.valid, "Accepted \(row.name)")
        } catch { #expect(!row.valid, "Rejected \(row.name): \(error)") }
    }
    #expect(try !DropLinkTransferJSON.decode(TransferAcceptRequest.self, from: Data("{}".utf8)).accepted)
    #expect(throws: (any Error).self) { try DropLinkTransferJSON.decode(TransferAcceptRequest.self, from: Data("{\"accepted\":null}".utf8)) }
}

@Test func transferSourceTraversalOwnershipAndLimits() async throws {
    let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath().appendingPathComponent("DropSpace-Transfer-" + UUID().dropLinkN)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
    let folder = root.appendingPathComponent("Folder"), empty = folder.appendingPathComponent("empty"), file = folder.appendingPathComponent("a.txt"), zero = folder.appendingPathComponent("zero.bin")
    try FileManager.default.createDirectory(at: empty, withIntermediateDirectories: true)
    try Data("original-source".utf8).write(to: file); try Data().write(to: zero)
    try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("skip-link"), withDestinationURL: root)
    let preparation = TransferSourcePreparation(), plan = try await preparation.prepare([folder])
    #expect(plan.manifest.items.count == 2); #expect(plan.manifest.totalBytes == 15)
    #expect(Set(plan.manifest.items.map(\.relativePath)) == ["Folder/a.txt", "Folder/zero.bin"])
    let item = try #require(plan.manifest.items.first { $0.relativePath.hasSuffix("a.txt") }), source = try #require(plan.sources[item.id])
    #expect(item.sha256 == Digests.sha256(Data("original-source".utf8)))
    try source.validate(TransferFileSystem.file(file).info())
    try Data("mutated--source".utf8).write(to: file)
    #expect(throws: (any Error).self) { try source.validate(TransferFileSystem.file(file).info()) }
    await #expect(throws: (any Error).self) { try await preparation.prepare([empty]) }
    await #expect(throws: (any Error).self) { try await preparation.prepare([folder.appendingPathComponent("skip-link")]) }
    let alias = root.appendingPathComponent("alias"); try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: folder)
    await #expect(throws: (any Error).self) { try await preparation.prepare([alias.appendingPathComponent("a.txt")]) }
    var limits = TransferLimits(); limits.maxItems = 1
    await #expect(throws: (any Error).self) { try await preparation.prepare([folder], limits: limits) }
    limits = .init(); limits.maxTotalBytes = 1
    await #expect(throws: (any Error).self) { try await preparation.prepare([file], limits: limits) }
    let fifo = root.appendingPathComponent("pipe"); #expect(mkfifo(fifo.path, 0o600) == 0)
    await #expect(throws: (any Error).self) { try await preparation.prepare([fifo]) }
    #expect(try Data(contentsOf: file) == Data("mutated--source".utf8))
}

@Test func transferReceiveFilesResumeIntegrityCollisionAndOwnedCleanup() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-Receive-" + UUID().dropLinkN)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
    let destination = root.appendingPathComponent("Downloads"), staging = root.appendingPathComponent("staging")
    let bytes = Data(repeating: 42, count: 65536 + 17)
    let first = TransferItemManifest(name: "first", path: "Folder/first", size: Int64(bytes.count), hash: Digests.sha256(bytes), mime: "application/octet-stream", chunks: 2)
    let empty = TransferItemManifest(name: "empty", path: "Folder/empty", size: 0, hash: Digests.sha256(Data()), mime: "application/octet-stream", chunks: 0)
    var limits = TransferLimits(); limits.chunkBytes = 65536
    let manifest = try TransferManifestPolicy.create(items: [first, empty], limits: limits)
    let files = try TransferReceiveFiles(parentURL: staging, manifest: manifest, destination: destination)
    let chunks = [Data(bytes.prefix(65536)), Data(bytes.suffix(17))]
    #expect(try await files.write(item: first, index: 1, body: chunks[1], hash: Digests.sha256(chunks[1])))
    #expect(try await !files.write(item: first, index: 1, body: chunks[1], hash: Digests.sha256(chunks[1])))
    await #expect(throws: (any Error).self) { try await files.commit(first) }
    #expect(!FileManager.default.fileExists(atPath: destination.appendingPathComponent("Folder/first").path))
    #expect(try await files.write(item: first, index: 0, body: chunks[0], hash: Digests.sha256(chunks[0])))
    #expect(try await files.commit(first) == "Folder/first")
    #expect(try Data(contentsOf: destination.appendingPathComponent("Folder/first")) == bytes)
    // A finalization retry cannot overwrite even an identical published file.
    await #expect(throws: (any Error).self) { try await files.commit(first) }
    try Data("user-existing".utf8).write(to: destination.appendingPathComponent("Folder/empty"))
    await #expect(throws: (any Error).self) { try await files.commit(empty) }
    #expect(try Data(contentsOf: destination.appendingPathComponent("Folder/empty")) == Data("user-existing".utf8))
    let lease = staging.appendingPathComponent(manifest.sessionId.dropLinkN), unknown = lease.appendingPathComponent("unknown")
    try Data("do-not-remove".utf8).write(to: unknown)
    await #expect(throws: (any Error).self) { try await files.cleanup() }
    #expect(FileManager.default.fileExists(atPath: lease.appendingPathComponent("owner").path))
    try FileManager.default.removeItem(at: unknown)
    let reopened = try TransferReceiveFiles(parentURL: staging, manifest: manifest, destination: destination, existing: true)
    try await reopened.cleanup(); #expect(!FileManager.default.fileExists(atPath: lease.path))
    #expect(try Data(contentsOf: destination.appendingPathComponent("Folder/first")) == bytes)
    #expect(try Data(contentsOf: destination.appendingPathComponent("Folder/empty")) == Data("user-existing".utf8))
}
