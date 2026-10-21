import Foundation
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

private actor TranslatorFixture: PlainHyTranslating {
    var prompts: [String] = []
    var failAt: Int?
    var afterCall: (@Sendable () async throws -> Void)?
    init(failAt: Int? = nil, afterCall: (@Sendable () async throws -> Void)? = nil) { self.failAt = failAt; self.afterCall = afterCall }
    func inferenceIdentity(modelID: String) async throws -> String { String(repeating: "b", count: 64) }
    func translate(prompt: String, modelID: String, preferMetal: Bool) async throws -> PlainHyExecution {
        prompts.append(prompt)
        if prompts.count == failAt { throw PlainHyWorkerFailure.requestDeadline }
        try await afterCall?()
        return .init(text: prompt.contains("月光") ? "Moonlight illuminates the quiet river." : "The morning breeze passes the window.", backend: "fixture", usedCPUFallback: false, processID: 0)
    }
}
private actor TranslationProgressFixture {
    var position: Double = 1.5
    var snapshots: [LyricsDocument] = []
    func record(_ value: LyricsDocument) { snapshots.append(value); position = 9.5 }
}
private func translationSource() throws -> LyricsDocument {
    let source = try LyricsParser.parse("[00:01]月光照亮了安静的河流\n[00:05]晨风轻轻掠过窗前\n[00:09]月光照亮了安静的河流", provider: .localLrc)
    return LyricsLanguagePolicy.prepare(source, target: "en", original: .init(label: "zh", score: 0.95, runnerUpScore: 0.02))
}

private actor CircuitWorkerFixture: PlainHyTranslating {
    var failure: PlainHyWorkerFailure?
    var calls = 0
    func setFailure(_ value: PlainHyWorkerFailure?) { failure = value }
    func inferenceIdentity(modelID: String) -> String { String(repeating: "c", count: 64) }
    func translate(prompt: String, modelID: String, preferMetal: Bool) throws -> PlainHyExecution {
        calls += 1
        if let failure { throw failure }
        return .init(text: "Moonlight illuminates the quiet river.", backend: "fixture", usedCPUFallback: false, processID: 0)
    }
}

@Test func plainHyCircuitCountsFailuresPreservesCacheAndRecoversExplicitly() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceCircuit-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let worker = CircuitWorkerFixture(), cache = LyricsCache(directory: root)
    let coordinator = PlainHyLyricsTranslator(cache: cache, worker: worker)
    let source = try translationSource()
    var settings = LyricsSettings(); settings.aiTranslationEnabled = true
    let cachedQuery = LyricsQuery(title: "Cached own recording", artist: "Fixture")
    let query = LyricsQuery(title: "Uncached own recording", artist: "Fixture")
    _ = try await coordinator.translate(query: cachedQuery, document: source, target: "en", settings: settings)
    let cachedBytes = try await cache.usage()
    #expect(cachedBytes > 0)
    // Recoverable resource/package admission is not a model failure.
    for reason in [PlainHyWorkerFailure.unavailableMemory, .memoryPressure, .residentLimit, .modelNotReady] {
        await worker.setFailure(reason)
        _ = try await coordinator.translate(query: query, document: source, target: "en", settings: settings)
        #expect(await !coordinator.inferencePaused)
    }
    await worker.setFailure(.decodeFailure)
    for attempt in 1...3 {
        let result = try await coordinator.translate(query: query, document: source, target: "en", settings: settings)
        #expect(result.outcome == .failed)
        #expect(result.document.lines.map(\.text) == source.lines.map(\.text))
        #expect(await coordinator.inferencePaused == (attempt == 3))
    }
    let calls = await worker.calls
    let blocked = try await coordinator.translate(query: query, document: source, target: "en", settings: settings)
    #expect(blocked.reason == "inference-circuit-paused")
    let hit = try await coordinator.translate(query: cachedQuery, document: source, target: "en", settings: settings)
    #expect(hit.fromCache && hit.outcome == .translated)
    #expect(await worker.calls == calls)
    #expect(await coordinator.inferencePaused)
    await coordinator.resumeInference()
    #expect(try await cache.usage() == cachedBytes)
    await worker.setFailure(nil)
    let recovered = try await coordinator.translate(query: query, document: source, target: "en", settings: settings)
    #expect(recovered.outcome == .translated && !recovered.fromCache)
    #expect(await !coordinator.inferencePaused)
}

