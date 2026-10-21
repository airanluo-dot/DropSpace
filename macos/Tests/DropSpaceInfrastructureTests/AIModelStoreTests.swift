import Foundation
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

@Test func fixedModelCatalogAndOwnedRemovalPreserveUnrelatedData() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceModel-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try AIModelStore(directory: root, budget: DownloadBudget())
    let model = try store.catalog.model("hy-mt2-18-q8-plain-beta")
    #expect(model.fullsize == 1_908_528_192 && model.parts.count == 1)
    let larger = try store.catalog.model("hy-mt2-7b-q8-plain-beta")
    #expect(larger.parts.map(\.bytes) == [2_000_000_000, 2_000_000_000, 2_000_000_000, 1_981_928_896])
    #expect(!AIModelCatalog.trusted(URL(string: "https://github.com/other/project/releases/download/model")!))
    #expect(!AIModelCatalog.trusted(URL(string: "https://release-assets.githubusercontent.com.evil.invalid/model")!))
    await #expect(throws: (any Error).self) { try await store.install(model.id, consent: false) }
    let part = root.appendingPathComponent(model.fullsha + "." + AIModelCatalog.manifestIdentity.prefix(16) + ".part001")
    let unrelated = root.appendingPathComponent("my-lyrics.txt")
    try Data([1, 2, 3]).write(to: part); try Data("keep".utf8).write(to: unrelated)
    let ranges = part.appendingPathExtension("ranges")
    try FileManager.default.createDirectory(at: ranges, withIntermediateDirectories: false)
    try Data([4, 5, 6, 7]).write(to: ranges.appendingPathComponent("part-00.bin"))
    try Data("unrelated range note".utf8).write(to: ranges.appendingPathComponent("my-notes.txt"))
    #expect(try await store.inspect(model.id).artifactBytes == 7)
    try await store.setEnabled(model.id, false)
    let restarted = try AIModelStore(directory: root, budget: DownloadBudget())
    #expect(try await !restarted.inspect(model.id).enabled)
    // Unexpected directory cannot be recursively removed as an owned model file.
    let blocked = root.appendingPathComponent(model.fullsha + ".gguf")
    try FileManager.default.createDirectory(at: blocked, withIntermediateDirectories: false)
    await #expect(throws: (any Error).self) { try await restarted.remove(model.id) }
    let saved = try JSONSerialization.jsonObject(with: Data(contentsOf: root.appendingPathComponent("models-state.json"))) as? [String: Any]
    #expect((saved?["pendingRemoval"] as? [String])?.contains(model.id) == true)
    try FileManager.default.removeItem(at: blocked) // Own empty fault fixture only.
    await restarted.retryPendingCleanup()
    #expect(try await restarted.inspect(model.id).artifactBytes == 0)
    #expect(try await !restarted.inspect(model.id).pendingCleanup)
    #expect(try String(contentsOf: unrelated, encoding: .utf8) == "keep")
    #expect(!FileManager.default.fileExists(atPath: ranges.appendingPathComponent("part-00.bin").path))
    #expect(try String(contentsOf: ranges.appendingPathComponent("my-notes.txt"), encoding: .utf8) == "unrelated range note")
}

private actor ModelDownloadProgress {
    var previous: AIModelProgress.Phase?
    var lastBytes: Int64 = 0
    func record(_ value: AIModelProgress) {
        guard previous != value.phase || value.received - lastBytes >= 64 * 1024 * 1024 else { return }
        previous = value.phase; lastBytes = value.received
        try? FileHandle.standardOutput.write(contentsOf: Data("model \(value.phase.rawValue): \(value.received)/\(value.total) bytes\n".utf8))
    }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_MODEL_DOWNLOAD"] == "1"))
func actualHyMT18ModelInstallAndVerifiedLease() async throws {
    let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("build/ModelValidation")
    let store = try AIModelStore(directory: root, budget: DownloadBudget()), progress = ModelDownloadProgress()
    try await store.install("hy-mt2-18-q8-plain-beta", consent: true) { await progress.record($0) }
    let lease = try await store.lease("hy-mt2-18-q8-plain-beta")
    #expect(try await store.inspect(lease.model.id).installed)
    await #expect(throws: (any Error).self) { try await store.remove(lease.model.id) }
    await store.release(lease)
    // Preserve the fixed model for the subsequent real CPU/Metal inference checks.
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_MODEL_7B_DOWNLOAD"] == "1"))
func actualHyMT7BMultipartInstallAndVerifiedLease() async throws {
    let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("build/ModelValidation")
    let store = try AIModelStore(directory: root, budget: DownloadBudget()), progress = ModelDownloadProgress()
    try await store.install("hy-mt2-7b-q8-plain-beta", consent: true) { await progress.record($0) }
    let lease = try await store.lease("hy-mt2-7b-q8-plain-beta")
    #expect(lease.model.fullsize == 7_981_928_896 && lease.model.parts.count == 4)
    #expect(try await store.inspect(lease.model.id).installed)
    #expect(try await !store.inspect(lease.model.id).pendingCleanup)
    await store.release(lease)
    // Download, part/full hashes, assembly, licenses and cleanup are validated.
    // This does not claim7B inference passed the separate memory admission.
}
