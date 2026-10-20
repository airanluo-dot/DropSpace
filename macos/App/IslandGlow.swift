import AppKit
import SwiftUI
import Observation
import DropSpaceCore

@MainActor @Observable private final class GlowPresentation {
    var value = LyricsGlowVisualState()
    var size = CGSize.zero
    var radius = 0.0
}

private struct GlowSurface: View {
    let presentation: GlowPresentation
    var body: some View {
        Rectangle().fill(.white).colorEffect(ShaderLibrary.islandGlow(
            .float2(Float(presentation.size.width), Float(presentation.size.height)),
            .float(Float(presentation.radius)), .float(Float(presentation.value.phase)),
            .float(Float(presentation.value.brightness)), .float(Float(presentation.value.simplification)),
            .floatArray(presentation.value.bands.map(Float.init))
        )).accessibilityHidden(true).allowsHitTesting(false)
    }
}

/// One native, click-through child panel and monotonic envelope per Island.
/// It never changes the Island's hit region, focus, saved placement or contour.
@MainActor final class IslandGlow {
    private weak var island: IslandController?
    private weak var owner: NSPanel?
    private let panel: GlowPanel
    private let presentation = GlowPresentation()
    private var envelope = LyricsGlowEnvelope()
    private var audio = LyricsGlowAudioResponse()
    private var timer: Timer?
    private var previous = ContinuousClock.now
    private var track = ""
    private var screen: Int?
    private var eligible = false
    private var reduced = false
    private var closed = false
    private let audioOwner = UUID()
    private var observers: [NSObjectProtocol] = []
    private var markers: [UUID: WeakMarker] = [:]
    private var lastTraceSecond = -1
    private struct WeakMarker { weak var value: GlowTranslationView? }

    init(island: IslandController, owner: NSPanel) {
        self.island = island; self.owner = owner
        panel = GlowPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "DropSpace Island Glow"; panel.identifier = .init("dropspace-island-glow")
        panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = false
        panel.ignoresMouseEvents = true; panel.hidesOnDeactivate = false; panel.isFloatingPanel = true
        panel.isExcludedFromWindowsMenu = true; panel.setAccessibilityHidden(true)
        panel.level = owner.level
        panel.contentView = NSHostingView(rootView: GlowSurface(presentation: presentation))
        for name in [NSWindow.didMoveNotification, NSWindow.didResizeNotification, NSWindow.didChangeOcclusionStateNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: owner, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.refresh() }
            })
        }
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.refresh() }
            })
    }
    func register(_ marker: GlowTranslationView) { markers[marker.identity] = WeakMarker(value: marker); refresh() }
    func unregister(_ marker: GlowTranslationView) { markers.removeValue(forKey: marker.identity); refresh() }

    func refresh() {
        guard !closed, let island, let owner, let model = island.model else { return }
        let visible = owner.isVisible && owner.occlusionState.contains(.visible) && !owner.isMiniaturized
        let allowed = visible && !island.editingPosition && island.snapshot.variant != .idle &&
            !NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast &&
            !NSWorkspace.shared.accessibilityDisplayShouldReduceTransparency
        guard allowed else { hideImmediately(); return }
        let currentScreen = (owner.screen?.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.intValue
        // AppKit can temporarily report no screen while a panel is reordered or
        // resized. That is not a monitor change and must not restart the phase.
        if let currentScreen, screen != currentScreen {
            hideImmediately(); envelope = .init(); screen = currentScreen
        }
        let media = model.media.snapshot
        if track != media.trackID { track = media.trackID; audio.reset() }
        let lyrics = model.settings.lyrics
        let surface = (island.snapshot.state == .compact || island.snapshot.state == .expanded) && island.motion.opacity > 0.01
        let translation = markers.values.compactMap(\.value).first { $0.isCurrentVisible(in: island) }
        eligible = LyricsGlowPolicy.eligible(mode: lyrics.glowMode, playing: media.playing && !media.title.isEmpty,
            islandVisible: surface, translationVisible: translation != nil,
            origin: translation?.origin ?? .none, translation: translation?.text)
        reduced = model.settings.overlayMotion == .reduced ||
            model.settings.overlayMotion == .system && NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        model.spectrum.setGlow(audioOwner, eligible: eligible)
        geometry()
        if eligible || envelope.state.brightness > 0 { start() }
    }
    private func start() {
        guard timer == nil else { return }
        previous = .now
        let value = Timer(timeInterval: 0.033, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.advance() }
        }
        timer = value; RunLoop.main.add(value, forMode: .common)
    }
    private func advance() {
        refresh()
        guard timer != nil, let model = island?.model else { return }
        let now = ContinuousClock.now, duration = previous.duration(to: now); previous = now
        let seconds = Double(duration.components.seconds) + Double(duration.components.attoseconds) / 1e18
        let spectrum = model.spectrum.frame
        if eligible && !reduced {
            audio.advance(energy: LyricsGlowPolicy.energy(spectrum),
                          bands: spectrum.mode == .processLoopback ? spectrum.bands : [], seconds: seconds)
        } else { audio.reset() }
        envelope.advance(eligible: eligible, energy: audio.energy, seconds: seconds, reducedMotion: reduced,
                         bands: audio.bands, simplified: model.settings.lyrics.simplifiedGlow)
        var value = envelope.state
        // Same sub-byte presentation coalescing as the baseline rasterizer.
        value.brightness = (value.brightness * 1024).rounded(.toNearestOrEven) / 1024
        value.simplification = (value.simplification * 1024).rounded(.toNearestOrEven) / 1024
        if value != presentation.value { presentation.value = value }
        if value.brightness > 0, let owner, owner.isVisible {
            if panel.parent == nil { owner.addChildWindow(panel, ordered: .below) }
            if !panel.isVisible { panel.order(.below, relativeTo: owner.windowNumber) }
        } else { panel.orderOut(nil) }
        #if DEBUG
        if ProcessInfo.processInfo.environment["DROPSPACE_VALIDATION_GLOW_TRACE"] == "1" {
            let second = Int(ProcessInfo.processInfo.systemUptime)
            if second != lastTraceSecond {
                lastTraceSecond = second
                FileHandle.standardError.write(Data(("Glow eligible=\(eligible) visible=\(panel.isVisible) brightness=\(value.brightness) phase=\(value.phase) simplified=\(value.simplification) energy=\(audio.energy)\n").utf8))
            }
        }
        #endif
        if !eligible && envelope.state.brightness == 0 { timer?.invalidate(); timer = nil }
    }
    private func geometry() {
        guard let owner, let island else { return }
        let frame = owner.frame.insetBy(dx: -18, dy: -18)
        if panel.frame != frame { panel.setFrame(frame, display: false) }
        if presentation.size != owner.frame.size { presentation.size = owner.frame.size }
        if presentation.radius != island.motion.radius { presentation.radius = island.motion.radius }
        panel.alphaValue = owner.alphaValue
        panel.collectionBehavior = owner.collectionBehavior
    }
    private func hideImmediately() {
        eligible = false; island?.model?.spectrum.setGlow(audioOwner, eligible: false)
        timer?.invalidate(); timer = nil; audio.reset()
        envelope.advance(eligible: false, energy: 0, seconds: 5)
        panel.orderOut(nil)
        if let parent = panel.parent { parent.removeChildWindow(panel) }
    }
    func stop() {
        guard !closed else { return }
        hideImmediately(); closed = true
        observers.forEach { NotificationCenter.default.removeObserver($0); NSWorkspace.shared.notificationCenter.removeObserver($0) }
        observers = []; markers = [:]; panel.close()
    }
}

