import Foundation
import CryptoKit
import Darwin
import OSLog
import DropSpaceCore

public struct PlainHyExecution: Sendable, Equatable {
    public var text: String
    public var backend: String
    public var usedCPUFallback: Bool
    public var processID: Int32
    public init(text: String, backend: String, usedCPUFallback: Bool, processID: Int32) {
        self.text = text; self.backend = backend; self.usedCPUFallback = usedCPUFallback; self.processID = processID
    }
}
public enum PlainHyWorkerFailure: String, Error, LocalizedError, Sendable {
    case queueDeadline, requestDeadline, invalidRuntime, invalidFrame, pipeFailure, unavailableMemory, memoryPressure, residentLimit, modelNotReady, runtimeLoad, modelLoad, decodeFailure
    public var errorDescription: String? { "Local translation: " + rawValue }
}

/// One application owner, one globally serialized inference operation. Model
/// leases outlive the resident process and are released only after confirmed exit.
public actor PlainHyTranslationWorker {
    private static let logger = Logger(subsystem: "io.github.airanluo-dot.DropSpace.macOS", category: "lyrics-runtime")
    public static let engineCommit = "7fe450e19305b828c199d602c23a8337aaa1f03b"
    private static let gate = PlainHyInferenceGate()
    private let models: AIModelStore
    private let queue = DispatchQueue(label: "app.dropspace.lyrics.translation", qos: .utility)
    private let engine: PlainHyEngine
    private var lease: AIModelLease?
    private var configuration: String?
    private var revision = 0
    private var stopping = 0
    private var active: PlainHyCancellation?
    private var idle: Task<Void, Never>?
    private var residentWatch: Task<Void, Never>?
    private var retryMetalAfter: [String: ContinuousClock.Instant] = [:]
    public private(set) var lastExecution: PlainHyExecution?
    public private(set) var lastFailure: String?
    public private(set) var runtimeStatus = PlainHyRuntimeStatus()
    private var statusObserver: (@Sendable (PlainHyRuntimeStatus) async -> Void)?

    public func observeExecution(_ observer: (@Sendable (PlainHyRuntimeStatus) async -> Void)?) async {
        statusObserver = observer
        await publishStatus()
    }
    private func publishStatus() async { await statusObserver?(runtimeStatus) }
    private func logFailure(_ failure: PlainHyRuntimeStatus.Failure) {
        Self.logger.warning("Local inference failed: model=\(failure.modelID, privacy: .public) requested=\(failure.requestedBackend, privacy: .public) attempted=\(failure.attemptedBackend, privacy: .public) reason=\(failure.reason?.rawValue ?? "external", privacy: .public) exit=\(failure.exit?.status ?? -1) signal=\(failure.exit?.wasSignal ?? false) hostStopped=\(failure.exit?.terminatedByHost ?? false). Detail=\(failure.detail, privacy: .private)")
    }

    public init(executable: URL, models: AIModelStore) {
        self.models = models; engine = PlainHyEngine(executable: executable)
    }
    public func inferenceIdentity(modelID: String) async throws -> String {
        let model = try models.catalog.model(modelID)
        let binary = try await perform { try $0.fingerprint() }
        let value = [model.fullsha, binary, Self.engineCommit, model.workerProfile, PlainHyLyrics.version,
                     PlainHyLyrics.sampler, "macos-arm64-resident-v1"].joined(separator: "\n")
        return SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    public func translate(prompt: String, modelID: String, preferMetal: Bool) async throws -> PlainHyExecution {
        guard stopping == 0 else { throw CancellationError() }
        guard !prompt.isEmpty, prompt.utf8.count <= PlainHyLyrics.maximumPromptBytes else { throw PlainHyWorkerFailure.invalidFrame }
        let run = revision, cancellation = PlainHyCancellation()
        let permit = try await Self.gate.acquire()
        do {
            try Task.checkCancellation(); guard revision == run, stopping == 0 else { throw CancellationError() }
            idle?.cancel(); idle = nil; active = cancellation
            let value = try await withTaskCancellationHandler {
                try await execute(prompt: prompt, modelID: modelID, preferMetal: preferMetal, cancellation: cancellation, revision: run)
            } onCancel: { cancellation.cancel() }
            try Task.checkCancellation(); try cancellation.check()
            guard revision == run else { throw CancellationError() }
            active = nil; lastExecution = value
            runtimeStatus.current = nil
            runtimeStatus.last = .init(backend: value.backend, modelID: modelID, at: Date(), processID: value.processID)
            await publishStatus()
            idle = Task { [weak self] in
                do { try await Task.sleep(for: .seconds(60)); await self?.stopIfIdle(run) } catch { }
            }
            if residentWatch == nil {
                residentWatch = Task { [weak self] in
                    while !Task.isCancelled {
                        do { try await Task.sleep(for: .milliseconds(250)) } catch { return }
                        await self?.inspectIdle()
                    }
                }
            }
            await Self.gate.release(permit); return value
        } catch {
            await closeSession(); active = nil
            runtimeStatus.current = nil; await publishStatus()
            if !(error is CancellationError) { lastFailure = error.localizedDescription }
            await Self.gate.release(permit); throw error
        }
    }
    private func execute(prompt: String, modelID: String, preferMetal: Bool, cancellation: PlainHyCancellation, revision run: Int) async throws -> PlainHyExecution {
        let preference = modelID + (preferMetal ? ":metal" : ":cpu")
        if configuration != preference {
            await closeSession(); retryMetalAfter.removeAll(); configuration = preference
            runtimeStatus = .init(); lastFailure = nil
        }
        let deadline = ContinuousClock.now.advanced(by: .seconds(60))
        var metal = preferMetal && (retryMetalAfter[modelID].map { ContinuousClock.now >= $0 } ?? true)
        for attempt in 0...1 {
            try cancellation.check(); guard revision == run else { throw CancellationError() }
            let mode = metal ? "metal" : "cpu"
            if attempt == 1 { runtimeStatus.failure?.cpuFallbackAttempted = true }
            runtimeStatus.current = .init(backend: mode, modelID: modelID, phase: .starting)
            await publishStatus()
            do {
                if lease == nil {
                    let descriptor = try models.catalog.model(modelID)
                    let inventory = try await models.inspect(modelID)
                    guard inventory.installed, inventory.enabled else { throw PlainHyWorkerFailure.modelNotReady }
                    lease = try await models.leaseForNativeVerification(modelID)
                    // Admit before model verification charges its mapped pages.
                    try await perform { try $0.admit(descriptor) }
                }
                guard let selected = lease else { throw PlainHyWorkerFailure.modelLoad }
                let request = try await perform {
                    try $0.begin(prompt: prompt, lease: selected, backend: mode, cancellation: cancellation, deadline: deadline)
                }
                try cancellation.check(); guard revision == run else { throw CancellationError() }
                runtimeStatus.current = .init(backend: mode, modelID: modelID, phase: .executing)
                await publishStatus()
                let output = try await perform { try $0.finish(request: request, backend: mode, cancellation: cancellation, deadline: deadline) }
                try cancellation.check(); guard revision == run else { throw CancellationError() }
                return .init(text: output.text, backend: mode, usedCPUFallback: preferMetal && !metal, processID: output.processID)
            } catch {
                // No fallback while any resource from the previous worker survives.
                let exit = await closeSession(); try cancellation.check()
                guard revision == run else { throw CancellationError() }
                let eligible: Bool
                switch error as? PlainHyWorkerFailure {
                case .runtimeLoad, .pipeFailure, .decodeFailure: eligible = true
                default: eligible = false
                }
                let fallback = metal && attempt == 0 && eligible && ContinuousClock.now < deadline
                runtimeStatus.current = nil
                // Preserve the failed GPU attempt when its CPU fallback also
                // fails, matching the baseline's causal runtime record.
                if metal || runtimeStatus.failure == nil {
                    runtimeStatus.failure = .init(requestedBackend: preferMetal ? "metal" : "cpu", attemptedBackend: mode,
                        modelID: modelID, at: Date(), reason: error as? PlainHyWorkerFailure,
                        detail: error.localizedDescription, exit: exit, cpuFallbackAttempted: false)
                    logFailure(runtimeStatus.failure!)
                }
                await publishStatus()
                guard fallback else { throw error }
                lastFailure = error.localizedDescription; retryMetalAfter[modelID] = .now.advanced(by: .seconds(60)); metal = false
            }
        }
        throw PlainHyWorkerFailure.runtimeLoad
    }
    public func stop(clearFailures: Bool = false) async {
        stopping += 1; defer { stopping -= 1 }
        revision &+= 1; active?.cancel(); idle?.cancel(); idle = nil; residentWatch?.cancel(); residentWatch = nil
        // Stop cannot time out into releasing a still-owned model or inference gate.
        let permit = await Self.gate.acquireForCleanup()
        await closeSession(); lastExecution = nil
        // Retiring a song/idle process must not erase the failed Metal
        // preference. Only an explicit retry/configuration change does that.
        runtimeStatus.current = nil
        if clearFailures { configuration = nil; lastFailure = nil; retryMetalAfter.removeAll(); runtimeStatus = .init() }
        await publishStatus()
        await Self.gate.release(permit)
    }
    private func stopIfIdle(_ run: Int) async {
        guard revision == run, active == nil, !Task.isCancelled else { return }
        // This is the idle task itself. stop() must not cancel its caller before
        // the caller acquires the cleanup gate and confirms process exit.
        idle = nil
        await stop()
    }
    private func inspectIdle() async {
        guard active == nil, lease != nil, !Task.isCancelled,
              let permit = try? await Self.gate.acquire() else { return }
        if active == nil, lease != nil, !Task.isCancelled {
            do { try await perform { try $0.inspectIdle() } }
            catch {
                lastFailure = error.localizedDescription
                let exit = await closeSession()
                if let last = runtimeStatus.last, last.backend == "metal" || runtimeStatus.failure == nil {
                    runtimeStatus.failure = .init(requestedBackend: configuration?.hasSuffix(":metal") == true ? "metal" : "cpu",
                        attemptedBackend: last.backend, modelID: last.modelID, at: Date(),
                        reason: error as? PlainHyWorkerFailure, detail: error.localizedDescription,
                        exit: exit, cpuFallbackAttempted: false)
                    logFailure(runtimeStatus.failure!)
                }
                runtimeStatus.current = nil; await publishStatus()
            }
        }
        await Self.gate.release(permit)
    }
    @discardableResult private func closeSession() async -> PlainHyProcessExit? {
        let exit = await withCheckedContinuation { continuation in queue.async { [engine] in continuation.resume(returning: engine.stop()) } }
        if let owned = lease { lease = nil; await models.release(owned) }
        return exit
    }
    private func perform<T: Sendable>(_ body: @escaping @Sendable (PlainHyEngine) throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in queue.async { [engine] in
            do { continuation.resume(returning: try body(engine)) } catch { continuation.resume(throwing: error) }
        } }
    }
}

