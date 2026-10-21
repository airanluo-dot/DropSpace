import Foundation
import Darwin
import DropSpaceCore

public struct ModuleProcessState: Sendable {
    public let guardianPID: Int32?
    public let workerPID: Int32?
    public let running: Bool
    public let exitConfirmed: Bool
}

/// Retain the client until stop() confirms exit. One request owns its complete
/// eight-second budget, including admission; cancellation after sending retires
/// the session so late replies cannot be reused by a new request.
public actor ModuleWorkerClient {
    public let manifest: ModuleManifest
    private let engine: ModuleProcessEngine
    private let queue = DispatchQueue(label: "app.dropspace.module.ipc", qos: .utility)
    private let gate = DownloadPermitPool(limit: 1)
    private let lifetime = ModuleCancellation()
    private var ready = false
    private var started = false
    private var stopping = false
    public init(guardian: URL, executable: URL, dataDirectory: URL, manifest: ModuleManifest, onExit: @escaping @Sendable () -> Void = {}) {
        self.manifest = manifest
        engine = ModuleProcessEngine(guardian: guardian, executable: executable, dataDirectory: dataDirectory, dataVersion: manifest.data.version, onExit: onExit)
    }
    public func start() async throws {
        guard !started, !stopping else { throw DropSpaceError.unavailable("ModuleNotRunning") }
        started = true
        let selected = manifest
        let reply = try await exchange("hello", payload: .object([
            .init("moduleId", .string(selected.id)), .init("moduleVersion", .string(selected.version)),
            .init("hostInterface", .number(String(ModuleContract.hostInterface))), .init("protocol", .number(String(ModuleContract.protocolVersion))),
            .init("dataVersion", .number(String(selected.data.version))),
            .init("capabilities", .array((selected.requiredCapabilities + selected.optionalCapabilities)
                .filter { ModuleContract.hasCapability(selected, $0.id) }.map { capability in
                    .object([.init("Id", .string(capability.id)), .init("Version", .number(String(capability.version))),
                             .init("Fallback", capability.fallback.map(ModuleJSON.string) ?? .null)])
                }))
        ]), launching: true)
        guard reply.result["moduleId"]?.string == selected.id, reply.result["moduleVersion"]?.string == selected.version,
              reply.result["protocol"]?.integer == ModuleContract.protocolVersion, reply.result["dataVersion"]?.integer == selected.data.version else {
            stopping = true; lifetime.cancel(); _ = await perform { $0.stop(graceful: false) }
            throw DropSpaceError.invalid("ModuleHandshakeMismatch")
        }
        try lifetime.check(); ready = true
    }
    public func request(_ method: String, payload: ModuleJSON) async throws -> ModuleReply {
        guard ready, !stopping else { throw DropSpaceError.unavailable("ModuleNotRunning") }
        return try await exchange(method, payload: payload, launching: false)
    }
    private func exchange(_ method: String, payload: ModuleJSON, launching: Bool) async throws -> ModuleReply {
        let deadline = ContinuousClock.now.advanced(by: .seconds(8)), cancellation = ModuleCancellation(), lifetime = lifetime
        let permit = try await gate.acquire(deadline: deadline)
        var entered = false
        do {
            try Task.checkCancellation(); try lifetime.check()
            entered = true
            let result = try await withTaskCancellationHandler {
                try await performThrowing { engine in
                    do {
                        if launching { try engine.launch(cancellation: cancellation, lifetime: lifetime, deadline: deadline) }
                        return try engine.exchange(method, payload: payload, cancellation: cancellation, lifetime: lifetime, deadline: deadline)
                    } catch {
                        lifetime.cancel(); _ = engine.stop(graceful: false); throw error
                    }
                }
            } onCancel: { cancellation.cancel() }
            try Task.checkCancellation(); try lifetime.check()
            await gate.release(permit); return result
        } catch {
            if entered { lifetime.cancel(); _ = await perform { $0.stop(graceful: false) } }
            if lifetime.cancelled { ready = false; stopping = true }
            await gate.release(permit); throw error
        }
    }
    public func processState() async -> ModuleProcessState { await perform { $0.state() } }
    public func stop() async -> Bool {
        stopping = true; ready = false; lifetime.cancel()
        // Work already on the serial IO queue observes cancellation within a
        // poll interval. No actor/main executor blocks on pipes or process exit.
        return await perform { $0.stop(graceful: true) }
    }
    private func perform<T: Sendable>(_ body: @escaping @Sendable (ModuleProcessEngine) -> T) async -> T {
        await withCheckedContinuation { continuation in queue.async { [engine] in continuation.resume(returning: body(engine)) } }
    }
    private func performThrowing<T: Sendable>(_ body: @escaping @Sendable (ModuleProcessEngine) throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in queue.async { [engine] in
            do { continuation.resume(returning: try body(engine)) } catch { continuation.resume(throwing: error) }
        } }
    }
}