private final class GlowPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// The secondary text supplies actual view bounds, not just document existence.
struct GlowTranslationPresence: NSViewRepresentable {
    let model: WorkspaceModel
    let line: LyricLine
    let compact: Bool
    var active: Bool
    func makeNSView(context: Context) -> GlowTranslationView { GlowTranslationView() }
    func updateNSView(_ view: GlowTranslationView, context: Context) {
        view.track = model.media.snapshot.trackID; view.start = line.start; view.text = line.secondary ?? ""
        view.origin = line.translationOrigin; view.compact = compact; view.active = active
        Task { @MainActor [weak view, weak model] in
            guard let view else { return }
            if view.glow !== model?.island?.glow { view.glow?.unregister(view); view.glow = model?.island?.glow }
            view.glow?.register(view)
        }
    }
    static func dismantleNSView(_ view: GlowTranslationView, coordinator: ()) { view.active = false; view.glow?.unregister(view); view.glow = nil }
}

final class GlowTranslationView: NSView {
    let identity = UUID()
    weak var glow: IslandGlow?
    var track = "", text = ""
    var start = 0.0
    var origin = LyricsTranslationOrigin.none
    var compact = false, active = false
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); glow?.refresh() }
    func isCurrentVisible(in island: IslandController) -> Bool {
        guard active, let model = island.model, model.settings.lyrics.enabled, model.settings.lyrics.secondaryLyrics,
              track == model.media.snapshot.trackID, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              window?.identifier?.rawValue == "dropspace-island", window?.occlusionState.contains(.visible) == true,
              !isHiddenOrHasHiddenAncestor, alphaValue > 0.01, !visibleRect.isEmpty, let content = window?.contentView,
              convert(visibleRect, to: content).intersects(content.bounds) else { return false }
        if compact {
            guard island.snapshot.content == .music, model.settings.islandActivity.showLyricsInCompact, island.motion.compact > 0.01 else { return false }
        } else {
            guard island.motion.expanded > 0.01, island.pages[IslandPage.music.rawValue] > 0.01 else { return false }
        }
        guard let document = model.lyrics?.document, document.match?.trackIdentity == track else { return false }
        let media = model.media.snapshot
        let presentation = LyricsDisplay.presentation(document, position: media.interpolatedPosition(), delay: model.settings.lyrics.delayMilliseconds)
        guard let index = presentation.lineIndex, !compact || !presentation.interlude else { return false }
        let current = document.lines[index]
        return current.start == start && current.secondary == text && current.translationOrigin == origin &&
            LyricsDisplay.secondary(current, target: model.settings.lyricsTranslationTarget.rawValue,
                                    enabled: true, showAI: false) != nil
    }
}
