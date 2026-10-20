import AppKit
import SwiftUI
import Observation
import DropSpaceCore
import DropSpacePlatform

@MainActor @Observable final class IslandController {
    weak var model: WorkspaceModel?
    private(set) var experience = IslandExperience()
    private(set) var motion = IslandMotion()
    private(set) var pages = [0.0, 0, 1, 0]
    var editingPosition = false
    var hovered = false
    let runtime = WidgetRuntime()
    var snapshot: IslandExperienceSnapshot { experience.current }
    var expanded: Bool { snapshot.state == .expanded }
    var page: IslandPage { snapshot.page }
    private var panel: IslandPanel?
    private(set) var glow: IslandGlow?
    private weak var previousKeyWindow: NSWindow?
    private var deadlineTask: Task<Void, Never>?
    private var rightHoldTask: Task<Void, Never>?
    private var rightOrigin: CGPoint?
    private var moveOrigin: CGRect?
    private var mouseMonitor: Any?
    private var observers: [NSObjectProtocol] = []
    private let drag = SmartDragObserver()
    private var selectedScreen: NSScreen?
    private var animationTimer: Timer?
    private var lastFrame = ContinuousClock.now
    private var stagedDrop = false
    private(set) var compactGeometry = IslandGeometry.compact(.idle)
    private(set) var compactScale = 1.0
    private(set) var expandedScale = 1.0
    init(model: WorkspaceModel) {
        self.model = model
        let panel = IslandPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "DropSpace Island"; panel.identifier = NSUserInterfaceItemIdentifier("dropspace-island")
        panel.isFloatingPanel = true; panel.hidesOnDeactivate = false; panel.backgroundColor = .clear; panel.isOpaque = false
        panel.hasShadow = true; panel.level = .floating; panel.isMovableByWindowBackground = false
        panel.contentView = NSHostingView(rootView: IslandView(controller: self, model: model)); self.panel = panel
        glow = IslandGlow(island: self, owner: panel)
        panel.onEscape = { [weak self] in if self?.editingPosition == true { self?.cancelPosition() } else { self?.dismiss() } }
        drag.onWake = { [weak self] point in
            guard let self else { return }
            selectedScreen = NSScreen.screens.first { $0.frame.contains(point) }
            experience.beginDrag(visible: panel.isVisible, now: Date()); apply()
        }
        drag.onEnd = { [weak self] in self?.experience.cancelDrag(now: Date()); self?.apply() }; drag.start()
        runtime.metrics.start(); runtime.onTimerCompleted = { [weak model] name in Task { await model?.activities.notify(title: name, body: "Time is up") } }
        for name in [NSApplication.didChangeScreenParametersNotification, NSWorkspace.didWakeNotification,
                     NSWorkspace.activeSpaceDidChangeNotification, NSWorkspace.didActivateApplicationNotification] {
            let center = name == NSApplication.didChangeScreenParametersNotification ? NotificationCenter.default : NSWorkspace.shared.notificationCenter
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.refresh() } })
        }
        mouseMonitor = NSEvent.addLocalMonitorForEvents(matching: [.rightMouseDown, .rightMouseUp, .rightMouseDragged]) { [weak self] event in
            guard let self, event.window === self.panel else { return event }
            return self.rightPointer(event) ? nil : event
        }
        refresh()
    }
    func refresh() {
        guard let model, let panel else { return }
        let now = Date(); drag.settings = model.settings
        let screen = display
        panel.appearance = model.settings.islandAppearance.theme == .system ? nil : NSAppearance(named: model.settings.islandAppearance.theme == .dark ? .darkAqua : .aqua)
        let fullscreen = screen.map { Self.foregroundCoversDisplay($0) } ?? false
        experience.updateSettings(model.settings.islandAppearance, priority: model.settings.islandContentPriority,
                                  fullscreenAllows: model.settings.islandAppearance.forceShowOverFullscreen || !fullscreen, now: now)
        experience.updateFiles(model.islandSpaceCount, now: now)
        let media = model.media.snapshot
        experience.updateMedia(available: !media.title.isEmpty, playing: media.playing, enabled: model.settings.islandActivity.enableMediaActivity, identity: media.trackID, now: now)
        experience.activities(notificationUntil: model.activities.notification?.expiresAt, volumeUntil: model.activities.volumeActivity?.expiresAt, now: now)
        if stagedDrop { stagedDrop = false; experience.completeDrop(model.islandSpaceCount, now: now) }
        if !model.settings.islandAppearance.rightClickHoldToMove { rightHoldTask?.cancel(); rightOrigin = nil }
        apply()
    }
    func showFromHotkey() {
        if NSApp.keyWindow !== panel { previousKeyWindow = NSApp.keyWindow }
        selectedScreen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) }
        experience.open(now: Date()); apply(); panel?.makeKey()
    }
    func open() { experience.open(now: Date()); apply() }
    func collapse() { experience.collapse(now: Date()); apply() }
    func dismiss() { experience.dismiss(now: Date()); apply() }
    func choosePage(_ value: IslandPage) { experience.select(value, now: Date()); apply() }
    func activityChanged() { refresh() }
    func hover(_ value: Bool) { hovered = value } // Windows hover feedback never opens the panel.
    func dragPresence(_ inside: Bool) {
        if inside && experience.fileState != .dragApproaching && experience.fileState != .dragReady && !experience.expandedDropActive { experience.beginDrag(visible: true, now: Date()) }
        experience.dragReady(inside, now: Date()); apply()
    }
    func dropAccepted() { stagedDrop = true; experience.cancelDrag(now: Date()); apply() }
    func beginPositionEdit() {
        editingPosition = true; deadlineTask?.cancel(); experience.open(now: Date()); apply()
        panel?.isMovableByWindowBackground = true
    }
    func savePosition() {
        guard let screen = panel?.screen, let frame = panel?.frame else { return }
        let key = screenKey(screen)
        model?.update { $0.overlayPlacements[key] = .init(custom: true, x: frame.midX - screen.frame.minX, y: screen.frame.maxY - frame.maxY) }
        editingPosition = false; panel?.isMovableByWindowBackground = false; moveOrigin = nil; rightOrigin = nil
        refresh()
    }
    func cancelPosition() { editingPosition = false; panel?.isMovableByWindowBackground = false; moveOrigin = nil; rightOrigin = nil; refresh() }
    func resetPosition() { guard let screen = panel?.screen ?? display else { return }; let key = screenKey(screen); model?.update { $0.overlayPlacements.removeValue(forKey: key) } }
    private func rightPointer(_ event: NSEvent) -> Bool {
        if event.type == .rightMouseDown, model?.settings.islandAppearance.rightClickHoldToMove == true, !editingPosition {
            rightOrigin = NSEvent.mouseLocation; rightHoldTask?.cancel()
            rightHoldTask = Task { [weak self] in
                do { try await Task.sleep(for: .milliseconds(400)) } catch { return }
                guard let self, !Task.isCancelled, NSEvent.pressedMouseButtons & 2 != 0, rightOrigin != nil else { return }
                beginPositionEdit(); moveOrigin = panel?.frame; rightOrigin = NSEvent.mouseLocation
            }
            return true
        }
        if event.type == .rightMouseUp { rightHoldTask?.cancel(); rightOrigin = nil; moveOrigin = nil; return editingPosition }
        if event.type == .rightMouseDragged, editingPosition, let origin = rightOrigin, let frame = moveOrigin {
            let point = NSEvent.mouseLocation
            panel?.setFrameOrigin(.init(x: frame.minX + point.x - origin.x, y: frame.minY + point.y - origin.y)); return true
        }
        return false
    }
    private var display: NSScreen? {
        guard let primary = NSScreen.screens.first else { return nil }
        return model?.settings.overlayMonitor == "primary" ? primary : selectedScreen.flatMap { old in NSScreen.screens.first { screenKey($0) == screenKey(old) } } ?? NSScreen.main ?? primary
    }
    private func screenKey(_ screen: NSScreen) -> String {
        guard let id = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID, let uuid = CGDisplayCreateUUIDFromDisplayID(id)?.takeRetainedValue() else { return screen.localizedName }
        return CFUUIDCreateString(nil, uuid) as String
    }
    private func apply() {
        guard let model, let panel, let screen = display else { return }
        let reduced = model.settings.overlayMotion == .reduced || model.settings.overlayMotion == .system && NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        compactGeometry = snapshot.variant == .music ? musicGeometry(model, screen: screen) : .compact(snapshot.variant)
        func fit(_ value: Double, _ geometry: IslandGeometry) -> Double {
            min(value, min(max(1, screen.visibleFrame.width - 36) / geometry.width, max(1, screen.visibleFrame.height - 34) / geometry.height))
        }
        compactScale = fit(model.settings.islandAppearance.compactScale, compactGeometry)
        expandedScale = fit(model.settings.islandAppearance.expandedScale, .files(.expanded))
        var geometry = snapshot.state == .compact ? compactGeometry : .files(snapshot.state)
        let scale = snapshot.state == .expanded ? expandedScale : snapshot.state == .compact ? compactScale : 1
        geometry.width *= scale; geometry.height *= scale; geometry.radius *= scale
        motion.target(geometry, state: snapshot.state, reduced: reduced)
        if reduced || snapshot.state != .expanded || motion.expanded < 0.01 { pages = IslandPage.allCases.map { $0 == page ? 1 : 0 } }
        panel.collectionBehavior = model.settings.islandAppearance.forceShowOverFullscreen || experience.manuallyOpen ? [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary] : [.canJoinAllSpaces, .stationary]
        panel.acceptsKeyboardFocus = snapshot.state == .expanded
        if !panel.acceptsKeyboardFocus, panel.isKeyWindow {
            panel.resignKey()
            if let previousKeyWindow, previousKeyWindow.isVisible { previousKeyWindow.makeKey() }
            else if let main = NSApp.mainWindow, main !== panel, main.isVisible { main.makeKey() }
        }
        if snapshot.state != .hidden { panel.orderFrontRegardless() }
        position()
        deadlineTask?.cancel()
        if !editingPosition, let deadline = snapshot.nextDeadline {
            let generation = experience.hideGeneration
            deadlineTask = Task { [weak self] in
                do { try await Task.sleep(for: .seconds(max(0, deadline.timeIntervalSinceNow))) } catch { return }
                guard let self, !Task.isCancelled else { return }; experience.deadline(generation: generation, expected: deadline, now: Date()); apply()
            }
        }
        if motion.animating || pages[page.rawValue] < 0.999 { startAnimation() }
        else { settle() }
    }
    private func startAnimation() {
        guard animationTimer == nil else { return }; lastFrame = .now
        let timer = Timer(timeInterval: 1.0 / 120, repeats: true) { [weak self] _ in MainActor.assumeIsolated { self?.frame() } }
        animationTimer = timer; RunLoop.main.add(timer, forMode: .common)
    }
    private func frame() {
        let now = ContinuousClock.now, duration = lastFrame.duration(to: now); lastFrame = now
        let seconds = Double(duration.components.seconds) + Double(duration.components.attoseconds) / 1e18
        motion.step(seconds: seconds)
        let blend = 1 - exp(-24 * min(seconds, 0.25))
        for index in pages.indices { pages[index] += ((index == page.rawValue ? 1 : 0) - pages[index]) * blend }
        if pages[page.rawValue] >= 0.999 { pages = IslandPage.allCases.map { $0 == page ? 1 : 0 } }
        position()
        if !motion.animating && pages[page.rawValue] == 1 { animationTimer?.invalidate(); animationTimer = nil; settle() }
    }
    private func settle() {
        if snapshot.state == .dismissing { experience.completeDismissal(generation: experience.hideGeneration, now: Date()) }
        if snapshot.state == .hidden { panel?.orderOut(nil) }
        glow?.refresh()
    }
    private func position() {
        guard let model, let panel, let screen = display else { return }
        defer { glow?.refresh() }
        panel.alphaValue = motion.opacity
        if editingPosition {
            // Movement belongs to the edit session, including a native left-button
            // window drag. Passive data refresh must never restore saved coordinates.
            let center = panel.frame.midX, top = panel.frame.maxY
            let size = CGSize(width: motion.width, height: motion.height)
            panel.setFrame(.init(x: center - size.width / 2, y: top - size.height, width: size.width, height: size.height), display: true)
            return
        }
        let size = CGSize(width: motion.width, height: motion.height), placement = model.settings.overlayPlacements[screenKey(screen)]
        let center = placement?.custom == true ? screen.frame.minX + placement!.x : screen.visibleFrame.midX
        let top = placement?.custom == true ? screen.frame.maxY - placement!.y : min(screen.visibleFrame.maxY, screen.frame.maxY - screen.safeAreaInsets.top) - 18
        let x = min(max(screen.visibleFrame.minX, center - size.width / 2), max(screen.visibleFrame.minX, screen.visibleFrame.maxX - size.width))
        let y = min(max(screen.visibleFrame.minY, top - size.height), max(screen.visibleFrame.minY, screen.visibleFrame.maxY - size.height))
        panel.setFrame(.init(origin: .init(x: x, y: y), size: size), display: true)
    }
    private func musicGeometry(_ model: WorkspaceModel, screen: NSScreen) -> IslandGeometry {
        let media = model.media.snapshot, settings = model.settings
        let showLyrics = settings.islandActivity.showLyricsInCompact && settings.lyrics.enabled
        let document = model.lyrics?.document
        let presentation = showLyrics && document?.match?.trackIdentity == media.trackID ?
            document.map { LyricsDisplay.presentation($0, position: media.position, delay: settings.lyrics.delayMilliseconds) } : nil
        let line = presentation?.lineIndex.flatMap { document?.lines[$0] }
        let text = LyricsDisplay.compact(showLyrics ? line?.text ?? media.title : media.title)
        let secondary = LyricsDisplay.compact(showLyrics ? line.flatMap {
            LyricsDisplay.secondary($0, target: settings.lyricsTranslationTarget.rawValue,
                enabled: settings.lyrics.secondaryLyrics, showAI: settings.lyrics.showAiLyricsLabel)
        } ?? "" : "")
        let font = NSFont.systemFont(ofSize: showLyrics ? settings.lyrics.fontSize : 13), smaller = NSFont.systemFont(ofSize: settings.lyrics.fontSize * 0.875)
        let primarySize = (text as NSString).size(withAttributes: [.font: font])
        let secondarySize = (secondary as NSString).size(withAttributes: [.font: smaller])
        let width = max(primarySize.width, secondarySize.width) + 4
        let available = max(40, screen.visibleFrame.width / settings.islandAppearance.compactScale - 36)
        let maximum = settings.lyrics.unlimitedScrollingWidth ? available : Double(settings.lyrics.scrollingMaxWidth)
        let textWidth = settings.islandActivity.compactDynamicWidth ? min(width, maximum) : 180
        let spectrumWidth = settings.islandActivity.showSpectrum && model.spectrum.hasPresentation ? 45.0 : 12.0
        let naturalWidth = min(available, presentation?.interlude == true ? 280 :
            max(180, 28 + ceil(textWidth * screen.backingScaleFactor) / screen.backingScaleFactor + (settings.islandActivity.showArtwork ? 36 : 12) + spectrumWidth))
        let height = presentation?.interlude == true ? 40 : max(40, min(340, max(28, primarySize.height) + (secondary.isEmpty ? 0 : secondarySize.height) + 12))
        return .init(width: naturalWidth, height: height, radius: min(naturalWidth, height) / 2)
    }
    private static func foregroundCoversDisplay(_ screen: NSScreen) -> Bool {
        guard let application = NSWorkspace.shared.frontmostApplication, application.processIdentifier != getpid(),
              let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]],
              let primary = NSScreen.screens.first else { return false }
        let target = CGRect(x: screen.frame.minX, y: primary.frame.maxY - screen.frame.maxY, width: screen.frame.width, height: screen.frame.height)
        return windows.contains { info in
            guard (info[kCGWindowOwnerPID as String] as? Int32) == application.processIdentifier, (info[kCGWindowLayer as String] as? Int) == 0,
                  let raw = info[kCGWindowBounds as String] as? [String: Any], let frame = CGRect(dictionaryRepresentation: raw as CFDictionary) else { return false }
            return abs(frame.minX - target.minX) < 2 && abs(frame.minY - target.minY) < 2 && frame.width >= target.width - 2 && frame.height >= target.height - 2
        }
    }
    func stop() {
        deadlineTask?.cancel(); rightHoldTask?.cancel(); animationTimer?.invalidate(); animationTimer = nil
        glow?.stop(); glow = nil
        drag.stop(); runtime.stopTimers(); runtime.metrics.stop(); panel?.close(); panel = nil
        if let mouseMonitor { NSEvent.removeMonitor(mouseMonitor) }; mouseMonitor = nil
        for observer in observers { NotificationCenter.default.removeObserver(observer); NSWorkspace.shared.notificationCenter.removeObserver(observer) }; observers.removeAll()
    }
}
final class IslandPanel: NSPanel {
    var onEscape: (() -> Void)?
    var acceptsKeyboardFocus = false
    override var canBecomeKey: Bool { acceptsKeyboardFocus }
    override var canBecomeMain: Bool { false }
    override func cancelOperation(_ sender: Any?) { onEscape?() }
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        // This hosting view is outside a SwiftUI Window scene. Give the app's
        // configured menu commands the same route while the Island owns focus.
        if NSApp.mainMenu?.performKeyEquivalent(with: event) == true { return true }
        return super.performKeyEquivalent(with: event)
    }
}
