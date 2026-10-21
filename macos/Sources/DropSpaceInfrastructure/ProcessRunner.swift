import Foundation
import DropSpaceCore

public enum ProcessRunner {
    /// Confirm helper exit before releasing its files. Never block the UI executor.
    public static func stop(_ process: Process) async {
        await Task.detached(priority: .utility) {
            if process.isRunning {
                process.terminate()
                let deadline = ContinuousClock.now.advanced(by: .seconds(2))
                while process.isRunning && ContinuousClock.now < deadline { try? await Task.sleep(for: .milliseconds(20)) }
                if process.isRunning { kill(process.processIdentifier, SIGKILL) }
            }
            process.waitUntilExit()
        }.value
    }
    public static func run(executable: URL,arguments: [String],timeout: TimeInterval = 60) async throws {
        let owner = ProcessOwner()
        try await withTaskCancellationHandler {
            try await Task.detached(priority: .utility) { try owner.execute(executable,arguments,timeout: timeout) }.value
        } onCancel: { owner.cancel() }
    }
}
private final class ProcessOwner: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?
    private var cancelled = false
    func execute(_ executable: URL,_ arguments: [String],timeout: TimeInterval) throws {
        let process = Process(); process.executableURL = executable; process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
        lock.lock()
        if cancelled { lock.unlock(); throw CancellationError() }
        self.process = process
        do { try process.run() } catch { self.process = nil; lock.unlock(); throw error }
        lock.unlock()
        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline {
            lock.lock(); let shouldStop = cancelled; lock.unlock()
            if shouldStop { break }
            Thread.sleep(forTimeInterval: 0.05)
        }
        if process.isRunning {
            process.terminate()
            let grace = Date().addingTimeInterval(2)
            while process.isRunning && Date() < grace { Thread.sleep(forTimeInterval: 0.02) }
            if process.isRunning { kill(process.processIdentifier,SIGKILL) }
        }
        process.waitUntilExit()
        lock.lock(); self.process = nil; let wasCancelled = cancelled; lock.unlock()
        if wasCancelled { throw CancellationError() }
        guard process.terminationReason == .exit, process.terminationStatus == 0 else { throw DropSpaceError.unavailable("Native helper failed (\(process.terminationStatus))") }
    }
    func cancel() { lock.lock(); cancelled = true; lock.unlock() }
}
