import Foundation
import Darwin
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

private actor RuntimeObservations {
    var values: [PlainHyRuntimeStatus] = []
    func append(_ value: PlainHyRuntimeStatus) { values.append(value) }
}

/// Uses the existing local model lease, but a deliberately controlled protocol
/// worker. This checks OS exit/status ownership, not GPU inference performance.
@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_RUNTIME_STATUS"] == "1"))
func runtimeStatusObservesRequestWriteExitFallbackAndExplicitReset() async throws {
    let macRoot = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceRuntimeStatus-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let executable = root.appendingPathComponent("worker")
    try """
    #!/usr/bin/python3
    import json,sys
    backend=sys.argv[sys.argv.index('--mode')+1]
    print(json.dumps({'protocol':1,'selectionProtocol':2,'ready':True,'backend':backend,'modelProfile':'hy-mt2-1.8b-q8','engineCommit':'7fe450e19305b828c199d602c23a8337aaa1f03b'}),flush=True)
    for line in sys.stdin:
        request=json.loads(line)
        if backend=='metal': sys.exit(71)
        print(json.dumps({'protocol':1,'id':request['id'],'complete':True,'text':'Moonlight illuminates the quiet river.'}),flush=True)
    """.write(to: executable, atomically: true, encoding: .utf8)
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)
    let store = try AIModelStore(directory: macRoot.appendingPathComponent("build/ModelValidation"), budget: DownloadBudget())
    let worker = PlainHyTranslationWorker(executable: executable, models: store)
    let observations = RuntimeObservations()
    await worker.observeExecution { await observations.append($0) }
    do {
        let value = try await worker.translate(prompt: PlainHyLyrics.prompt("月光照亮了安静的河流。", target: "en"),
            modelID: "hy-mt2-18-q8-plain-beta", preferMetal: true)
        #expect(value.backend == "cpu" && value.usedCPUFallback)
        let values = await observations.values
        let phases = values.compactMap(\.current)
        #expect(phases.map(\.backend) == ["metal", "metal", "cpu", "cpu"])
        #expect(phases.map(\.phase) == [.starting, .executing, .starting, .executing])
        let status = await worker.runtimeStatus
        #expect(status.current == nil && status.last?.backend == "cpu")
        #expect(status.failure?.attemptedBackend == "metal" && status.failure?.reason == .decodeFailure)
        #expect(status.failure?.exit?.status == 71 && status.failure?.exit?.wasSignal == false)
        #expect(status.failure?.exit?.terminatedByHost == false && status.failure?.cpuFallbackAttempted == true)
        await worker.stop()
        #expect(await worker.runtimeStatus.failure == status.failure)
        #expect(await worker.runtimeStatus.last == status.last)
        await worker.stop(clearFailures: true)
        #expect(await worker.runtimeStatus == PlainHyRuntimeStatus())
        let resident = try await worker.translate(prompt: PlainHyLyrics.prompt("月光照亮了安静的河流。", target: "en"),
            modelID: "hy-mt2-18-q8-plain-beta", preferMetal: false)
        #expect(kill(resident.processID, SIGKILL) == 0)
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        while await worker.runtimeStatus.failure == nil, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(25))
        }
        let idleFailure = await worker.runtimeStatus.failure
        #expect(idleFailure?.exit?.status == SIGKILL && idleFailure?.exit?.wasSignal == true)
        #expect(idleFailure?.exit?.terminatedByHost == false && idleFailure?.cpuFallbackAttempted == false)
        #expect(await worker.runtimeStatus.last?.processID == resident.processID)
        await worker.stop()
        try await store.setEnabled("hy-mt2-18-q8-plain-beta", true)
    } catch { await worker.stop(); throw error }
}
