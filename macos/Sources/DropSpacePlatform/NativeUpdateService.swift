import AppKit
import Observation
import DropSpaceCore
import DropSpaceInfrastructure

@MainActor @Observable public final class NativeUpdateService {
    public enum Phase: String { case idle, checking, available, downloading, ready, preparing, installing, current, cancelled, failed }
    public private(set) var phase: Phase = .idle
    public private(set) var release: MacUpdateManifest?
    public private(set) var message: String?
    public private(set) var received: Int64 = 0
    public private(set) var total: Int64?
    public private(set) var history: [UpdateInstallPlan] = []
    public var onChecked: ((Date) -> Void)?
    public var onInstall: (() -> Void)?
    public var isBusy: Bool { [.checking, .downloading, .preparing, .installing].contains(phase) }
    private let store: MacUpdateStore
    private var settings = AppSettings()
    private var job: Task<Void, Never>?
    private var generation = 0
    private var archive: URL?
    private var started = false

    public init(dataDirectory: URL, budget: DownloadBudget) {
        store = MacUpdateStore(dataDirectory: dataDirectory, budget: budget)
    }
    public func configure(_ settings: AppSettings) {
        if self.settings.updateChannel != settings.updateChannel {
            cancel(); release = nil; archive = nil; phase = .idle
        }
        self.settings = settings
        if !started {
            started = true
            Task { history = (try? await store.history()) ?? [] }
            if settings.autoCheckForUpdates { check() }
        }
    }
    public func check() {
        guard !isBusy else { return }
        let run = nextGeneration(); let channel = settings.updateChannel
        let previous = release; let previousArchive = archive
        phase = .checking; message = nil
        job = Task {
            defer { if generation == run { job = nil } }
            do {
                let candidate = try await store.check(channel: channel)
                try Task.checkCancellation()
                guard generation == run else { return }
                onChecked?(Date())
                guard let candidate, try MacVersion(candidate.tag) > MacVersion("macos-" + Baseline.version) else {
                    release = nil; archive = nil; phase = .current; return
                }
                release = candidate
                if previous == candidate, previousArchive != nil { archive = previousArchive; phase = .ready; return }
                archive = nil; phase = .available
                if settings.autoDownloadUpdates {
                    try await fetch(candidate, run: run)
                    if settings.autoInstallUpdates, generation == run { try await installReady(candidate, run: run) }
                }
            } catch {
                guard generation == run else { return }
                if case DropSpaceError.invalid = error {
                    release = nil; archive = nil; phase = .failed
                } else if previous != nil, previousArchive != nil { release = previous; archive = previousArchive; phase = .ready }
                else { phase = Task.isCancelled ? .cancelled : .failed }
                message = Task.isCancelled ? "Update check cancelled" : error.localizedDescription
            }
        }
    }
    public func download() {
        guard !isBusy, let release else { return }
        let run = nextGeneration()
        job = Task {
            defer { if generation == run { job = nil } }
            do { try await fetch(release, run: run) }
            catch { if generation == run { phase = Task.isCancelled ? .cancelled : .failed; message = error.localizedDescription } }
        }
    }
    public func install() {
        guard phase == .ready, let release else { return }
        let run = nextGeneration()
        job = Task {
            defer { if generation == run { job = nil } }
            do { try await installReady(release, run: run) }
            catch { if generation == run { phase = .ready; message = error.localizedDescription } }
        }
    }
    public func cancel() {
        guard phase != .installing else { return }
        generation += 1; job?.cancel(); job = nil
        if isBusy { phase = archive == nil ? .cancelled : .ready; message = "Update cancelled; completed partial bytes remain available for resume." }
    }
    public func shutdown() async {
        let pending = job
        if phase != .installing { cancel() }
        await pending?.value
    }
    private func nextGeneration() -> Int { generation += 1; return generation }
    private func fetch(_ release: MacUpdateManifest, run: Int) async throws {
        phase = .downloading; message = nil; received = 0; total = release.archive.size
        let result = try await store.download(release) { [weak self] received, total in
            await self?.progress(received, total, run: run)
        }
        try Task.checkCancellation()
        guard generation == run else { throw CancellationError() }
        archive = result; phase = .ready
    }
    private func progress(_ received: Int64, _ total: Int64?, run: Int) {
        guard generation == run else { return }; self.received = received; self.total = total
    }
    private func installReady(_ release: MacUpdateManifest, run: Int) async throws {
        guard let archive else { throw DropSpaceError.unavailable("Download this update before installing") }
        phase = .preparing; message = nil
        let (plan, url) = try await store.prepare(archive: archive, manifest: release, application: Bundle.main.bundleURL)
        var helperProcess: Process?
        do {
            try Task.checkCancellation()
            guard generation == run else { throw CancellationError() }
            let embeddedHelper = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/DropSpaceUpdateHelper")
            guard FileManager.default.isExecutableFile(atPath: embeddedHelper.path) else {
                throw DropSpaceError.unavailable("The update helper is missing. Reinstall using the official Mac DMG.")
            }
            // Run an intact copy outside the bundle that will be swapped. No elevation is requested.
            let helper = url.deletingLastPathComponent().appendingPathComponent("helper-\(plan.id.uuidString)")
            try FileManager.default.copyItem(at: embeddedHelper, to: helper)
            guard try Digests.sha256(file: helper) == Digests.sha256(file: embeddedHelper) else { throw DropSpaceError.invalid("Update helper copy changed") }
            let process = Process(); process.executableURL = helper; process.arguments = [url.path]
            process.standardInput = FileHandle.nullDevice; process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
            try process.run()
            helperProcess = process
            // Wait for the helper to validate and acknowledge ownership before quitting.
            let deadline = ContinuousClock.now.advanced(by: .seconds(10))
            while process.isRunning && ContinuousClock.now < deadline {
                if let bytes = try? Data(contentsOf: url), let saved = try? JSONDecoder().decode(UpdateInstallPlan.self, from: bytes), saved.phase == "waitingForExit" {
                    phase = .installing; onInstall?(); return
                }
                try await Task.sleep(for: .milliseconds(50))
            }
            throw DropSpaceError.unavailable("The update helper could not prepare installation. The current application is unchanged.")
        } catch {
            if let helperProcess { await ProcessRunner.stop(helperProcess) }
            try? FileManager.default.removeItem(at: plan.candidate)
            var failed = plan; failed.phase = "cancelled"; failed.message = error.localizedDescription; try? failed.save(to: url)
            throw error
        }
    }

    public static func acknowledgeLaunch(dataDirectory: URL) throws {
        let environment = ProcessInfo.processInfo.environment
        guard let value = environment["DROPSPACE_UPDATE_TOKEN"], let token = UUID(uuidString: value),
              let receiptPath = environment["DROPSPACE_UPDATE_RECEIPT"] else { return }
        let expected = dataDirectory.appendingPathComponent("Updates/\(token.uuidString).receipt")
        guard URL(fileURLWithPath: receiptPath) == expected else { throw DropSpaceError.invalid("Unexpected update receipt location") }
        try AtomicFile.writeNew(Data(value.utf8), to: expected)
    }
}
