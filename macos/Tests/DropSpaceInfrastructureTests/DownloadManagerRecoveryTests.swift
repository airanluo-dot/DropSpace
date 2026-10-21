import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private func recoveryRoot() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-download-recovery-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true); return url
}
private func seedDownload(_ root: URL) throws -> DownloadRecord {
    var record = DownloadRecord(source: URL(string: "https://example.invalid/file?token=private")!, destination: root.appendingPathComponent("output.bin"))
    record.phase = .paused
    try JSONEncoder().encode([record]).write(to: root.appendingPathComponent("history.json")); return record
}

@Test func nativeDownloadAdmissionAndControlWritesRollbackWithoutStartingTransfer() async throws {
    let root = try recoveryRoot(); defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.path); try? FileManager.default.removeItem(at: root) }
    let record = try seedDownload(root), budget = DownloadBudget(), manager = try DownloadManager(directory: root, budget: budget)
    let staging = root.appendingPathComponent(record.id.uuidString + ".part"); try Data("retained".utf8).write(to: staging)
    let before = try Data(contentsOf: root.appendingPathComponent("history.json"))
    try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: root.path)
    await #expect(throws: (any Error).self) { try await manager.add(source: URL(string: "https://example.invalid/new")!, directory: root) }
    await #expect(throws: (any Error).self) { try await manager.resume(record.id) }
    await #expect(throws: (any Error).self) { try await manager.cancel(record.id) }
    #expect(await manager.history().count == 1)
    #expect(await manager.history().first?.phase == .paused)
    #expect(await budget.transfers.activeCount == 0)
    #expect(try Data(contentsOf: root.appendingPathComponent("history.json")) == before)
    #expect(try String(contentsOf: staging, encoding: .utf8) == "retained")
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.path)
    try await manager.cancel(record.id)
    #expect(await manager.history().first?.phase == .cancelled)
    #expect(await manager.history().first?.cleanupPending == false)
    // Cancelled transfers cannot silently restart through the public resume API.
    try await manager.resume(record.id)
    #expect(await manager.history().first?.phase == .cancelled)
    await manager.shutdown()
}

@Test func nativeDownloadHistoryTombstoneSurvivesUnsafeCleanupAndRestart() async throws {
    let root = try recoveryRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let record = try seedDownload(root), budget = DownloadBudget(), manager = try DownloadManager(directory: root, budget: budget)
    let staging = root.appendingPathComponent(record.id.uuidString + ".part"), source = root.appendingPathComponent("keep-user.txt")
    try Data("keep exact user bytes".utf8).write(to: source)
    try Data("partial".utf8).write(to: staging)
    let linked = staging.appendingPathExtension("request.json")
    try FileManager.default.linkItem(at: source, to: linked)
    await #expect(throws: (any Error).self) { try await manager.cancel(record.id) }
    let cancelled = try #require(await manager.history().first)
    #expect(cancelled.phase == .cancelled && cancelled.cleanupPending == true && cancelled.error == nil && cancelled.cleanupError != nil)
    await #expect(throws: (any Error).self) { try await manager.removeHistory(record.id) }
    #expect(await manager.history().isEmpty)
    await manager.shutdown()
    let retained = try JSONDecoder().decode([DownloadRecord].self, from: Data(contentsOf: root.appendingPathComponent("history.json")))
    #expect(retained.count == 1 && retained[0].historyRemovalPending == true && retained[0].cleanupPending == true)
    #expect(retained[0].cleanupError?.contains("private") == false)
    let restarted = try DownloadManager(directory: root, budget: budget)
    #expect(await restarted.history().isEmpty)
    #expect(try String(contentsOf: source, encoding: .utf8) == "keep exact user bytes")
    try FileManager.default.removeItem(at: linked)
    try await restarted.retryCleanup(record.id)
    #expect(try JSONDecoder().decode([DownloadRecord].self, from: Data(contentsOf: root.appendingPathComponent("history.json"))).isEmpty)
    #expect(try String(contentsOf: source, encoding: .utf8) == "keep exact user bytes")
    await restarted.shutdown()
}

@Test func nativeDownloadHistoryRemovalIgnoresUnfinishedRecords() async throws {
    let root = try recoveryRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let record = try seedDownload(root), manager = try DownloadManager(directory: root, budget: DownloadBudget())
    let staging = root.appendingPathComponent(record.id.uuidString + ".part"); try Data("retained".utf8).write(to: staging)
    try await manager.removeHistory(record.id)
    #expect(await manager.history().first?.id == record.id)
    #expect(try String(contentsOf: staging, encoding: .utf8) == "retained")
    await manager.shutdown()
}

@Test func nativeDownloadCleanupAutomaticallyRetriesAfterBlockedMarkerRemoved() async throws {
    let root = try recoveryRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let record = try seedDownload(root), manager = try DownloadManager(directory: root, budget: DownloadBudget())
    let source = root.appendingPathComponent("keep-user.txt"), linked = root.appendingPathComponent(record.id.uuidString + ".part.request.json")
    try Data("keep".utf8).write(to: source); try FileManager.default.linkItem(at: source, to: linked)
    await #expect(throws: (any Error).self) { try await manager.cancel(record.id) }
    #expect(await manager.history().first?.cleanupPending == true)
    try FileManager.default.removeItem(at: linked)
    let clock = ContinuousClock.now, deadline = clock.advanced(by: .seconds(35))
    while await manager.history().first?.cleanupPending == true, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(100)) }
    #expect(await manager.history().first?.cleanupPending == false)
    #expect(clock.duration(to: .now) >= .seconds(29))
    #expect(try String(contentsOf: source, encoding: .utf8) == "keep")
    let disk = try JSONDecoder().decode([DownloadRecord].self, from: Data(contentsOf: root.appendingPathComponent("history.json")))
    #expect(disk.first?.phase == .cancelled && disk.first?.cleanupPending == false)
    await manager.shutdown()
}