private actor PlainHyInferenceGate {
    private struct Waiter {
        var id: UUID
        var resume: @Sendable (UUID) -> Void
        var fail: (@Sendable (any Error) -> Void)?
        var expiry: Task<Void, Never>?
    }
    private var owner: UUID?
    private var waiting: [Waiter] = []
    func acquire(timeout: Duration? = .seconds(10)) async throws -> UUID {
        try Task.checkCancellation(); let id = UUID()
        if owner == nil { owner = id; return id }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                let expiry = timeout.map { duration in Task { [weak self] in
                    do { try await Task.sleep(for: duration); await self?.cancel(id, deadline: true) } catch { }
                } }
                waiting.append(.init(id: id, resume: { continuation.resume(returning: $0) }, fail: { continuation.resume(throwing: $0) }, expiry: expiry))
            }
        } onCancel: { Task { await self.cancel(id, deadline: false) } }
    }
    /// Cancellation is a reason to clean up, never a reason to abandon cleanup.
    func acquireForCleanup() async -> UUID {
        let id = UUID()
        if owner == nil { owner = id; return id }
        return await withCheckedContinuation { continuation in
            waiting.append(.init(id: id, resume: { continuation.resume(returning: $0) }, fail: nil, expiry: nil))
        }
    }
    private func cancel(_ id: UUID, deadline: Bool) {
        guard let index = waiting.firstIndex(where: { $0.id == id }) else { return }
        let value = waiting.remove(at: index); value.expiry?.cancel()
        value.fail?(deadline ? PlainHyWorkerFailure.queueDeadline : CancellationError())
    }
    func release(_ id: UUID) {
        guard owner == id else { return }; owner = nil
        if !waiting.isEmpty {
            let next = waiting.removeFirst(); next.expiry?.cancel(); owner = next.id; next.resume(next.id)
        }
    }
}

