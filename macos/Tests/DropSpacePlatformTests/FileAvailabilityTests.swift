import Foundation
import Testing
import DropSpaceCore
@testable import DropSpacePlatform

@Test func fileAvailabilityFollowsBookmarkAndDetectsMissingTypeVolumeAndPermission() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let original = root.appendingPathComponent("original.txt"), renamed = root.appendingPathComponent("renamed.txt")
    try Data("keep these bytes".utf8).write(to: original)
    let reference = try #require(FileIntake.prepare([original], source: .space, settings: AppSettings()).items.first?.file)
    let service = FileReferenceService()
    #expect(try await service.check(reference, kind: .file).status == .available)
    try FileManager.default.moveItem(at: original, to: renamed)
    let moved = try await service.check(reference, kind: .file)
    #expect(moved.status == .available && moved.resolvedURL?.lastPathComponent == "renamed.txt")
    var wrongVolume = reference; wrongVolume.volumeID = "not-this-volume"
    #expect(try await service.check(wrongVolume, kind: .file).reason == .volumeUnavailable)
    #expect(try await service.check(reference, kind: .folder).reason == .typeChanged)
    try FileManager.default.removeItem(at: renamed)
    #expect(try await service.check(reference, kind: .file).status == .missing)
    try FileManager.default.createDirectory(at: original, withIntermediateDirectories: true)
    #expect(try await service.check(reference, kind: .file).reason == .typeChanged)
    let locked = root.appendingPathComponent("locked")
    try FileManager.default.createDirectory(at: locked, withIntermediateDirectories: true)
    let child = locked.appendingPathComponent("child"); try Data("preserve".utf8).write(to: child)
    try FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: locked.path)
    let denied = try await service.check(.init(path: child.path), kind: .file)
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: locked.path)
    #expect(denied.status == .unavailable && denied.reason == .accessDenied)
    #expect(try String(contentsOf: child, encoding: .utf8) == "preserve")
    #expect(FileReferenceService.isRemote(root.path) == false)
    #expect(FileReferenceService.isRemote("/Volumes/DropSpace-absent-validation-volume/file") == true)
    #expect(await service.shutdown())
}

private final class BlockingMetadata: @unchecked Sendable {
    private let condition = NSCondition()
    private var released = false
    private var started = 0
    func inspect(_ reference: FileReference, _ kind: ItemKind) -> FileAvailability {
        condition.lock(); started += 1
        while !released { condition.wait() }
        condition.unlock()
        return .init(status: .available, resolvedURL: URL(fileURLWithPath: reference.path))
    }
    func release() { condition.lock(); released = true; condition.broadcast(); condition.unlock() }
    var count: Int { condition.lock(); defer { condition.unlock() }; return started }
}

@Test func remoteMetadataTimeoutCancellationAndShutdownRetainActualWorkerPermits() async throws {
    let blocked = BlockingMetadata(); defer { blocked.release() }
    let service = FileReferenceService(timing: .init(acquisition: .milliseconds(40), operation: .milliseconds(100), cache: 2), remotePath: { _ in true }, inspect: blocked.inspect)
    let first = Task { try await service.check(.init(path: "/remote/one"), kind: .file) }
    let second = Task { try await service.check(.init(path: "/remote/two"), kind: .file) }
    let deadline = ContinuousClock.now.advanced(by: .seconds(1))
    while blocked.count < 2, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(2)) }
    first.cancel()
    await #expect(throws: CancellationError.self) { try await first.value }
    await #expect(throws: (any Error).self) { try await second.value }
    #expect(blocked.count == 2)
    #expect(await service.diagnostics.remote == 2)
    await #expect(throws: (any Error).self) { try await service.check(.init(path: "/remote/three"), kind: .file) }
    #expect(blocked.count == 2)
    let waiting = Task { try await service.check(.init(path: "/remote/cancel"), kind: .file) }
    waiting.cancel()
    await #expect(throws: CancellationError.self) { try await waiting.value }
    #expect(!(await service.shutdown(timeout: .milliseconds(40))))
    #expect(await service.diagnostics.remote == 2)
    blocked.release()
    #expect(await service.shutdown())
    #expect(await service.diagnostics.remote == 0)
}

@Test func fileIntakePreservesMissingReferencesAndAppliesKnownFileBudgetsOnly() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let folder = root.appendingPathComponent("folder"), small = root.appendingPathComponent("small"), large = root.appendingPathComponent("large"), later = root.appendingPathComponent("later")
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    try Data(repeating: 1, count: 6).write(to: small)
    try Data(repeating: 2, count: 20).write(to: large)
    try Data(repeating: 3, count: 4).write(to: later)
    let missing = root.appendingPathComponent("missing")
    let service = FileReferenceService()
    var settings = AppSettings(); settings.maxClipboardFileBytes = 6; settings.maxClipboardFileTotalBytes = 10
    let batch = try await service.prepare([folder, small, large, missing, later], source: .clipboard, settings: settings)
    #expect(batch.items.map(\.title) == ["folder", "small", "missing", "later"])
    #expect(batch.failures.count == 1)
    #expect(batch.items[0].file?.size == nil && batch.items[2].file?.size == nil && batch.items[2].status == .missing)
    #expect(batch.items.allSatisfy { $0.batchCount == 4 && $0.batchAcquisition == "clipboard-files" && $0.fingerprint != nil && $0.payloadPath == nil })
    #expect(batch.items.map(\.batchIndex) == [0, 1, 2, 3])
    settings.captureFolders = false
    #expect(try await service.prepare([folder, missing], source: .clipboard, settings: settings).items.map(\.title) == ["missing"])
    settings.captureFiles = false; settings.maxClipboardFileItems = 1
    #expect(try await service.prepare([folder, small], source: .clipboard, settings: settings).items.isEmpty)
    // Explicit repair/intake is independent of clipboard limits and toggles.
    #expect(try await service.prepare([folder, large], source: .space, settings: settings).items.count == 2)
    #expect(await service.shutdown())
    #expect(try Data(contentsOf: large) == Data(repeating: 2, count: 20))
}

@Test func fileAvailabilityRemoteCacheSeparatesIdentityAndHasFixedCapacity() async throws {
    let service = FileReferenceService(timing: .init(), remotePath: { _ in true }, inspect: { reference, kind in
        .init(status: kind == .folder ? .unavailable : .available, reason: kind == .folder ? .typeChanged : nil, resolvedURL: URL(fileURLWithPath: reference.path))
    })
    let file = FileReference(path: "/remote/Name", bookmark: Data([1]), volumeID: "one")
    let first = try await service.check(file, kind: .file)
    let cached = try await service.check(file, kind: .file)
    #expect(first.checkedAt == cached.checkedAt)
    #expect(try await service.check(file, kind: .folder).reason == .typeChanged)
    var different = file; different.path = "/remote/name"
    _ = try await service.check(different, kind: .file)
    different = file; different.bookmark = Data([2])
    _ = try await service.check(different, kind: .file)
    different = file; different.volumeID = "two"
    _ = try await service.check(different, kind: .file)
    #expect(await service.diagnostics.cached == 5)
    for index in 0..<1025 { _ = try await service.check(.init(path: "/remote/\(index)"), kind: .file) }
    #expect(await service.diagnostics.cached == 1024)
    #expect(await service.shutdown())
}
