import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func promisedStorageLimitsAndReplacementAreCheckedBeforeAdoption() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let payloads = PayloadStore(directory: root), lease = OwnedImportLease()
    let stage = try await payloads.createPromiseStaging(lease)
    var urls: [URL] = []
    for index in 0..<5 {
        let url = stage.appendingPathComponent("sparse-\(index)")
        #expect(FileManager.default.createFile(atPath: url.path, contents: nil))
        let handle = try FileHandle(forWritingTo: url)
        try handle.truncate(atOffset: UInt64(PromisedFileRules.maximumFileBytes)); try handle.close()
        urls.append(url)
    }
    #expect(try await payloads.inspectPromises(Array(urls.prefix(4)), lease: lease).reduce(0) { $0 + $1.bytes } == PromisedFileRules.maximumBatchBytes)
    await #expect(throws: PromisedFileError.batchLimit) { try await payloads.inspectPromises(urls, lease: lease) }
    await #expect(throws: PromisedFileError.countLimit) { try await payloads.inspectPromises(Array(repeating: urls[0], count: 1001), lease: lease) }
    let original = try #require(try await payloads.inspectPromises([urls[0]], lease: lease).first)
    try FileManager.default.removeItem(at: urls[0]); try Data([1]).write(to: urls[0])
    await #expect(throws: PromisedFileError.invalidResult) { try await payloads.adoptPromise(original, lease: lease, name: "replacement.txt") }
    #expect(try Data(contentsOf: urls[0]) == Data([1]))
    #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("replacement.txt").path))
}

@Test func promisedDirectoryRetainsLinksWithoutFollowingThemAndCleanupCanRetry() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer {
        try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.appendingPathComponent("Payloads").path)
        try? FileManager.default.removeItem(at: root)
    }
    let store = try ItemStore(directory: root), payloads = PayloadStore(directory: root.appendingPathComponent("Payloads"))
    let lease = OwnedImportLease(); try await store.beginOwnedImport(lease)
    let stage = try await payloads.createPromiseStaging(lease)
    let outside = root.appendingPathComponent("source.txt"); try Data("original".utf8).write(to: outside)
    let folder = stage.appendingPathComponent("folder", isDirectory: true)
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
    try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("source-link"), withDestinationURL: outside)
    try Data().write(to: folder.appendingPathComponent("empty"))
    let entry = try #require(try await payloads.inspectPromises([folder], lease: lease).first)
    #expect(entry.kind == .folder && entry.bytes == 0)
    try await store.abandonOwnedImport(lease.id)
    let parent = root.appendingPathComponent("Payloads")
    try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: parent.path)
    try await payloads.reconcile(store)
    #expect(try await store.pendingCleanup() == [lease.stagingName])
    #expect(try String(contentsOf: outside, encoding: .utf8) == "original")
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: parent.path)
    try await payloads.reconcile(store)
    #expect(try await store.pendingCleanup().isEmpty)
    #expect(try String(contentsOf: outside, encoding: .utf8) == "original")
}
