import AppKit
import DropSpaceCore

@MainActor public final class SmartDragObserver {
    public var onWake: ((CGPoint) -> Void)?
    public var onEnd: (() -> Void)?
    public var settings = AppSettings()
    private var timer: Timer?
    private var wasPressed = false
    private var origin = CGPoint.zero
    private var initialCount = NSPasteboard(name: .drag).changeCount
    private var started = Date.distantPast
    private var fired = false
    private var sawFreshPayload = false
    public init() {}
    public func start() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 0.05,repeats: true) { [weak self] _ in Task { @MainActor in self?.poll() } }
    }
    public func stop() { timer?.invalidate(); timer = nil; wasPressed = false; sawFreshPayload = false }
    private func poll() {
        let pressed = NSEvent.pressedMouseButtons & 1 != 0
        let board = NSPasteboard(name: .drag)
        let point = NSEvent.mouseLocation
        defer { wasPressed = pressed }
        guard settings.fileDragWakeMode != .disabled else { initialCount = board.changeCount; return }
        if !pressed { if wasPressed && fired { onEnd?() }; initialCount = board.changeCount; fired = false; sawFreshPayload = false; return }
        if !wasPressed { origin = point; started = Date(); fired = false; sawFreshPayload = board.changeCount != initialCount }
        if board.changeCount != initialCount { sawFreshPayload = true }
        guard !fired, sawFreshPayload, Date().timeIntervalSince(started) < 15, hypot(point.x-origin.x,point.y-origin.y) >= 6 else { return }
        let source = NSWorkspace.shared.frontmostApplication
        guard !settings.smartDragExcludedProcesses.contains(where: { $0.caseInsensitiveCompare(source?.bundleIdentifier ?? "") == .orderedSame || $0.caseInsensitiveCompare(source?.localizedName ?? "") == .orderedSame }) else { return }
        let types: [NSPasteboard.PasteboardType] = [.fileURL,.URL,.string] + NSFilePromiseReceiver.readableDraggedTypes.map { NSPasteboard.PasteboardType($0) }
        guard board.availableType(from: types) != nil else { return }
        if settings.fileDragWakeMode == .classicTopEdge {
            guard NSScreen.screens.contains(where: { $0.frame.contains(point) && point.y >= $0.frame.maxY - 8 }) else { return }
        }
        fired = true; onWake?(point)
    }
}
