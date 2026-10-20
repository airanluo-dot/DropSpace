import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private let parallelSize = 12 * 1024 * 1024 + 3
private func downloadData(changed: Bool = false) -> Data {
    Data((0..<parallelSize).map { changed ? UInt8(($0 % 251 + 1) % 256) : UInt8($0 % 251) })
}
private func downloadRoot() throws -> URL {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-download-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); return root
}
private actor TransferObservation {
    var peak = 0
    var received: Int64 = 0
    var metrics: [DownloadMetrics] = []
    func observe(_ value: DownloadMetrics) { metrics.append(value) }
    func record(_ bytes: Int64, connections: Int) { received = bytes; peak = max(peak, connections) }
}
private struct RequestObservation: Decodable {
    let path: String
    let range: String?
    let validator: String?
    let status: Int
    let begin: Int64
}
private func observations(_ address: String, path: String) async throws -> [RequestObservation] {
    let (bytes, _) = try await URLSession.shared.data(from: URL(string: address + "/stats")!)
    return try JSONDecoder().decode([RequestObservation].self, from: bytes).filter { $0.path == path }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"] != nil))
func nativeParallelHTTPUsesSharedCeilingAndAssemblesExactBytes() async throws {
    let address = ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"]!
    let root = try downloadRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let budget = DownloadBudget(); try await budget.connections.setLimit(3)
    let engine = HTTPDownloader(budget: budget), data = downloadData(), observed = TransferObservation()
    let path = "/parallel/" + UUID().uuidString, secondPath = "/parallel/" + UUID().uuidString
    let first = root.appendingPathComponent("first"), second = root.appendingPathComponent("second")
    async let a: Void = engine.download(source: URL(string: address + path)!, staging: first, expectedBytes: Int64(data.count), sha256: Digests.sha256(data), progress: { bytes, _ in
        await observed.record(bytes, connections: budget.connections.activeCount)
    })
    async let b: Void = engine.download(source: URL(string: address + secondPath)!, staging: second, expectedBytes: Int64(data.count), sha256: Digests.sha256(data))
    try await a; try await b
    #expect(try Data(contentsOf: first) == data)
    #expect(try Data(contentsOf: second) == data)
    let peak = await observed.peak
    #expect(peak > 1 && peak <= 3)
    #expect(await budget.connections.activeCount == 0)
    #expect(await budget.transfers.activeCount == 0)
    let requests = try await observations(address, path: path)
    #expect(requests.filter { $0.range != "bytes=0-0" && $0.validator == "\"v1\"" }.count == 3)
    try DownloadStaging.clean(first); try DownloadStaging.clean(second)
    #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"] != nil))
func nativeParallelHTTPCancelResumesPersistedPartitionWithLowerCeiling() async throws {
    let address = ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"]!
    let root = try downloadRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let budget = DownloadBudget(); try await budget.connections.setLimit(4); try await budget.bandwidth.setLimit(512 * 1024)
    let engine = HTTPDownloader(budget: budget), data = downloadData(), observed = TransferObservation()
    let path = "/slow/" + UUID().uuidString, destination = root.appendingPathComponent("body")
    let operation = Task { try await engine.download(source: URL(string: address + path)!, staging: destination, expectedBytes: Int64(data.count), sha256: Digests.sha256(data), metrics: { await observed.observe($0) }, progress: { bytes, _ in
        await observed.record(bytes, connections: budget.connections.activeCount)
    }) }
    let deadline = ContinuousClock.now.advanced(by: .seconds(8))
    while await observed.received < 262144, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
    operation.cancel()
    await #expect(throws: (any Error).self) { try await operation.value }
    #expect(await budget.connections.activeCount == 0)
    #expect(await budget.transfers.activeCount == 0)
    let files = try DownloadFiles(staging: destination)
    let retained = try files.rangeBytes()
    #expect(retained >= 262144 && retained < parallelSize)
    #expect(await observed.received == retained)
    let paused = try #require(await observed.metrics.last)
    #expect(paused.received == retained && paused.activeConnections == 0 && paused.bytesPerSecond == 0)
    #expect(await observed.metrics.contains { $0.bytesPerSecond > 0 && $0.activeConnections > 0 })
    let identityBefore = try #require(try files.read(files.identity))
    let sizes = try (0..<4).map { try files.size(files.part($0)) ?? 0 }
    try await budget.connections.setLimit(1); try await budget.bandwidth.setLimit(0)
    let resumed = TransferObservation()
    try await engine.download(source: URL(string: address + path)!, staging: destination, expectedBytes: Int64(data.count), sha256: Digests.sha256(data), progress: { bytes, _ in
        await resumed.record(bytes, connections: budget.connections.activeCount)
    })
    #expect(try files.read(files.identity) == identityBefore)
    #expect(try Data(contentsOf: destination) == data)
    #expect(await resumed.peak == 1)
    let requests = try await observations(address, path: path), plan = try HTTPByteRange.plan(length: Int64(data.count), connections: 4)
    for index in sizes.indices where sizes[index] > 0 && sizes[index] < plan[index].length {
        #expect(requests.contains { $0.begin == plan[index].from + sizes[index] && $0.validator == "\"v1\"" })
    }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"] != nil))
func nativeParallelHTTPRejectsChangedRepresentationAndRetriesInterruptedParts() async throws {
    let address = ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"]!
    let root = try downloadRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let budget = DownloadBudget(); try await budget.connections.setLimit(4)
    let engine = HTTPDownloader(budget: budget)
    let changedPath = "/changed/" + UUID().uuidString, changedFile = root.appendingPathComponent("changed"), changed = downloadData(changed: true)
    try await engine.download(source: URL(string: address + changedPath)!, staging: changedFile, expectedBytes: Int64(changed.count), sha256: Digests.sha256(changed))
    #expect(try Data(contentsOf: changedFile) == changed)
    let changes = try await observations(address, path: changedPath)
    #expect(changes.contains { $0.range != nil && $0.range != "bytes=0-0" && $0.status == 200 })
    #expect(changes.contains { $0.range == nil && $0.status == 200 })
    let retryPath = "/interrupted/" + UUID().uuidString, retryFile = root.appendingPathComponent("retry"), data = downloadData()
    try await engine.download(source: URL(string: address + retryPath)!, staging: retryFile, expectedBytes: Int64(data.count), sha256: Digests.sha256(data))
    #expect(try Data(contentsOf: retryFile) == data)
    let retryRequests = try await observations(address, path: retryPath)
    #expect(retryRequests.filter { $0.validator != nil }.count > 4)
    #expect(retryRequests.contains { $0.begin == 1024 * 1024 && $0.validator == "\"v1\"" })
    #expect(await budget.connections.activeCount == 0)
    #expect(await budget.transfers.activeCount == 0)
}

@Test func nativeDownloadCleanupPreservesUnknownFilesAndLinkedSources() throws {
    let root = try downloadRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let files = try DownloadFiles(staging: root.appendingPathComponent("body"))
    try files.prepareRanges()
    let source = root.appendingPathComponent("source.txt"), unknown = files.ranges.appendingPathComponent("user-notes.txt")
    try Data("source".utf8).write(to: source); try Data("keep".utf8).write(to: unknown)
    try FileManager.default.linkItem(at: source, to: files.part(0))
    #expect(throws: (any Error).self) { try DownloadStaging.clean(files.staging) }
    #expect(try String(contentsOf: source, encoding: .utf8) == "source")
    try FileManager.default.removeItem(at: files.part(0))
    try Data("part".utf8).write(to: files.part(0))
    try DownloadStaging.clean(files.staging)
    #expect(try String(contentsOf: unknown, encoding: .utf8) == "keep")
    #expect(!FileManager.default.fileExists(atPath: files.part(0).path))
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"] != nil))
func nativeParallelHTTPGrowsWorkersAndManagerPreservesPublishedFileDuringCleanup() async throws {
    let address = ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"]!
    let root = try downloadRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let budget = DownloadBudget(); try await budget.connections.setLimit(8)
    let engine = HTTPDownloader(budget: budget), observed = TransferObservation(), body = root.appendingPathComponent("growth")
    try await engine.download(source: URL(string: address + "/parallel/" + UUID().uuidString)!, staging: body, expectedBytes: Int64(parallelSize), sha256: Digests.sha256(downloadData()), metrics: { await observed.observe($0) }, progress: { bytes, _ in
        await observed.record(bytes, connections: budget.connections.activeCount)
    })
    #expect(await observed.peak > 4)
    let metrics = await observed.metrics
    #expect(metrics.map(\.activeConnections).max() ?? 0 > 4)
    #expect(metrics.contains { $0.stage == .merging && $0.activeConnections == 0 })
    #expect(metrics.contains { $0.stage == .verifying && $0.activeConnections == 0 })
    #expect(metrics.last?.stage == .completed && metrics.last?.bytesPerSecond == 0)
    #expect(metrics.last?.received == Int64(parallelSize) && metrics.last?.activeConnections == 0)
    #expect(try Data(contentsOf: body) == downloadData())
    try DownloadStaging.clean(body)
    let managerRoot = root.appendingPathComponent("history"), output = root.appendingPathComponent("output")
    let manager = try DownloadManager(directory: managerRoot, budget: budget)
    let id = try await manager.add(source: URL(string: address + "/slow/" + UUID().uuidString)!, directory: output, name: "download.bin")
    let deadline = ContinuousClock.now.advanced(by: .seconds(10))
    while await manager.history().first?.phase != .completed, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(20)) }
    let record = try #require(await manager.history().first)
    #expect(record.phase == .completed && record.cleanupPending == false)
    #expect(try Data(contentsOf: record.destination) == downloadData())
    #expect(try FileManager.default.contentsOfDirectory(atPath: managerRoot.path) == ["history.json"])
    try await manager.removeHistory(id)
    #expect(try Data(contentsOf: record.destination) == downloadData())
    await manager.shutdown()
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"] != nil))
func nativeDownloadMetricsIsolateQueuedTransferAndSettleCancellation() async throws {
    let address = ProcessInfo.processInfo.environment["DROPSPACE_TEST_PARALLEL_HTTP"]!
    let root = try downloadRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let budget = DownloadBudget(); try await budget.transfers.setLimit(1)
    try await budget.connections.setLimit(4); try await budget.bandwidth.setLimit(512 * 1024)
    let engine = HTTPDownloader(budget: budget), first = TransferObservation(), second = TransferObservation()
    let a = Task { try await engine.download(source: URL(string: address + "/slow/" + UUID().uuidString)!, staging: root.appendingPathComponent("first"), metrics: { await first.observe($0) }, progress: { bytes, _ in await first.record(bytes, connections: 0) }) }
    let deadline = ContinuousClock.now.advanced(by: .seconds(8))
    while await first.received < 262144, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
    #expect(await first.received >= 262144)
    let b = Task { try await engine.download(source: URL(string: address + "/slow/" + UUID().uuidString)!, staging: root.appendingPathComponent("second"), metrics: { await second.observe($0) }) }
    while await second.metrics.isEmpty, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
    let queued = try #require(await second.metrics.last)
    #expect(queued.stage == .queued && queued.activeConnections == 0 && queued.bytesPerSecond == 0 && queued.received == 0)
    #expect(await budget.connections.activeCount > 0)
    b.cancel(); await #expect(throws: (any Error).self) { try await b.value }
    a.cancel(); await #expect(throws: (any Error).self) { try await a.value }
    #expect(await first.metrics.last?.activeConnections == 0)
    #expect(await second.metrics.allSatisfy { $0.activeConnections == 0 && $0.received == 0 })
    #expect(await budget.connections.activeCount == 0)
    #expect(await budget.transfers.activeCount == 0)
}
