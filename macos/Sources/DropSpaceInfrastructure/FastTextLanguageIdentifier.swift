import Foundation
import CryptoKit
import Darwin
import DropSpaceCore

/// One CPU model shared by all providers. The private queue owns the process,
/// pipes and role-separated LRU; no blocking process I/O runs on MainActor.
public final class FastTextLanguageIdentifier: LyricsLanguageIdentifying, Sendable {
    public static let modelSHA256 = "7e69ec5451bc261cc7844e49e4792a85d7f09c06789ec800fc4a44aec362764e"
    public static let engineCommit = "1142dc4c4ecbc19cc16eee5cdd28472e689267e6"
    public static let modelIdentity = modelSHA256 + ":" + engineCommit + ":macos-arm64-pipe-v1:" + LyricsLanguagePolicy.preprocessingVersion
    public struct Statistics: Sendable {
        public var modelLoads = 0
        public var predictions = 0
        public var cacheEntries = 0
        public var workerRunning = false
    }
    private let queue = DispatchQueue(label: "app.dropspace.lyrics.language", qos: .utility)
    private let engine: Engine
    public init(executable: URL, model: URL, timeout: TimeInterval = 15) {
        engine = Engine(executable: executable, model: model, timeout: max(0.1, min(timeout, 30)))
    }
    public func prepare(_ document: LyricsDocument, target: String) async throws -> LyricsDocument {
        try Task.checkCancellation()
        if LyricsLanguagePolicy.current(document, target: target), let admission = document.translationAdmission,
           admission.originalPrediction.modelIdentity == Self.modelIdentity,
           admission.translationPrediction == nil || admission.translationPrediction?.modelIdentity == Self.modelIdentity { return document }
        let sample = LyricsLanguagePolicy.sample(document, role: .original)
        guard !sample.isEmpty, LyricsTextPolicy.quality(document.lines) == .usable else {
            return LyricsLanguagePolicy.prepare(document, target: target, original: .init(diagnostic: "no-lyric-body"))
        }
        let original = try await predict(sample, role: .original)
        var translation: LyricsLanguagePrediction?
        if !LyricsLanguagePolicy.predictionMatches(original, sample: sample, target: target),
           !LyricsLanguagePolicy.trustedProviderTarget(document, target: target) {
            let provider = LyricsLanguagePolicy.sample(document, role: .providerTranslation)
            if !provider.isEmpty { translation = try await predict(provider, role: .providerTranslation) }
        }
        try Task.checkCancellation()
        return LyricsLanguagePolicy.prepare(document, target: target, original: original, translation: translation,
                                            generation: document.translationAdmission?.generation ?? 0)
    }
    public func predict(_ sample: String, role: LyricsLanguageRole) async throws -> LyricsLanguagePrediction {
        let cancellation = Cancellation()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                queue.async { [engine] in
                    do { continuation.resume(returning: try engine.predict(sample, role: role, cancellation: cancellation)) }
                    catch { continuation.resume(throwing: error) }
                }
            }
        } onCancel: { cancellation.cancel() }
    }
    public func statistics() async -> Statistics {
        await withCheckedContinuation { continuation in queue.async { [engine] in
            var value = engine.statistics; value.cacheEntries = engine.cache.count; value.workerRunning = engine.process?.isRunning == true
            continuation.resume(returning: value)
        } }
    }
    /// Called after cancelling/draining provider invocations. Exit is confirmed
    /// before returning; a later request may start a new validated process.
    public func stop(clearPredictions: Bool = false) async {
        await withCheckedContinuation { continuation in queue.async { [engine] in
            engine.stop(); if clearPredictions { engine.cache.removeAll() }; continuation.resume()
        } }
    }
}

private final class Cancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    func cancel() { lock.withLock { cancelled = true } }
    func check() throws { if lock.withLock({ cancelled }) { throw CancellationError() } }
}

