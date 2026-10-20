import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func ownedImportInterruptedAdoptionRecoversOnlyReservedPaths() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try ItemStore(directory: root), payloads = PayloadStore(directory: root.appendingPathComponent("Payloads"))
    let lease = OwnedImportLease()
    try await store.beginOwnedImport(lease)
    let stage = try await payloads.createPromiseStaging(lease)
    let file = stage.appendingPathComponent("input.txt"); try Data([1, 2]).write(to: file)
    let entry = try #require(try await payloads.inspectPromises([file], lease: lease).first)
    let name = UUID().uuidString + ".txt"
    try await store.reserveOwnedImport(lease.id, name: name)
    let output = try await payloads.adoptPromise(entry, lease: lease, name: name)
    let unrelated = root.appendingPathComponent("Payloads/unrelated.txt"); try Data([9]).write(to: unrelated)
    let reopened = try ItemStore(directory: root)
    try await reopened.recoverAfterLaunch()
    #expect(Set(try await reopened.pendingCleanup()) == [lease.stagingName, name])
    try await payloads.reconcile(reopened)
    #expect(!FileManager.default.fileExists(atPath: stage.path))
    #expect(!FileManager.default.fileExists(atPath: output.path))
    #expect(try Data(contentsOf: unrelated) == Data([9]))
    #expect(try await reopened.pendingCleanup().isEmpty)
    await #expect(throws: (any Error).self) { try await reopened.commitOwnedImportItem(lease.id, item: .init(source: .space, kind: .file, title: "invalid")) }
}
