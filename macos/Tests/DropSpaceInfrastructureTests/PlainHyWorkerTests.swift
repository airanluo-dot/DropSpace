import Foundation
import Darwin
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

private let nativeWorkerValidation = ProcessInfo.processInfo.environment["DROPSPACE_TRANSLATION_HOST"] == "1"
private let macRoot = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TRANSLATION_7B"] == "1"))
func actual7BTranslationAdmissionProbe() async throws {
    let store = try AIModelStore(directory: macRoot.appendingPathComponent("build/ModelValidation"), budget: DownloadBudget())
    let worker = PlainHyTranslationWorker(executable: macRoot.appendingPathComponent("build/workers/bin/plain-lyrics-worker"), models: store)
    let model = "hy-mt2-7b-q8-plain-beta"
    var records: [[String: Any]] = []
    for metal in [false, true] {
        do {
            let value = try await worker.translate(prompt: PlainHyLyrics.prompt("月光照亮了安静的河流。", target: "en"), modelID: model, preferMetal: metal)
            #expect(!value.text.isEmpty)
            records.append(["requestedBackend": metal ? "metal" : "cpu", "status": "inference-returned", "actualBackend": value.backend, "output": value.text, "fallback": value.usedCPUFallback])
            await worker.stop()
            #expect(kill(value.processID, 0) == -1 && errno == ESRCH)
        } catch let error as PlainHyWorkerFailure where error == .unavailableMemory || error == .memoryPressure {
            records.append(["requestedBackend": metal ? "metal" : "cpu", "status": "resource-admission-denied", "reason": error.rawValue])
            await worker.stop()
        } catch { await worker.stop(); throw error }
        try await store.setEnabled(model, true) // No retained worker lease after either outcome.
    }
    let receipt: [String: Any] = ["model": model, "physicalMemory": ProcessInfo.processInfo.physicalMemory,
        "startupAvailableRequirement": UInt64(13) * 1024 * 1024 * 1024, "system": ProcessInfo.processInfo.operatingSystemVersionString,
        "checks": records, "note": "A resource-admission-denied result is not successful inference validation."]
    try JSONSerialization.data(withJSONObject: receipt, options: [.prettyPrinted, .sortedKeys]).write(to: macRoot.appendingPathComponent("build/translation-7b-admission-receipt.json"))
    print(String(data: try JSONSerialization.data(withJSONObject: receipt, options: [.sortedKeys]), encoding: .utf8)!)
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TRANSLATION_IDLE"] == "1"))
func actualTranslationHostRetiresAfterProductionIdleInterval() async throws {
    let store = try AIModelStore(directory: macRoot.appendingPathComponent("build/ModelValidation"), budget: DownloadBudget())
    let worker = PlainHyTranslationWorker(executable: macRoot.appendingPathComponent("build/workers/bin/plain-lyrics-worker"), models: store)
    let model = "hy-mt2-18-q8-plain-beta"
    do {
        let result = try await worker.translate(prompt: PlainHyLyrics.prompt("月光照亮了安静的河流。", target: "en"), modelID: model, preferMetal: false)
        let start = ContinuousClock.now, deadline = start.advanced(by: .seconds(70))
        while kill(result.processID, 0) == 0, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(250)) }
        #expect(kill(result.processID, 0) == -1 && errno == ESRCH)
        #expect(start.duration(to: .now) >= .seconds(59))
        try await store.setEnabled(model, true)
        await worker.stop()
    } catch { await worker.stop(); throw error }
}

@Test(.enabled(if: nativeWorkerValidation))
func actualTranslationHostReusesModelAndDrainsBeforeBackendSwitch() async throws {
    let store = try AIModelStore(directory: macRoot.appendingPathComponent("build/ModelValidation"), budget: DownloadBudget())
    let worker = PlainHyTranslationWorker(executable: macRoot.appendingPathComponent("build/workers/bin/plain-lyrics-worker"), models: store)
    let model = "hy-mt2-18-q8-plain-beta"
    do {
        let identity = try await worker.inferenceIdentity(modelID: model)
        #expect(identity.count == 64)
        let first = try await worker.translate(prompt: PlainHyLyrics.prompt("月光照亮了安静的河流。", target: "en"), modelID: model, preferMetal: false)
        #expect(first.backend == "cpu" && !first.text.isEmpty && !first.usedCPUFallback)
        let second = try await worker.translate(prompt: PlainHyLyrics.prompt("晨风轻轻掠过窗前。", target: "en"), modelID: model, preferMetal: false)
        #expect(second.processID == first.processID && !second.text.isEmpty)
        await #expect(throws: (any Error).self) { try await store.setEnabled(model, false) }
        let accelerated = try await worker.translate(prompt: PlainHyLyrics.prompt("月光照亮了安静的河流。", target: "en"), modelID: model, preferMetal: true)
        #expect(accelerated.backend == "metal" && !accelerated.usedCPUFallback && accelerated.processID != first.processID)
        #expect(kill(first.processID, 0) == -1 && errno == ESRCH)
        await worker.stop()
        #expect(kill(accelerated.processID, 0) == -1 && errno == ESRCH)
        // Successful enable mutation proves the process lease has actually ended.
        try await store.setEnabled(model, true)
    } catch { await worker.stop(); throw error }
}

