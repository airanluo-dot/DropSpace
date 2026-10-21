import AppKit
import Observation
import DropSpaceCore

@MainActor @Observable public final class NativeSpectrumService {
    public enum State: Sendable { case stopped, sourceUnconfirmed, starting, capturing, waiting, failed }
    public private(set) var state: State = .stopped
    public private(set) var frame = SpectrumFrame()
    public private(set) var failure: String?
    public private(set) var droppedSamples: UInt64 = 0
    public private(set) var presentationTrack: String?
    public private(set) var trackIdentity = ""
    public var onPresentationChange: (() -> Void)?
    @ObservationIgnored private let capture = ProcessAudioTap()
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var generation = 0
    @ObservationIgnored private var target: AudioProcessIdentity?
    @ObservationIgnored private var media = MediaSnapshot()
    @ObservationIgnored private var settings = AppSettings()
    @ObservationIgnored private var closed = false
    @ObservationIgnored private var suspended = false
    @ObservationIgnored private var visibleOwners = Set<UUID>()
    @ObservationIgnored private var glowOwners = Set<UUID>()
    @ObservationIgnored private var sleeping = false
    @ObservationIgnored private var sleepObservers: [NSObjectProtocol] = []
    public init() {
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.didWakeNotification] {
            sleepObservers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { [weak self] notification in
                let sleeping = notification.name == NSWorkspace.willSleepNotification
                Task { @MainActor in
                    guard let self, !self.closed else { return }
                    self.sleeping = sleeping; self.configure(snapshot: self.media, settings: self.settings)
                }
            })
        }
    }
    public var hasPresentation: Bool {
        presentationTrack == media.trackID && !media.title.isEmpty && (frame.mode == .processLoopback || !media.playing)
    }
    public func configure(snapshot: MediaSnapshot, settings: AppSettings) {
        guard !closed else { return }
        self.media = snapshot; self.settings = settings
        guard !suspended else { return }
        let wanted = !sleeping && settings.islandActivity.enableMediaActivity &&
            (settings.islandActivity.showSpectrum && !visibleOwners.isEmpty || !glowOwners.isEmpty)
        let identity = wanted && snapshot.playing && !snapshot.title.isEmpty ? Self.identity(snapshot.sourceID) : nil
        let changedTrack = trackIdentity != snapshot.trackID
        if changedTrack { presentationTrack = nil; onPresentationChange?() }
        trackIdentity = snapshot.trackID
        let desiredState: State = wanted && snapshot.playing && identity == nil ? .sourceUnconfirmed : .stopped
        guard target != identity || (identity == nil && state != desiredState) else { return }
        target = identity
        replace(with: identity, idle: desiredState)
    }
    public func setPresentation(_ owner: UUID, visible: Bool) {
        let changed = visible ? visibleOwners.insert(owner).inserted : visibleOwners.remove(owner) != nil
        if changed {
            #if DEBUG
            if ProcessInfo.processInfo.environment["DROPSPACE_VALIDATION_SPECTRUM_TRACE"] == "1" { FileHandle.standardError.write(Data(("Spectrum presentation owners=\(visibleOwners.count)\n").utf8)) }
            #endif
            configure(snapshot: media, settings: settings)
        }
    }
    /// Exterior glow remains audio-driven even when the spectrum meter is hidden.
    public func setGlow(_ owner: UUID, eligible: Bool) {
        let changed = eligible ? glowOwners.insert(owner).inserted : glowOwners.remove(owner) != nil
        if changed { configure(snapshot: media, settings: settings) }
    }
    public func retry() {
        guard !closed, !suspended else { return }
        let wanted = settings.islandActivity.showSpectrum && !visibleOwners.isEmpty || !glowOwners.isEmpty
        let identity = !sleeping && settings.islandActivity.enableMediaActivity && wanted && media.playing ? Self.identity(media.sourceID) : nil
        target = identity
        replace(with: identity, idle: identity == nil && media.playing ? .sourceUnconfirmed : .stopped)
    }
    private func replace(with source: AudioProcessIdentity?, idle: State) {
        generation += 1; let run = generation
        let previous = task; previous?.cancel()
        frame = .init(); droppedSamples = 0; failure = nil; state = source == nil ? idle : .starting
        task = Task { [weak self] in
            await previous?.value
            guard let self, generation == run, !Task.isCancelled else { return }
            guard let source else { return }
            do {
                while !Task.isCancelled && generation == run {
                    guard Self.identity(source.bundleID) == source else { state = .sourceUnconfirmed; break }
                    state = .starting
                    do { try await capture.start(source) }
                    catch ProcessAudioError.sourceUnavailable {
                        state = .waiting
                        try await Task.sleep(for: .seconds(5))
                        continue
                    }
                    guard generation == run, !Task.isCancelled else { break }
                    let started = ContinuousClock.now
                    var lastIdentityCheck = started
                    while !Task.isCancelled && generation == run {
                        // Recheck launch identity as well as PID, avoiding PID reuse.
                        if Self.seconds(lastIdentityCheck.duration(to: .now)) >= 1 {
                            guard Self.identity(source.bundleID) == source else { throw ProcessAudioError.sourceUnavailable }
                            lastIdentityCheck = .now
                        }
                        let reading = try await capture.read()
                        guard generation == run, !Task.isCancelled else { break }
                        // A new PCM sequence number alone does not change the
                        // visible spectrum. Preserve native analysis revisions
                        // in the capture owner without invalidating SwiftUI for
                        // every identical silent frame.
                        if frame.mode != reading.frame.mode || frame.bands != reading.frame.bands { frame = reading.frame }
                        droppedSamples = reading.droppedSamples
                        if reading.frame.revision > 0, reading.sampleAge < 0.6 {
                            if presentationTrack != trackIdentity { presentationTrack = trackIdentity; onPresentationChange?() }
                            state = .capturing
                        }
                        else { state = .waiting }
                        let elapsed = Self.seconds(started.duration(to: .now))
                        if SpectrumFreshness.shouldRecover(wanted: true, mode: frame.mode, sampleAge: reading.sampleAge, sinceAttempt: elapsed) { break }
                        try await Task.sleep(for: .milliseconds(33))
                    }
                    try await capture.stop()
                }
            } catch is CancellationError {
                // Cleanup below is joined by the replacement or shutdown task.
            } catch {
                if generation == run { state = .failed; failure = Self.describe(error); frame = .init(mode: .unavailable) }
            }
            do { try await capture.stop() }
            catch { if generation == run { state = .failed; failure = Self.describe(error) } }
        }
    }
    public func suspendForReconnect() async throws {
        suspended = true; generation += 1; task?.cancel()
        await task?.value; task = nil
        try await capture.stop()
        target = nil; frame = .init(); state = .stopped; presentationTrack = nil; droppedSamples = 0
        onPresentationChange?()
    }
    public func resumeAfterReconnect(snapshot: MediaSnapshot, settings: AppSettings) {
        suspended = false; configure(snapshot: snapshot, settings: settings)
    }
    public func shutdown() async -> Bool {
        closed = true; generation += 1; task?.cancel()
        sleepObservers.forEach(NSWorkspace.shared.notificationCenter.removeObserver); sleepObservers = []
        await task?.value; task = nil; frame = .init(); state = .stopped; target = nil
        do { try await capture.stop(); return true }
        catch { state = .failed; failure = Self.describe(error); return false }
    }
    private static func identity(_ bundle: String) -> AudioProcessIdentity? {
        guard NativeMediaService.players[bundle] != nil else { return nil }
        let matches = NSRunningApplication.runningApplications(withBundleIdentifier: bundle).filter { !$0.isTerminated }
        guard matches.count == 1, let app = matches.first, let date = app.launchDate else { return nil }
        return .init(pid: app.processIdentifier, bundleID: bundle, launchedAt: date)
    }
    private static func seconds(_ value: Duration) -> Double {
        Double(value.components.seconds) + Double(value.components.attoseconds) / 1e18
    }
    private static func describe(_ error: any Error) -> String {
        switch error {
        case ProcessAudioError.sourceUnavailable: "source-unavailable"
        case ProcessAudioError.unsupportedFormat: "unsupported-format"
        case ProcessAudioError.system(let operation, let code): "\(operation) (\(code))"
        default: error.localizedDescription
        }
    }
}
