import Foundation
import DropSpaceCore

public protocol PlainHyTranslating: Sendable {
    func inferenceIdentity(modelID: String) async throws -> String
    func translate(prompt: String, modelID: String, preferMetal: Bool) async throws -> PlainHyExecution
}
extension PlainHyTranslationWorker: PlainHyTranslating {}

public struct PlainHyTranslationResult: Sendable {
    public enum Outcome: String, Sendable { case translated, noUsefulTranslation, failed }
    public var document: LyricsDocument
    public var outcome: Outcome
    public var fromCache = false
    public var reason: String?
    public var cacheWarning: String?
}

/// Admission and occurrence identities belong to the host. A worker sees only a
/// frozen single-segment prompt; no provider, track metadata or host IDs are sent.
public actor PlainHyLyricsTranslator {
    private struct Negative { var generation: UInt64; var until: ContinuousClock.Instant }
    private let cache: LyricsCache
    private let worker: any PlainHyTranslating
    private var noUseful: [String: Negative] = [:]
    private var circuit = LyricsInferenceCircuit()
    public var inferencePaused: Bool { circuit.isPaused }
    public func resumeInference() { circuit.resume() }
    public init(cache: LyricsCache, worker: any PlainHyTranslating) { self.cache = cache; self.worker = worker }
    public func translate(query: LyricsQuery, document: LyricsDocument, target: String, settings: LyricsSettings,
                          position: @escaping @Sendable () async -> Double = { -.infinity },
                          isCurrent: @escaping @Sendable () async -> Bool = { true },
                          progress: @escaping @Sendable (LyricsDocument) async -> Void = { _ in }) async throws -> PlainHyTranslationResult {
        try Task.checkCancellation()
        let source = PlainHyLyrics.markStates(document, target: target), indices = PlainHyLyrics.eligible(source, target: target)
        guard settings.aiTranslationEnabled, !indices.isEmpty else { return .init(document: source, outcome: .noUsefulTranslation) }
        let generation = await cache.generation, execution = await cache.executionGeneration
        var result = source, pending = Set(indices.filter { source.lines[$0].translationState != .translated })
        var outputs = Dictionary(uniqueKeysWithValues: indices.filter { !pending.contains($0) }.map { ($0, source.lines[$0].secondary ?? "") })
        var warning: String?
        var circuitToken: UInt64?
        do {
            let identity = try await worker.inferenceIdentity(modelID: settings.aiModelID)
            let key = try PlainHyLyrics.cacheIdentity(query: query, source: source, target: target, inference: identity)
            guard await current(execution, isCurrent) else { throw CancellationError() }
            if let hit = noUseful[key], hit.generation == generation, hit.until > .now {
                return .init(document: source, outcome: .noUsefulTranslation, fromCache: true)
            }
            noUseful.removeValue(forKey: key)
            do {
                if let bytes = try await cache.read(category: .ai, identity: key), let mapped = try? PlainHyOutputDecoder.decode(bytes),
                   let cached = try? PlainHyLyrics.apply(mapped, to: source, indices: indices, target: target) {
                    guard await current(execution, isCurrent) else { throw CancellationError() }
                    if PlainHyLyrics.useful(cached, target: target) { return .init(document: cached, outcome: .translated, fromCache: true) }
                    await rememberNoUseful(key, generation: generation)
                    return .init(document: source, outcome: .noUsefulTranslation, fromCache: true)
                }
            } catch is CancellationError { throw CancellationError() }
            catch { warning = error.localizedDescription }
            guard let token = circuit.begin() else {
                for index in pending {
                    result.lines[index].translationState = .failed
                    result.lines[index].translationReason = "inference-circuit-paused"
                }
                return .init(document: result, outcome: .failed, reason: "inference-circuit-paused")
            }
            circuitToken = token
            var memo = PlainHySegmentMemo()
            let parts = LyricsLanguagePolicy.parts(source), deadline = ContinuousClock.now.advanced(by: .seconds(PlainHyLyrics.wholeSongSeconds))
            while !pending.isEmpty {
                try Task.checkCancellation()
                guard await current(execution, isCurrent) else { throw CancellationError() }
                let cursor = await position()
                guard let index = PlainHyLyrics.nextLine(source, pending: pending, position: cursor) else { throw DropSpaceError.invalid("No pending lyric occurrence") }
                var translated: [String] = []
                for segment in parts[index] {
                    let prompt = try PlainHyLyrics.prompt(segment, target: target)
                    let memoKey = PlainHySegmentMemo.Key(prompt: Data(prompt.utf8).base64EncodedString(), target: LyricsLanguagePolicy.target(target), identity: identity, policy: LyricsLanguagePolicy.version)
                    let text: String
                    if let previous = memo.values[memoKey] { text = previous }
                    else {
                        let worker = worker, model = settings.aiModelID, metal = settings.gpuAccelerationEnabled && settings.gpuBackend != "cpu"
                        text = try await withThrowingTaskGroup(of: String.self) { group in
                            group.addTask { try await worker.translate(prompt: prompt, modelID: model, preferMetal: metal).text }
                            group.addTask { try await Task.sleep(until: deadline, clock: .continuous); throw PlainHySongDeadline() }
                            defer { group.cancelAll() }
                            return try await group.next()!
                        }
                    }
                    try Task.checkCancellation()
                    guard await current(execution, isCurrent) else { throw CancellationError() }
                    guard PlainHyLyrics.completeLine(text) else { throw DropSpaceError.invalid("Invalid worker output") }
                    let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                    translated.append(trimmed); memo.remember(memoKey, source: segment, text: trimmed)
                }
                let text = translated.joined(separator: " ")
                guard PlainHyLyrics.completeLine(text) else { throw DropSpaceError.invalid("Invalid lyric output") }
                result = try PlainHyLyrics.apply([.init(id: index, text: text)], to: result, indices: [index], target: target)
                outputs[index] = text; pending.remove(index)
                guard await current(execution, isCurrent) else { throw CancellationError() }
                await progress(result)
            }
            try Task.checkCancellation()
            guard ContinuousClock.now < deadline else { throw PlainHySongDeadline() }
            guard await current(execution, isCurrent) else { throw CancellationError() }
            let mapped = indices.map { PlainHyOutput(id: $0, text: outputs[$0]!) }
            result = try PlainHyLyrics.apply(mapped, to: source, indices: indices, target: target)
            if !PlainHyLyrics.useful(result, target: target) {
                await rememberNoUseful(key, generation: generation)
                return .init(document: source, outcome: .noUsefulTranslation, cacheWarning: warning)
            }
            let bytes = try JSONEncoder().encode(mapped)
            do { try await cache.write(category: .ai, identity: key, bytes: bytes, generation: generation) }
            catch is CancellationError { throw CancellationError() }
            catch { warning = error.localizedDescription }
            guard await current(execution, isCurrent) else { throw CancellationError() }
            circuit.complete(token, success: true)
            return .init(document: result, outcome: .translated, cacheWarning: warning)
        } catch is CancellationError { throw CancellationError() }
        catch {
            try Task.checkCancellation()
            guard await current(execution, isCurrent) else { throw CancellationError() }
            if let circuitToken {
                switch error as? PlainHyWorkerFailure {
                case .unavailableMemory, .memoryPressure, .residentLimit, .modelNotReady: break
                default: circuit.complete(circuitToken, success: false)
                }
            }
            let reason = error is PlainHySongDeadline ? "translation-deadline" : error.localizedDescription
            for index in pending { result.lines[index].translationState = .failed; result.lines[index].translationReason = reason }
            return .init(document: result, outcome: .failed, reason: reason, cacheWarning: warning)
        }
    }
    private func current(_ execution: UInt64, _ isCurrent: @Sendable () async -> Bool) async -> Bool {
        guard !Task.isCancelled, await cache.executionGeneration == execution else { return false }
        return await isCurrent()
    }
    private func rememberNoUseful(_ key: String, generation: UInt64) async {
        guard await cache.generation == generation else { return }
        if noUseful.count >= 64, let oldest = noUseful.min(by: { $0.value.until < $1.value.until })?.key { noUseful.removeValue(forKey: oldest) }
        noUseful[key] = .init(generation: generation, until: .now.advanced(by: .seconds(600)))
    }
    public func clearMemo() { noUseful.removeAll() }
}

private struct PlainHySongDeadline: Error {}
private struct PlainHySegmentMemo {
    struct Key: Hashable {
        var prompt: String; var target: String; var identity: String; var policy: String
        var byteCount: Int { [prompt, target, identity, policy].reduce(0) { $0 + $1.utf16.count * 2 } }
    }
    var values: [Key: String] = [:]
    var bytes = 0
    mutating func remember(_ key: Key, source: String, text: String) {
        guard values[key] == nil, values.count < 128, PlainHyLyrics.completeLine(text), PlainHyLyrics.validTranslation(text),
              !source.trimmingCharacters(in: .whitespacesAndNewlines).utf8.elementsEqual(text.trimmingCharacters(in: .whitespacesAndNewlines).utf8) else { return }
        let size = key.byteCount + text.utf16.count * 2
        guard size <= 256 * 1024 - bytes else { return }; values[key] = text; bytes += size
    }
}
