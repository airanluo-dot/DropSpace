import Foundation
import Darwin
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private let moduleRoot = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
private func workerFixture(_ mode: String? = nil) throws -> (ModuleWorkerClient, URL) {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-module-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    let manifest = try ModuleCodec.manifest(Data(contentsOf: moduleRoot.appendingPathComponent("Modules/Sample/manifest.template.json")))
    let executable: URL
    if let mode {
        try Data(mode.utf8).write(to: root.appendingPathComponent("fixture-mode"))
        executable = moduleRoot.appendingPathComponent("build/module-guardian/module-fault-worker")
    } else { executable = moduleRoot.appendingPathComponent(".build/debug/DropSpaceModuleSample") }
    return (.init(guardian: moduleRoot.appendingPathComponent("build/module-guardian/module-guardian"), executable: executable, dataDirectory: root, manifest: manifest), root)
}

@Test func nativeModuleWorkerRunsHelloSettingsActionAndConfirmedStop() async throws {
    let (client, root) = try workerFixture(); defer { try? FileManager.default.removeItem(at: root) }
    do {
        try await client.start()
        let state = await client.processState()
        #expect(state.running && !state.exitConfirmed && state.workerPID != nil)
        let reply = try await client.request("action", payload: .object([.init("action", .string("show"))]))
        #expect(reply.result["messageKey"]?.string == "message")
        try ModuleContract.validateIsland(#require(reply.island), manifest: await client.manifest, now: Date())
        _ = try await client.request("settings", payload: .object([.init("show-island", .string("false"))]))
        let hidden = try await client.request("action", payload: .object([.init("action", .string("show"))]))
        #expect(hidden.island == nil)
        #expect(await client.stop())
        #expect(await client.processState().exitConfirmed)
        #expect(kill(try #require(state.workerPID), 0) == -1 && errno == ESRCH)
    } catch { _ = await client.stop(); throw error }
}

@Test func nativeModuleWorkerRejectsOversizedStaleAndFloodedResponses() async throws {
    for mode in ["oversized", "stale", "stderr"] {
        let (client, root) = try workerFixture(mode); defer { try? FileManager.default.removeItem(at: root) }
        do {
            try await client.start()
            await #expect(throws: (any Error).self) { try await client.request("action", payload: .object([])) }
            #expect(await client.stop())
            #expect(await client.processState().exitConfirmed)
            await #expect(throws: (any Error).self) { try await client.request("action", payload: .object([])) }
        } catch { _ = await client.stop(); throw error }
    }
}

@Test func nativeModuleQueuedCancellationPreservesActiveOwnerAndRetirementKillsDescendants() async throws {
    let (client, root) = try workerFixture("descendant"); defer { try? FileManager.default.removeItem(at: root) }
    try await client.start()
    let active = Task { try await client.request("action", payload: .object([])) }
    let pidURL = root.appendingPathComponent("descendant-pid"), deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while !FileManager.default.fileExists(atPath: pidURL.path), ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
    let descendant = try #require(Int32(String(contentsOf: pidURL, encoding: .utf8)))
    let queued = Task { try await client.request("action", payload: .object([])) }
    try await Task.sleep(for: .milliseconds(30)); queued.cancel()
    await #expect(throws: CancellationError.self) { try await queued.value }
    #expect(kill(descendant, 0) == 0)
    // state() shares the pipe executor; do not synchronously ask it while a
    // deliberately stalled request owns that queue.
    active.cancel(); await #expect(throws: (any Error).self) { try await active.value }
    #expect(await client.stop())
    #expect(kill(descendant, 0) == -1 && errno == ESRCH)
}

@Test func nativeModuleProductionDeadlineRetiresUnresponsiveWorker() async throws {
    let (client, root) = try workerFixture("stall"); defer { try? FileManager.default.removeItem(at: root) }
    try await client.start()
    let begin = ContinuousClock.now
    await #expect(throws: (any Error).self) { try await client.request("action", payload: .object([])) }
    let elapsed = begin.duration(to: .now)
    #expect(elapsed >= .seconds(8) && elapsed < .seconds(11))
    #expect(await client.stop())
}