@Test func lyricsCircuitSuccessAndRetiredGenerationFollowBeta7() {
    var circuit = LyricsInferenceCircuit()
    let old = circuit.begin()!
    circuit.complete(old, success: false); circuit.complete(old, success: false)
    circuit.complete(old, success: true)
    circuit.complete(old, success: false); circuit.complete(old, success: false)
    #expect(!circuit.isPaused)
    circuit.complete(old, success: false)
    #expect(circuit.isPaused && circuit.begin() == nil)
    circuit.complete(old, success: true)
    #expect(circuit.isPaused)
    circuit.resume()
    circuit.complete(old, success: false); circuit.complete(old, success: false); circuit.complete(old, success: false)
    #expect(!circuit.isPaused && circuit.begin() != old)
}

@Test func plainHyCoordinatorMemoSeekAndCompleteCacheUseCanonicalOccurrences() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceTranslation-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let cache = LyricsCache(directory: root), worker = TranslatorFixture(), progress = TranslationProgressFixture()
    let coordinator = PlainHyLyricsTranslator(cache: cache, worker: worker), source = try translationSource()
    var settings = LyricsSettings(); settings.aiTranslationEnabled = true
    let query = LyricsQuery(title: "Own recording", artist: "Fixture")
    let first = try await coordinator.translate(query: query, document: source, target: "en", settings: settings,
        position: { await progress.position }, progress: { await progress.record($0) })
    #expect(first.outcome == .translated && !first.fromCache)
    #expect(await worker.prompts.count == 2)
    let snapshots = await progress.snapshots
    #expect(snapshots.count == 3)
    #expect(snapshots[1].lines[2].translationOrigin == .localAI && snapshots[1].lines[1].translationOrigin != .localAI)
    #expect(first.document.lines[0].localAIAdmissionKey != first.document.lines[2].localAIAdmissionKey)
    let second = try await coordinator.translate(query: query, document: source, target: "en", settings: settings)
    #expect(second.fromCache && second.document == first.document)
    #expect(await worker.prompts.count == 2)
    try await cache.configure(maximumBytes: 0)
    let noDisk = try await coordinator.translate(query: query, document: source, target: "en", settings: settings)
    #expect(noDisk.outcome == .translated && !noDisk.fromCache)
    #expect(try await cache.usage() == 0)
    #expect(await worker.prompts.count == 4)
}

@Test func plainHyCoordinatorPartialFailureAndClearFenceNeverPublishCompleteCache() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceTranslationPartial-" + UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let cache = LyricsCache(directory: root), worker = TranslatorFixture(failAt: 2)
    let coordinator = PlainHyLyricsTranslator(cache: cache, worker: worker), source = try translationSource()
    var settings = LyricsSettings(); settings.aiTranslationEnabled = true
    let query = LyricsQuery(title: "Own recording", artist: "Fixture")
    let failed = try await coordinator.translate(query: query, document: source, target: "en", settings: settings)
    #expect(failed.outcome == .failed)
    #expect(failed.document.lines[0].translationOrigin == .localAI && failed.document.lines[1].translationState == .failed)
    #expect(try await cache.usage() == 0)
    let clearing = TranslatorFixture(afterCall: { try await cache.clear() })
    let fenced = PlainHyLyricsTranslator(cache: cache, worker: clearing)
    await #expect(throws: CancellationError.self) { try await fenced.translate(query: query, document: source, target: "en", settings: settings) }
    #expect(try await cache.usage() == 0)
}
