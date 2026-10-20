import Foundation

public enum IslandState: Sendable { case hidden, dragApproaching, dragReady, compact, expanded, dismissing }
public enum IslandPage: Int, CaseIterable, Sendable { case widgets, music, files, clipboard }
public enum IslandContent: Sendable { case none, files, music, notification, volume }
public enum IslandVariant: Sendable { case idle, emptyWake, singleFile, multipleFiles, music, activity }
public struct IslandExperienceSnapshot: Equatable, Sendable {
    public var state: IslandState = .hidden
    public var content: IslandContent = .none
    public var page: IslandPage = .files
    public var variant: IslandVariant = .idle
    public var mediaPresent = false
    public var nextDeadline: Date?
    public var pendingHide = false
    public var revision: Int64 = 0
}

/// Port of frozen IslandExperienceCoordinator / IslandPresencePolicy. All time
/// comes from the caller; a media refresh cannot restart an existing hide clock.
public struct IslandExperience: Sendable {
    public private(set) var current = IslandExperienceSnapshot()
    public private(set) var hideGeneration: Int64 = 0
    public private(set) var fileCount = 0
    public private(set) var expandedDropActive = false
    public private(set) var fileState: IslandState = .hidden
    private var mediaAvailable = false, mediaPlaying = false, manual = false, expanded = false
    private var resident = false, allows = true, dismissed = false, hideCompleted = false
    private var mediaIdentity: String?
    private var page: IslandPage = .files
    private var selected = false
    private var priority = "music"
    private var notification: Date?, volume: Date?, hideStarted: Date?
    private var delay = 3000
    public init() {}
    public var manuallyOpen: Bool { manual && expanded }
    public mutating func updateSettings(_ appearance: IslandAppearanceSettings, priority: String, fullscreenAllows: Bool, now: Date) {
        resident = appearance.resident; delay = min(10_000, max(0, Int((Double(appearance.hideDelayMilliseconds) / 1000).rounded()) * 1000))
        allows = fullscreenAllows
        if self.priority != priority { self.priority = priority; selected = false }
        reconcile(now)
    }
    public mutating func updateMedia(available: Bool, playing: Bool, enabled: Bool, identity: String?, now: Date) {
        let available = enabled && available, active = enabled && available && playing
        if available && identity != mediaIdentity || active && !mediaPlaying { dismissed = false }
        if mediaAvailable && !available && page == .music { selected = false }
        mediaIdentity = available ? identity : nil; mediaAvailable = available; mediaPlaying = active; reconcile(now)
    }
    public mutating func updateFiles(_ count: Int, now: Date) {
        precondition(count >= 0)
        if count > fileCount { dismissed = false }
        if fileCount > 0 && count == 0 && page == .files { selected = false }
        fileCount = count
        if count == 0 { expandedDropActive = false; if fileState == .compact { fileState = .dismissing } }
        else if fileState == .hidden || fileState == .dismissing { fileState = .compact }
        reconcile(now)
    }
    public mutating func beginDrag(visible: Bool, now: Date) {
        dismissed = false
        if visible && current.state == .expanded { fileState = .expanded; expandedDropActive = true }
        else { fileState = .dragApproaching; expandedDropActive = false }
        reconcile(now)
    }
    public mutating func dragReady(_ ready: Bool, now: Date) {
        if !expandedDropActive && (fileState == .dragApproaching || fileState == .dragReady) { fileState = ready ? .dragReady : .dragApproaching }
        reconcile(now)
    }
    public mutating func cancelDrag(now: Date) {
        if expandedDropActive { expandedDropActive = false }
        else if fileState == .dragApproaching || fileState == .dragReady { fileState = fileCount == 0 ? .dismissing : .compact }
        reconcile(now)
    }
    public mutating func completeDrop(_ count: Int, now: Date) {
        let remainExpanded = expandedDropActive
        updateFiles(count, now: now); expandedDropActive = false
        fileState = count == 0 ? .dismissing : remainExpanded ? .expanded : .compact
        dismissed = false; reconcile(now)
    }
    public mutating func open(_ page: IslandPage? = nil, now: Date) {
        hideGeneration += 1; hideStarted = nil
        if let page { self.page = page; selected = true }
        dismissed = false; manual = true; expanded = true; fileState = .expanded; expandedDropActive = false; reconcile(now)
    }
    public mutating func select(_ page: IslandPage, now: Date) { self.page = page; selected = true; reconcile(now) }
    public mutating func collapse(now: Date) { manual = false; expanded = false; expandedDropActive = false; fileState = fileCount == 0 ? .dismissing : .compact; reconcile(now) }
    public mutating func dismiss(now: Date) {
        dismissed = true; manual = false; expanded = false; hideStarted = nil; hideCompleted = false; hideGeneration += 1; reconcile(now)
    }
    public mutating func completeDismissal(generation: Int64, now: Date) {
        guard generation == hideGeneration, current.state == .dismissing else { return }
        hideCompleted = true; reconcile(now)
    }
    public mutating func activities(notificationUntil: Date?, volumeUntil: Date?, now: Date) {
        if notificationUntil != notification && notificationUntil.map({ $0 > now }) == true || volumeUntil != volume && volumeUntil.map({ $0 > now }) == true { dismissed = false }
        notification = notificationUntil; volume = volumeUntil; reconcile(now)
    }
    public mutating func deadline(generation: Int64, expected: Date, now: Date) {
        guard generation == hideGeneration, current.nextDeadline == expected else { return }; reconcile(now)
    }
    private mutating func reconcile(_ now: Date) {
        let files = fileCount > 0
        let defaultPage: IslandPage = files && (!mediaAvailable || !mediaPlaying || priority == "space") ? .files : mediaAvailable ? .music : .files
        if !selected { page = defaultPage }
        let contentPage = selected && (page == .music && mediaAvailable || page == .files && files) ? page : defaultPage
        let hasNotification = notification.map { $0 > now } == true, hasVolume = volume.map { $0 > now } == true
        let dragging = fileState == .dragApproaching || fileState == .dragReady || expandedDropActive
        var content: IslandContent = dragging ? .files : hasNotification ? .notification : hasVolume ? .volume :
            files && contentPage == .files ? .files : mediaAvailable ? .music : files || manual ? .files : .none
        var state: IslandState = dragging ? fileState : content == .none ? .hidden : !hasNotification && !hasVolume && (expanded || manual) ? .expanded : .compact
        var variant: IslandVariant
        switch content {
        case .music: variant = .music
        case .notification, .volume: variant = .activity
        case .files: variant = fileCount == 1 ? .singleFile : fileCount > 1 ? .multipleFiles : .emptyWake
        case .none: variant = .idle
        }
        let desired = (resident || mediaPlaying || manuallyOpen || files || dragging || hasNotification || hasVolume) && (allows || manuallyOpen) && !dismissed
        if desired {
            if hideStarted != nil { hideStarted = nil; hideGeneration += 1 }; hideCompleted = false
            if state == .hidden { state = .compact }
        } else {
            if hideCompleted { state = .hidden }
            else if dismissed { state = current.state == .hidden ? .hidden : .dismissing }
            else if hideStarted != nil || current.state != .hidden && current.state != .dismissing {
                if hideStarted == nil { hideStarted = now; hideGeneration += 1 }
                state = current.state == .dismissing || now >= hideStarted!.addingTimeInterval(Double(delay) / 1000) ? .dismissing : .compact
            } else { state = .hidden }
            if state == .hidden { content = .none; variant = .idle }
        }
        let hideDeadline = !desired && !dismissed && state != .dismissing && !hideCompleted ? hideStarted?.addingTimeInterval(Double(delay) / 1000) : nil
        let next = [hideDeadline, notification, volume].compactMap { $0 }.filter { $0 > now }.min()
        var value = IslandExperienceSnapshot(state: state, content: content, page: dragging ? .files : page, variant: variant,
            mediaPresent: state != .hidden && mediaAvailable, nextDeadline: next, pendingHide: hideDeadline.map { $0 > now } == true, revision: current.revision)
        if value != current { value.revision += 1; current = value }
    }
}

public struct IslandGeometry: Equatable, Sendable {
    public var width: Double, height: Double, radius: Double
    public init(width: Double, height: Double, radius: Double) { self.width = width; self.height = height; self.radius = radius }
    public static func files(_ state: IslandState) -> Self {
        switch state {
        case .dragApproaching: .init(width: 300, height: 54, radius: 27)
        case .dragReady: .init(width: 430, height: 92, radius: 30)
        case .compact: .init(width: 340, height: 64, radius: 32)
        case .expanded: .init(width: 560, height: 340, radius: 28)
        case .hidden, .dismissing: .init(width: 120, height: 12, radius: 6)
        }
    }
    public static func compact(_ variant: IslandVariant) -> Self {
        switch variant {
        case .singleFile: .init(width: 340, height: 64, radius: 32)
        case .multipleFiles: .init(width: 200, height: 64, radius: 32)
        case .emptyWake: .init(width: 88, height: 40, radius: 20)
        case .activity: .init(width: 400, height: 80, radius: 40)
        case .idle, .music: .init(width: 180, height: 40, radius: 20)
        }
    }
}