private final class PlainHyCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    func cancel() { lock.withLock { cancelled = true } }
    func check() throws { if lock.withLock({ cancelled }) { throw CancellationError() } }
}

/// Only the utility queue touches pipes and Process. The pressure callback owns
/// just its locked signal, so it remains responsive while the queue polls I/O.
private final class PlainHyEngine: @unchecked Sendable {
    private struct Message: Decodable {
        var `protocol`: Int
        var ready: Bool?; var selectionProtocol: Int?; var backend: String?; var modelProfile: String?; var engineCommit: String?
        var id: String?; var complete: Bool?; var text: String?
    }
    private final class Pressure: @unchecked Sendable {
        let lock = NSLock(); var raised = false
        func set(_ value: Bool) { lock.withLock { raised = value } }
        var isRaised: Bool { lock.withLock { raised } }
    }
    let executable: URL
    private var process: Process?
    private var input: FileHandle?, output: FileHandle?
    private var pending = Data()
    private var sessionKey: String?
    private var limit: UInt64 = 0
    private var checkedAt = ContinuousClock.now
    private let pressure = Pressure()
    private let pressureSource: any DispatchSourceMemoryPressure
    init(executable: URL) {
        self.executable = executable
        pressureSource = DispatchSource.makeMemoryPressureSource(eventMask: [.normal, .warning, .critical], queue: .global(qos: .utility))
        pressureSource.setEventHandler { [weak self] in
            guard let self else { return }; pressure.set(!pressureSource.data.intersection([.warning, .critical]).isEmpty)
        }
        pressureSource.resume()
    }
    deinit { pressureSource.cancel(); stop() }
    func fingerprint() throws -> String {
        let fd = open(executable.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw PlainHyWorkerFailure.invalidRuntime }; defer { close(fd) }
        var attributes = stat()
        guard fstat(fd, &attributes) == 0, attributes.st_mode & S_IFMT == S_IFREG, attributes.st_size > 0, attributes.st_size <= 100_000_000 else { throw PlainHyWorkerFailure.invalidRuntime }
        var digest = SHA256(), buffer = [UInt8](repeating: 0, count: 65536)
        while true {
            let count = Darwin.read(fd, &buffer, buffer.count)
            if count < 0 { if errno == EINTR { continue }; throw PlainHyWorkerFailure.invalidRuntime }
            if count == 0 { break }; digest.update(data: Data(buffer.prefix(count)))
        }
        return digest.finalize().map { String(format: "%02x", $0) }.joined()
    }
    func admit(_ model: AIModelDescriptor) throws {
        let maximum: UInt64 = model.id == "hy-mt2-7b-q8-plain-beta" ? 12 * 1024 * 1024 * 1024 : 3 * 1024 * 1024 * 1024
        guard !pressure.isRaised else { throw PlainHyWorkerFailure.memoryPressure }
        guard Self.availableMemory() >= maximum + 1024 * 1024 * 1024 else { throw PlainHyWorkerFailure.unavailableMemory }
    }
    func inspectIdle() throws {
        guard process?.isRunning == true else { throw PlainHyWorkerFailure.pipeFailure }
        try check(PlainHyCancellation(), deadline: .now.advanced(by: .seconds(1)))
    }
    func begin(prompt: String, lease: AIModelLease, backend: String, cancellation: PlainHyCancellation, deadline: ContinuousClock.Instant) throws -> String {
        let key = lease.model.fullsha + ":" + backend
        if sessionKey != key || process?.isRunning != true {
            stop(); try check(cancellation, deadline: deadline)
            limit = lease.model.id == "hy-mt2-7b-q8-plain-beta" ? 12 * 1024 * 1024 * 1024 : 3 * 1024 * 1024 * 1024
            _ = try fingerprint()
            let child = Process(), incoming = Pipe(), outgoing = Pipe()
            child.executableURL = executable
            child.arguments = ["--model", lease.url.path, "--mode", backend, "--model-profile", lease.model.workerProfile]
            child.environment = ProcessInfo.processInfo.environment.filter { key, _ in
                !["LLAMA_", "GGML_", "VK_", "VULKAN_", "CUDA_", "HIP_"].contains { key.hasPrefix($0) }
            }.merging(["OMP_NUM_THREADS": "4"]) { _, value in value }
            child.standardInput = incoming; child.standardOutput = outgoing; child.standardError = FileHandle.nullDevice
            try child.run(); process = child
            input = incoming.fileHandleForWriting; output = outgoing.fileHandleForReading
            try? incoming.fileHandleForReading.close(); try? outgoing.fileHandleForWriting.close()
            for fd in [input!.fileDescriptor, output!.fileDescriptor] {
                guard fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK) == 0, fcntl(fd, F_SETNOSIGPIPE, 1) == 0 else { throw PlainHyWorkerFailure.pipeFailure }
            }
            let ready = try read(cancellation, deadline: deadline)
            guard ready.protocol == 1, ready.ready == true, ready.selectionProtocol == 2, ready.backend == backend,
                  ready.modelProfile == lease.model.workerProfile, ready.engineCommit == PlainHyTranslationWorker.engineCommit else { throw PlainHyWorkerFailure.invalidRuntime }
            sessionKey = key
        }
        let id = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let request = try JSONSerialization.data(withJSONObject: ["protocol": 1, "id": id, "prompt": prompt]) + Data([10])
        try write(request, cancellation, deadline: deadline)
        return id
    }
    func finish(request id: String, backend: String, cancellation: PlainHyCancellation, deadline: ContinuousClock.Instant) throws -> PlainHyExecution {
        let message = try read(cancellation, deadline: deadline)
        guard message.protocol == 1, message.id == id, message.complete == true, let raw = message.text, raw.utf8.count <= PlainHyLyrics.maximumLineBytes else { throw PlainHyWorkerFailure.invalidFrame }
        let result = PlainHyLyrics.removeRuntimeTerminator(raw)
        guard PlainHyLyrics.completeLine(result) else { throw PlainHyWorkerFailure.invalidFrame }
        try check(cancellation, deadline: deadline)
        return .init(text: result, backend: backend, usedCPUFallback: false, processID: process!.processIdentifier)
    }
    private func check(_ cancellation: PlainHyCancellation, deadline: ContinuousClock.Instant) throws {
        try cancellation.check()
        guard ContinuousClock.now < deadline else { throw PlainHyWorkerFailure.requestDeadline }
        guard !pressure.isRaised else { throw PlainHyWorkerFailure.memoryPressure }
        if let process, process.isRunning, checkedAt.duration(to: .now) >= .milliseconds(100) {
            checkedAt = .now; var info = proc_taskinfo()
            guard proc_pidinfo(process.processIdentifier, PROC_PIDTASKINFO, 0, &info, Int32(MemoryLayout<proc_taskinfo>.size)) == MemoryLayout<proc_taskinfo>.size else {
                if process.isRunning { throw PlainHyWorkerFailure.residentLimit }; return
            }
            guard info.pti_resident_size <= limit else { throw PlainHyWorkerFailure.residentLimit }
        }
    }
    private func wait(_ fd: Int32, events: Int16, cancellation: PlainHyCancellation, deadline: ContinuousClock.Instant) throws {
        try check(cancellation, deadline: deadline)
        var descriptor = pollfd(fd: fd, events: events, revents: 0)
        if poll(&descriptor, 1, 25) < 0 && errno != EINTR { throw PlainHyWorkerFailure.pipeFailure }
        if descriptor.revents & Int16(POLLERR | POLLNVAL) != 0 { throw pipeError() }
    }
    private func pipeError() -> PlainHyWorkerFailure {
        // EOF may arrive just before Foundation observes exit. Classify a model
        // or protocol failure before considering a different acceleration mode.
        if let process, process.isRunning {
            let until = ContinuousClock.now.advanced(by: .milliseconds(100))
            while process.isRunning && ContinuousClock.now < until { Thread.sleep(forTimeInterval: 0.005) }
        }
        guard let process, !process.isRunning else { return .pipeFailure }
        switch process.terminationStatus {
        case 65, 66: return .runtimeLoad
        case 67: return .modelLoad
        case 64, 68, 69, 70, 72, 73: return .invalidFrame
        case 71: return .decodeFailure
        default: return .pipeFailure
        }
    }
    private func write(_ bytes: Data, _ cancellation: PlainHyCancellation, deadline: ContinuousClock.Instant) throws {
        guard let fd = input?.fileDescriptor else { throw PlainHyWorkerFailure.pipeFailure }
        try bytes.withUnsafeBytes { buffer in
            var offset = 0
            while offset < bytes.count {
                try wait(fd, events: Int16(POLLOUT), cancellation: cancellation, deadline: deadline)
                let count = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), bytes.count - offset)
                if count > 0 { offset += count } else if count == 0 || errno != EAGAIN && errno != EINTR { throw pipeError() }
            }
        }
    }
    private func read(_ cancellation: PlainHyCancellation, deadline: ContinuousClock.Instant) throws -> Message {
        guard let fd = output?.fileDescriptor else { throw PlainHyWorkerFailure.pipeFailure }
        var chunk = [UInt8](repeating: 0, count: 4096)
        while true {
            try check(cancellation, deadline: deadline)
            if let end = pending.firstIndex(of: 10) {
                guard end <= 32768 else { throw PlainHyWorkerFailure.invalidFrame }
                let frame = pending[..<end]; pending.removeSubrange(...end)
                do { return try JSONDecoder().decode(Message.self, from: frame) } catch { throw PlainHyWorkerFailure.invalidFrame }
            }
            guard pending.count <= 32768 else { throw PlainHyWorkerFailure.invalidFrame }
            try wait(fd, events: Int16(POLLIN), cancellation: cancellation, deadline: deadline)
            let count = Darwin.read(fd, &chunk, chunk.count)
            if count > 0 { pending.append(contentsOf: chunk.prefix(count)) }
            else if count == 0 || errno != EAGAIN && errno != EINTR { throw pipeError() }
        }
    }
    @discardableResult func stop() -> PlainHyProcessExit? {
        try? input?.close(); input = nil
        var exit: PlainHyProcessExit?
        if let process {
            let terminatedByHost = process.isRunning
            if process.isRunning {
                process.terminate()
                let deadline = ContinuousClock.now.advanced(by: .seconds(2))
                while process.isRunning && ContinuousClock.now < deadline { Thread.sleep(forTimeInterval: 0.01) }
                if process.isRunning { kill(process.processIdentifier, SIGKILL) }
            }
            process.waitUntilExit()
            exit = .init(status: process.terminationStatus, wasSignal: process.terminationReason == .uncaughtSignal,
                         terminatedByHost: terminatedByHost)
        }
        try? output?.close(); output = nil; process = nil; sessionKey = nil; pending.removeAll(keepingCapacity: false)
        return exit
    }
    private static func availableMemory() -> UInt64 {
        var vm = vm_statistics64_data_t(); var count = mach_msg_type_number_t(MemoryLayout.size(ofValue: vm) / MemoryLayout<integer_t>.size)
        let host = mach_host_self(); defer { mach_port_deallocate(mach_task_self_, host) }
        let result = withUnsafeMutablePointer(to: &vm) { pointer in pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
            host_statistics64(host, HOST_VM_INFO64, $0, &count)
        } }
        let page = sysconf(_SC_PAGESIZE)
        guard result == KERN_SUCCESS, page > 0 else { return 0 }
        return (UInt64(vm.free_count) + UInt64(vm.inactive_count)) * UInt64(page)
    }
}