@Test(.enabled(if: nativeWorkerValidation))
func actualTranslationHostCancellationConfirmsUnresponsiveChildExit() async throws {
    let store = try AIModelStore(directory: macRoot.appendingPathComponent("build/ModelValidation"), budget: DownloadBudget())
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceTranslationCancel-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let script = root.appendingPathComponent("worker"), marker = root.appendingPathComponent("ready.pid")
    let code = """
    #!/usr/bin/python3
    import json,os,signal,time
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    with open(\(String(reflecting: marker.path)), 'w') as f: f.write(str(os.getpid()))
    print(json.dumps({'protocol':1,'selectionProtocol':2,'ready':True,'backend':'cpu','modelProfile':'hy-mt2-1.8b-q8','engineCommit':'7fe450e19305b828c199d602c23a8337aaa1f03b'}),flush=True)
    time.sleep(120)
    """
    try code.write(to: script, atomically: true, encoding: .utf8)
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
    let worker = PlainHyTranslationWorker(executable: script, models: store)
    let request = Task {
        do { return try await worker.translate(prompt: PlainHyLyrics.prompt("月光照亮了安静的河流。", target: "en"), modelID: "hy-mt2-18-q8-plain-beta", preferMetal: false) }
        catch { print("Cancellation fixture request finished: \(error)"); throw error }
    }
    do {
        let deadline = ContinuousClock.now.advanced(by: .seconds(20))
        while !FileManager.default.fileExists(atPath: marker.path), ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(20)) }
        let pid = try #require(Int32(String(contentsOf: marker, encoding: .utf8)))
        let started = ContinuousClock.now; request.cancel()
        await #expect(throws: CancellationError.self) { _ = try await request.value }
        #expect(started.duration(to: .now) >= .seconds(2))
        #expect(kill(pid, 0) == -1 && errno == ESRCH)
        try await store.setEnabled("hy-mt2-18-q8-plain-beta", true)
        await worker.stop()
    } catch { request.cancel(); _ = try? await request.value; await worker.stop(); throw error }
}

@Test(.enabled(if: nativeWorkerValidation))
func translationHostFallbackRequiresValidatedFailureAndCleanup() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceTranslationFallback-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try AIModelStore(directory: macRoot.appendingPathComponent("build/ModelValidation"), budget: DownloadBudget())
    let script = root.appendingPathComponent("worker"), starts = root.appendingPathComponent("starts.txt")
    let code = """
    #!/usr/bin/python3
    import json,os,sys
    mode=sys.argv[sys.argv.index('--mode')+1]
    with open(\(String(reflecting: starts.path)), 'a') as f: f.write(mode+' '+str(os.getpid())+'\\n')
    if mode=='metal': sys.exit(65)
    print(json.dumps({'protocol':1,'selectionProtocol':2,'ready':True,'backend':mode,'modelProfile':'hy-mt2-1.8b-q8','engineCommit':'7fe450e19305b828c199d602c23a8337aaa1f03b'}),flush=True)
    for line in sys.stdin:
        request=json.loads(line)
        print(json.dumps({'protocol':1,'id':request['id'],'complete':True,'text':'Own validated fixture output'}),flush=True)
    """
    try code.write(to: script, atomically: true, encoding: .utf8)
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
    let worker = PlainHyTranslationWorker(executable: script, models: store), model = "hy-mt2-18-q8-plain-beta"
    do {
        let prompt = try PlainHyLyrics.prompt("月光照亮了安静的河流。", target: "en")
        let first = try await worker.translate(prompt: prompt, modelID: model, preferMetal: true)
        let second = try await worker.translate(prompt: prompt, modelID: model, preferMetal: true)
        #expect(first.backend == "cpu" && first.usedCPUFallback && second.processID == first.processID)
        let launches = try String(contentsOf: starts, encoding: .utf8).split(separator: "\n")
        #expect(launches.count == 2 && launches[0].hasPrefix("metal ") && launches[1].hasPrefix("cpu "))
        let metalPID = try #require(Int32(launches[0].split(separator: " ")[1]))
        #expect(kill(metalPID, 0) == -1 && errno == ESRCH)
        // Cleanup must work even when invoked by an already cancelled caller.
        let cleanup = Task { await worker.stop() }; cleanup.cancel(); await cleanup.value
        #expect(kill(first.processID, 0) == -1 && errno == ESRCH)
        try await store.setEnabled(model, true)
        // A model-integrity failure must never be retried on CPU.
        try code.replacingOccurrences(of: "sys.exit(65)", with: "sys.exit(67)").write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        await #expect(throws: PlainHyWorkerFailure.modelLoad) { try await worker.translate(prompt: prompt, modelID: model, preferMetal: true) }
        #expect(try String(contentsOf: starts, encoding: .utf8).split(separator: "\n").count == 3)
        await worker.stop()
    } catch { await worker.stop(); throw error }
}