private final class ModuleCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false
    var cancelled: Bool { lock.withLock { value } }
    func cancel() { lock.withLock { value = true } }
    func check() throws { if cancelled { throw CancellationError() } }
}

/// Mutable state is confined to the client's private serial queue.
private final class ModuleProcessEngine: @unchecked Sendable {
    let guardian: URL, executable: URL, dataDirectory: URL, dataVersion: Int
    private let session = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    private var process: Process?
    private var input: FileHandle?, output: FileHandle?
    private var pending = Data()
    private var nextID: Int64 = 0
    private var workerPID: Int32?
    private var confirmed = true
    private let onExit: @Sendable () -> Void
    init(guardian: URL, executable: URL, dataDirectory: URL, dataVersion: Int, onExit: @escaping @Sendable () -> Void) {
        self.guardian = guardian; self.executable = executable; self.dataDirectory = dataDirectory; self.dataVersion = dataVersion
        self.onExit = onExit
    }
    deinit {
        // EOF tells the independent guardian to kill/reap its group. The runtime
        // must still retain this owner on an unconfirmed explicit stop.
        try? input?.close(); try? output?.close()
    }
    func state() -> ModuleProcessState {
        .init(guardianPID: process?.processIdentifier, workerPID: workerPID, running: process?.isRunning == true && input != nil,
              exitConfirmed: confirmed || process.map { !$0.isRunning && $0.terminationReason == .exit && [0, 64, 70, 71].contains($0.terminationStatus) } == true)
    }
    func launch(cancellation: ModuleCancellation, lifetime: ModuleCancellation, deadline: ContinuousClock.Instant) throws {
        guard process == nil else { throw DropSpaceError.invalid("ModuleAlreadyStarted") }
        try check(cancellation, lifetime, deadline)
        let child = Process(), incoming = Pipe(), outgoing = Pipe()
        child.executableURL = guardian
        child.arguments = [executable.path, "--module-session", session, "--data-version", String(dataVersion), "--data-directory", dataDirectory.path]
        child.standardInput = incoming; child.standardOutput = outgoing; child.standardError = FileHandle.nullDevice
        child.environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("DYLD_") }
        child.terminationHandler = { [onExit] _ in onExit() }
        try child.run(); process = child; confirmed = false
        input = incoming.fileHandleForWriting; output = outgoing.fileHandleForReading
        try incoming.fileHandleForReading.close(); try outgoing.fileHandleForWriting.close()
        for fd in [input!.fileDescriptor, output!.fileDescriptor] {
            guard fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK) == 0, fcntl(fd, F_SETNOSIGPIPE, 1) == 0 else { throw DropSpaceError.unavailable("ModulePipeUnavailable") }
        }
        let banner = try readLine(cancellation, lifetime, deadline, maximum: 128)
        guard let text = String(data: banner, encoding: .utf8), text.hasPrefix("DropSpaceModuleGuardian/1 "),
              let pid = Int32(text.dropFirst("DropSpaceModuleGuardian/1 ".count)), pid > 1 else { throw DropSpaceError.invalid("ModuleGuardianInvalid") }
        workerPID = pid
    }
    func exchange(_ method: String, payload: ModuleJSON, cancellation: ModuleCancellation, lifetime: ModuleCancellation, deadline: ContinuousClock.Instant) throws -> ModuleReply {
        guard nextID < Int64.max else { throw DropSpaceError.limit("ModuleRequestOverflow") }; nextID += 1
        let id = String(nextID)
        let bytes = try ModuleEnvelope(session: session, id: id, method: method, payload: payload).encoded() + Data([10])
        guard let fd = input?.fileDescriptor else { throw DropSpaceError.unavailable("ModuleNotRunning") }
        try bytes.withUnsafeBytes { buffer in
            var offset = 0
            while offset < bytes.count {
                try wait(fd, Int16(POLLOUT), cancellation, lifetime, deadline)
                let count = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), bytes.count - offset)
                if count > 0 { offset += count }
                else if count == 0 || errno != EAGAIN && errno != EINTR { throw DropSpaceError.unavailable("ModuleDisconnected") }
            }
        }
        let reply = try ModuleEnvelope.decode(readLine(cancellation, lifetime, deadline))
        return try reply.reply(expectedSession: session, expectedID: id)
    }
    private func check(_ cancellation: ModuleCancellation, _ lifetime: ModuleCancellation, _ deadline: ContinuousClock.Instant) throws {
        try cancellation.check(); try lifetime.check()
        guard ContinuousClock.now < deadline else { throw DropSpaceError.unavailable("ModuleTimeout") }
    }
    private func wait(_ fd: Int32, _ events: Int16, _ cancellation: ModuleCancellation, _ lifetime: ModuleCancellation, _ deadline: ContinuousClock.Instant) throws {
        try check(cancellation, lifetime, deadline)
        var descriptor = pollfd(fd: fd, events: events, revents: 0)
        if poll(&descriptor, 1, 25) < 0 && errno != EINTR { throw DropSpaceError.unavailable("ModulePipeUnavailable") }
        if descriptor.revents & Int16(POLLNVAL | POLLERR) != 0 { throw DropSpaceError.unavailable("ModuleDisconnected") }
    }
    private func readLine(_ cancellation: ModuleCancellation, _ lifetime: ModuleCancellation, _ deadline: ContinuousClock.Instant, maximum: Int = ModuleContract.maximumMessageBytes) throws -> Data {
        guard let fd = output?.fileDescriptor else { throw DropSpaceError.unavailable("ModuleDisconnected") }
        var buffer = [UInt8](repeating: 0, count: 4096)
        while true {
            try check(cancellation, lifetime, deadline)
            if let newline = pending.firstIndex(of: 10) {
                guard pending.distance(from: pending.startIndex, to: newline) <= maximum else { throw DropSpaceError.limit("ModuleMessageTooLarge") }
                let line = Data(pending[..<newline]); pending.removeSubrange(...newline); return line
            }
            guard pending.count <= maximum else { throw DropSpaceError.limit("ModuleMessageTooLarge") }
            try wait(fd, Int16(POLLIN), cancellation, lifetime, deadline)
            let count = Darwin.read(fd, &buffer, min(buffer.count, maximum + 1 - pending.count))
            if count > 0 { pending.append(contentsOf: buffer.prefix(count)) }
            else if count == 0 || errno != EAGAIN && errno != EINTR { throw DropSpaceError.unavailable("ModuleDisconnected") }
        }
    }
    func stop(graceful: Bool) -> Bool {
        guard let process else { return confirmed }
        if graceful, process.isRunning, input != nil {
            let deadline = ContinuousClock.now.advanced(by: .seconds(2))
            _ = try? exchange("stop", payload: .object([]), cancellation: ModuleCancellation(), lifetime: ModuleCancellation(), deadline: deadline)
            while process.isRunning && ContinuousClock.now < deadline { Thread.sleep(forTimeInterval: 0.01) }
        }
        try? input?.close(); input = nil
        // Never SIGKILL the guardian: it remains responsible for descendants if
        // the kernel cannot yet complete their exit. Retry keeps this handle.
        if process.isRunning { process.terminate() }
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while process.isRunning && ContinuousClock.now < deadline { Thread.sleep(forTimeInterval: 0.01) }
        // 64/70/71 are guardian pre-spawn failures: no worker was created.
        guard !process.isRunning, process.terminationReason == .exit, [0, 64, 70, 71].contains(process.terminationStatus) else { return false }
        confirmed = true; self.process = nil; workerPID = nil; try? output?.close(); output = nil; pending.removeAll()
        return true
    }
}
