import AppKit
import Carbon
import Observation
import DropSpaceCore

@MainActor @Observable public final class NativeMediaService: MediaSessionService {
    public private(set) var snapshot = MediaSnapshot() { didSet { onChange?(snapshot) } }
    public var onChange: ((MediaSnapshot) -> Void)?
    public private(set) var error: String?
    public private(set) var availablePlayers: [String] = []
    private var timer: Timer?
    private var task: Task<Void,Never>?
    private var controlTask: Task<Void, Error>?
    private let scripts = MediaScripts()
    private var settings = AppSettings()
    private var generation = 0
    private var suspended = false
    private var playbackClock = NativePlaybackClock()
    public init() {}
    public static let players = ["com.apple.Music": "Apple Music", "com.spotify.client": "Spotify"]
    public func start(settings: AppSettings) {
        if self.settings.islandActivity != settings.islandActivity {
            generation += 1; task?.cancel(); task = nil; controlTask?.cancel()
        }
        self.settings = settings
        if !MediaSourcePolicy.allows(snapshot.sourceID, settings: settings.islandActivity) {
            playbackClock.reset(); snapshot = .init()
        }
        guard !suspended else { return }
        guard settings.islandActivity.enableMediaActivity else { stop(); return }
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 1,repeats: true) { [weak self] _ in Task { @MainActor in self?.poll() } }
        poll()
    }
    public func stop() { generation += 1; timer?.invalidate(); timer = nil; task?.cancel(); task = nil; controlTask?.cancel(); playbackClock.reset(); error = nil; snapshot = .init() }
    /// Prevent readers/settings changes from restarting native work during retirement.
    public func suspend() { suspended = true; stop() }
    public func resume() { suspended = false; start(settings: settings) }
    public func drain() async {
        // Retain the control slot until its bounded native call has returned.
        // Cancellation cannot undo an already delivered player command.
        let operation = controlTask
        _ = try? await operation?.value
        await scripts.resetArtwork()
    }
    public func reconnect() async throws {
        guard suspended else { throw DropSpaceError.unavailable("Music must retire before reconnecting") }
        try Task.checkCancellation()
        await scripts.resetArtwork()
        try Task.checkCancellation()
        // Disabled media is a valid configured state, not a reason to enable it.
        guard settings.islandActivity.enableMediaActivity else { return }
        try await readPlayers(generation: generation)
    }
    public func connect(_ bundleID: String) async {
        guard !suspended, Self.players[bundleID] != nil else { return }
        let run = generation
        let result = await scripts.permission(bundleID,ask: true)
        guard generation == run, !suspended, !Task.isCancelled else { return }
        if result != noErr { error = "Automation permission was not granted (\(result)). Open Privacy & Security → Automation." }
        else { error = nil; poll() }
    }
    private func poll() {
        guard !suspended, settings.islandActivity.enableMediaActivity, task == nil else { return }
        let run = generation
        task = Task { [weak self] in
            guard let self else { return }; defer { if generation == run { task = nil } }
            do { try await readPlayers(generation: run) }
            catch is CancellationError { }
            catch { if generation == run { self.error = error.localizedDescription } }
        }
    }
    private func readPlayers(generation run: Int) async throws {
        try Task.checkCancellation()
        availablePlayers = NSWorkspace.shared.runningApplications.compactMap(\.bundleIdentifier).filter { Self.players[$0] != nil }.sorted()
        let players = availablePlayers.filter { MediaSourcePolicy.allows($0, settings: settings.islandActivity) }
        var candidates: [MediaSnapshot] = []
        var failures: [String] = []
        for player in players {
            let permission = await scripts.permission(player, ask: false)
            try Task.checkCancellation(); guard generation == run else { throw CancellationError() }
            guard permission == noErr else {
                failures.append("Automation access unavailable for \(Self.players[player] ?? player) (\(permission)).")
                continue
            }
            do { candidates.append(try await scripts.read(player)) }
            catch is CancellationError { throw CancellationError() }
            catch { failures.append(error.localizedDescription) }
        }
        try Task.checkCancellation(); guard generation == run else { throw CancellationError() }
        let current = candidates.first { $0.sourceID == snapshot.sourceID && $0.playing }
        let selected = current ?? candidates.first(where: \.playing) ?? candidates.first(where: { $0.sourceID == snapshot.sourceID }) ?? candidates.first ?? .init()
        var metadata = playbackClock.observe(selected)
        if settings.islandActivity.showArtwork, snapshot.trackID == selected.trackID { metadata.artwork = snapshot.artwork }
        snapshot = metadata // Optional artwork must never hold up current metadata.
        guard !candidates.isEmpty || failures.isEmpty else { throw DropSpaceError.unavailable(failures.joined(separator: "\n")) }
        error = nil
        let cover = await scripts.cover(selected, enabled: settings.islandActivity.showArtwork)
        try Task.checkCancellation(); guard generation == run, snapshot.trackID == selected.trackID else { throw CancellationError() }
        snapshot.artwork = cover
    }
    public func command(_ command: MediaCommand) async throws {
        try await self.command(command, expectedTrackIdentity: snapshot.trackID)
    }
    public func command(_ command: MediaCommand, expectedTrackIdentity: String) async throws {
        guard !suspended, expectedTrackIdentity == snapshot.trackID, settings.islandActivity.enableMediaActivity,
              MediaSourcePolicy.allows(snapshot.sourceID, settings: settings.islandActivity) else { throw DropSpaceError.unavailable("The media source changed before the command") }
        guard Self.players[snapshot.sourceID] != nil else { throw DropSpaceError.unavailable("No connected media source") }
        guard controlTask == nil else { throw DropSpaceError.unavailable("A playback command is still completing") }
        switch command {
        case .seek(let seconds): guard snapshot.canSeek, seconds.isFinite, seconds >= 0, seconds <= snapshot.duration else { throw DropSpaceError.invalid("Seeking unavailable") }
        case .playPause: guard snapshot.canPlayPause else { throw DropSpaceError.unavailable("Playback control unavailable") }
        case .next,.previous: guard snapshot.canSkip else { throw DropSpaceError.unavailable("Track control unavailable") }
        }
        let run = generation
        let expected = snapshot, scripts = scripts
        let operation = Task { try await scripts.command(expected, command) }
        controlTask = operation
        defer { controlTask = nil }
        try await withTaskCancellationHandler {
            try await operation.value
        } onCancel: { operation.cancel() }
        try Task.checkCancellation()
        guard generation == run, !suspended else { throw CancellationError() }
        // A completed command invalidates any earlier queued poll/cover response.
        // Even a subsecond explicit seek must not be mistaken for polling jitter.
        generation += 1; task?.cancel(); task = nil
        if case .seek = command { playbackClock.didSeek() }
        poll()
    }
}
private actor MediaScripts {
    private var artworkKey: String?
    private var artwork: Data?
    func resetArtwork() { artworkKey = nil; artwork = nil }
    func permission(_ id: String,ask: Bool) -> OSStatus {
        let descriptor = NSAppleEventDescriptor(bundleIdentifier: id)
        return AEDeterminePermissionToAutomateTarget(descriptor.aeDesc,typeWildCard,typeWildCard,ask)
    }
    func read(_ id: String) throws -> MediaSnapshot {
        try Task.checkCancellation()
        let script: String
        if id == "com.apple.Music" {
            script = """
            tell application id "com.apple.Music"
                if player state is stopped then return {"stopped", "", "", "", 0, 0, "", ""}
                set trackIdentity to ""
                set albumPerformer to ""
                try
                    set trackIdentity to persistent ID of current track
                    set albumPerformer to album artist of current track
                end try
                return {player state as text, name of current track, artist of current track, album of current track, duration of current track, player position, trackIdentity, albumPerformer}
            end tell
            """
        } else {
            script = "tell application id \"com.spotify.client\" to return {player state as text, name of current track, artist of current track, album of current track, (duration of current track) / 1000, player position, id of current track, \"\"}"
        }
        let result = try execute(script)
        var snapshot = MediaSnapshot()
        snapshot.sourceID = id; snapshot.sourceName = id == "com.apple.Music" ? "Apple Music" : "Spotify"
        snapshot.playing = result.atIndex(1)?.stringValue == "playing"
        snapshot.title = result.atIndex(2)?.stringValue ?? ""; snapshot.artist = result.atIndex(3)?.stringValue ?? ""
        snapshot.album = result.atIndex(4)?.stringValue ?? ""
        snapshot.sourceTrackID = result.atIndex(7)?.stringValue ?? ""; snapshot.albumArtist = result.atIndex(8)?.stringValue ?? ""
        snapshot.duration = result.atIndex(5)?.doubleValue ?? 0; snapshot.position = result.atIndex(6)?.doubleValue ?? 0
        snapshot.canPlayPause = !snapshot.title.isEmpty; snapshot.canSkip = snapshot.canPlayPause
        snapshot.canSeek = snapshot.duration.isFinite && snapshot.position.isFinite && snapshot.duration > 0 && snapshot.position >= 0
        return snapshot
    }
    func cover(_ snapshot: MediaSnapshot, enabled: Bool) -> Data? {
        guard enabled else { artworkKey = nil; artwork = nil; return nil }
        guard !Task.isCancelled, snapshot.sourceID == "com.apple.Music", !snapshot.title.isEmpty else { return nil }
        if artworkKey != snapshot.trackID { artworkKey = snapshot.trackID; artwork = nil }
        if let artwork { return artwork }
        // Missing artwork is retried on later polls. Guard both sides of the
        // optional read so a track transition cannot attach the next cover.
        let check = Self.trackGuard(snapshot)
        let source = """
        tell application id "com.apple.Music"
            \(check)
            set coverData to raw data of artwork 1 of current track
            \(check)
            return coverData
        end tell
        """
        if let bytes = try? execute(source, timeout: 1).data, !Task.isCancelled {
            artwork = NativeMediaArtwork.decode(bytes)
        }
        return artwork
    }
    func command(_ expected: MediaSnapshot, _ command: MediaCommand) throws {
        try Task.checkCancellation()
        let id = expected.sourceID
        guard ["com.apple.Music", "com.spotify.client"].contains(id) else { throw DropSpaceError.invalid("Unknown player") }
        let body: String
        switch command {
        case .playPause: body = "playpause"
        case .next: body = "next track"
        case .previous: body = "previous track"
        case .seek(let seconds):
            guard seconds.isFinite, seconds >= 0 else { throw DropSpaceError.invalid("Invalid seek position") }
            let duration = id == "com.apple.Music" ? "duration of current track" : "(duration of current track) / 1000"
            body = "if \(seconds) > (\(duration)) then error \"The track duration changed before seeking\"\nset player position to \(seconds)"
        }
        _ = try execute("tell application id \"\(id)\"\n\(Self.trackGuard(expected))\n\(body)\nend tell")
    }
    private static func trackGuard(_ expected: MediaSnapshot) -> String {
        func literal(_ value: String) -> String {
            "\"" + value.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
        }
        var checks = ["name of current track is not \(literal(expected.title))", "artist of current track is not \(literal(expected.artist))", "album of current track is not \(literal(expected.album))"]
        if !expected.sourceTrackID.isEmpty {
            let property = expected.sourceID == "com.apple.Music" ? "persistent ID" : "id"
            checks.append("\(property) of current track is not \(literal(expected.sourceTrackID))")
        }
        return checks.map { "if \($0) then error \"The media recording changed before the command\"" }.joined(separator: "\n")
    }
    private func execute(_ source: String, timeout: Int = 3) throws -> NSAppleEventDescriptor {
        guard let script = NSAppleScript(source: "with timeout of \(timeout) seconds\n" + source + "\nend timeout") else { throw DropSpaceError.invalid("Media command could not compile") }
        var error: NSDictionary?
        let output = script.executeAndReturnError(&error)
        if let error { throw DropSpaceError.unavailable(error[NSAppleScript.errorMessage] as? String ?? "Media command failed") }
        return output
    }
}