/// All mutable state is confined to FastTextLanguageIdentifier.queue.
private final class Engine: @unchecked Sendable {
    private enum Failure: String, Error { case modelUnavailable, invalidFrame, pipeFailure, deadline, invalidIdentity, invalidPrediction }
    private struct Message: Decodable {
        struct Prediction: Decodable { var label: String; var score: Double }
        var type: String
        var `protocol`: Int?
        var modelSha256: String
        var engineCommit: String?
        var id: String?
        var predictions: [Prediction]?
    }
    struct Cached { var value: LyricsLanguagePrediction; var accessed: ContinuousClock.Instant }
    let executable: URL, model: URL, timeout: TimeInterval
    var process: Process?
    private var input: FileHandle?, output: FileHandle?
    private var pending = Data()
    var cache: [String: Cached] = [:]
    var statistics = FastTextLanguageIdentifier.Statistics()
    init(executable: URL, model: URL, timeout: TimeInterval) { self.executable = executable; self.model = model; self.timeout = timeout }
    deinit { stop() }

    func predict(_ sample: String, role: LyricsLanguageRole, cancellation: Cancellation) throws -> LyricsLanguagePrediction {
        try cancellation.check()
        guard sample.utf8.count < 16 * 1024 * 1024 else {
            return .init(modelIdentity: FastTextLanguageIdentifier.modelIdentity, diagnostic: "language-model-invalidFrame")
        }
        let key = role.rawValue + ":" + FastTextLanguageIdentifier.modelIdentity + ":" + SHA256.hash(data: Data(sample.utf8)).map { String(format: "%02x", $0) }.joined()
        let now = ContinuousClock.now
        cache = cache.filter { $0.value.accessed.duration(to: now) < .seconds(600) }
        if var hit = cache[key] { try cancellation.check(); hit.accessed = now; cache[key] = hit; hit.value.cacheHit = true; return hit.value }
        do {
            guard !sample.isEmpty, !sample.contains("\0"), !sample.contains("\n"), !sample.contains("\r") else { throw Failure.invalidFrame }
            let deadline = now.advanced(by: .seconds(timeout))
            try start(cancellation, deadline: deadline)
            let identifier = UUID().uuidString
            let request = try JSONSerialization.data(withJSONObject: ["type": "predict", "id": identifier, "text": sample])
            guard request.count < 16 * 1024 * 1024 else { throw Failure.invalidFrame }
            try write(request + Data([10]), cancellation, deadline: deadline)
            let message = try read(cancellation, deadline: deadline)
            guard message.type == "prediction", message.id == identifier, message.modelSha256 == FastTextLanguageIdentifier.modelSHA256 else { throw Failure.invalidIdentity }
            guard let values = message.predictions, (1...2).contains(values.count), values.allSatisfy({
                $0.label.hasPrefix("__label__") && $0.label.utf8.count <= 64 && $0.score.isFinite && $0.score >= 0 && $0.score <= 1.0001
            }), values.count < 2 || values[0].score >= values[1].score else { throw Failure.invalidPrediction }
            try cancellation.check()
            let value = LyricsLanguagePrediction(label: values[0].label, score: values[0].score, runnerUpScore: values.count == 2 ? values[1].score : 0, modelIdentity: FastTextLanguageIdentifier.modelIdentity)
            statistics.predictions += 1
            if cache.count >= 128, let oldest = cache.min(by: { $0.value.accessed < $1.value.accessed })?.key { cache.removeValue(forKey: oldest) }
            cache[key] = .init(value: value, accessed: .now)
            return value
        } catch {
            stop() // Cancelling a computation never leaves an unowned worker.
            try cancellation.check()
            return .init(modelIdentity: FastTextLanguageIdentifier.modelIdentity, diagnostic: "language-model-" + ((error as? Failure)?.rawValue ?? "unavailable"))
        }
    }
    private func start(_ cancellation: Cancellation, deadline: ContinuousClock.Instant) throws {
        if process?.isRunning == true { return }
        stop()
        var attributes = stat()
        guard lstat(executable.path, &attributes) == 0, attributes.st_mode & S_IFMT == S_IFREG,
              lstat(model.path, &attributes) == 0, attributes.st_mode & S_IFMT == S_IFREG else { throw Failure.modelUnavailable }
        let child = Process(), incoming = Pipe(), outgoing = Pipe()
        child.executableURL = executable; child.arguments = ["--model", model.path]
        child.standardInput = incoming; child.standardOutput = outgoing; child.standardError = FileHandle.nullDevice
        try cancellation.check()
        try child.run()
        process = child; input = incoming.fileHandleForWriting; output = outgoing.fileHandleForReading
        try? incoming.fileHandleForReading.close(); try? outgoing.fileHandleForWriting.close()
        for fd in [input!.fileDescriptor, output!.fileDescriptor] {
            guard fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK) == 0, fcntl(fd, F_SETNOSIGPIPE, 1) == 0 else { throw Failure.pipeFailure }
        }
        let ready = try read(cancellation, deadline: deadline)
        guard ready.type == "ready", ready.protocol == 1, ready.modelSha256 == FastTextLanguageIdentifier.modelSHA256,
              ready.engineCommit == FastTextLanguageIdentifier.engineCommit else { throw Failure.invalidIdentity }
        statistics.modelLoads += 1
    }
    private func wait(_ fd: Int32, events: Int16, cancellation: Cancellation, deadline: ContinuousClock.Instant) throws {
        try cancellation.check()
        guard ContinuousClock.now < deadline else { throw Failure.deadline }
        var descriptor = pollfd(fd: fd, events: events, revents: 0)
        let result = poll(&descriptor, 1, 25)
        if result < 0 && errno != EINTR { throw Failure.pipeFailure }
        if descriptor.revents & Int16(POLLERR | POLLNVAL) != 0 { throw Failure.pipeFailure }
    }
    private func write(_ bytes: Data, _ cancellation: Cancellation, deadline: ContinuousClock.Instant) throws {
        guard let fd = input?.fileDescriptor else { throw Failure.pipeFailure }
        try bytes.withUnsafeBytes { buffer in
            var offset = 0
            while offset < bytes.count {
                try wait(fd, events: Int16(POLLOUT), cancellation: cancellation, deadline: deadline)
                let count = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), bytes.count - offset)
                if count > 0 { offset += count }
                else if count == 0 || errno != EAGAIN && errno != EINTR { throw Failure.pipeFailure }
            }
        }
    }
    private func read(_ cancellation: Cancellation, deadline: ContinuousClock.Instant) throws -> Message {
        guard let fd = output?.fileDescriptor else { throw Failure.pipeFailure }
        var chunk = [UInt8](repeating: 0, count: 4096)
        while true {
            try cancellation.check()
            if let end = pending.firstIndex(of: 10) {
                let frame = pending[..<end]; pending.removeSubrange(...end)
                do { return try JSONDecoder().decode(Message.self, from: frame) } catch { throw Failure.invalidFrame }
            }
            guard pending.count < 65536 else { throw Failure.invalidFrame }
            try wait(fd, events: Int16(POLLIN), cancellation: cancellation, deadline: deadline)
            let count = Darwin.read(fd, &chunk, chunk.count)
            if count > 0 { pending.append(contentsOf: chunk.prefix(count)) }
            else if count == 0 || errno != EAGAIN && errno != EINTR { throw Failure.pipeFailure }
        }
    }
    func stop() {
        try? input?.close(); input = nil
        if let child = process {
            if child.isRunning {
                child.terminate()
                let deadline = ContinuousClock.now.advanced(by: .seconds(2))
                while child.isRunning && ContinuousClock.now < deadline { Thread.sleep(forTimeInterval: 0.01) }
                if child.isRunning { kill(child.processIdentifier, SIGKILL) }
            }
            child.waitUntilExit()
        }
        try? output?.close(); output = nil; process = nil; pending.removeAll(keepingCapacity: false)
    }
}
