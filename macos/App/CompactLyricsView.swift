import AppKit
import SwiftUI
import DropSpaceCore

/// Two independent, clipped native text rows; playback time is the only marquee clock.
struct CompactLyricsView: View {
    let model: WorkspaceModel
    // TimelineView may remount its representable when the schedule pauses.
    // Motion belongs to the lyric presentation, not to that temporary NSView.
    @State private var renderState = CompactLyricRenderState()
    var body: some View {
        let active = model.island?.snapshot.state == .compact && model.island?.snapshot.content == .music
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !model.media.snapshot.playing || !active)) { context in
            let media = model.media.snapshot
            let position = media.interpolatedPosition()
            let candidate = model.lyrics?.document
            let document = candidate?.match?.trackIdentity == media.trackID ? candidate! : .init(lines: [], provider: .localLrc)
            let show = model.settings.lyrics.enabled && model.settings.islandActivity.showLyricsInCompact
            let display = show ? LyricsDisplay.presentation(document, position: position, delay: model.settings.lyrics.delayMilliseconds) :
                LyricsDisplay.Presentation(lineIndex: nil, waiting: false, interlude: false)
            let line = display.lineIndex.map { document.lines[$0] }
            let secondary = display.interlude ? "" : line.flatMap { LyricsDisplay.secondary($0, target: model.settings.lyricsTranslationTarget.rawValue,
                enabled: show && model.settings.lyrics.secondaryLyrics, showAI: model.settings.lyrics.showAiLyricsLabel) } ?? ""
            let size = show ? model.settings.lyrics.fontSize : 13
            let primary = display.interlude ? "" : LyricsDisplay.compact(line?.text ?? media.title)
            let primaryHeight = max(28, CompactLyricTextView.measure(primary, font: NSFont.systemFont(ofSize: size)).height)
            let secondaryHeight = secondary.isEmpty ? 0 : CompactLyricTextView.measure(LyricsDisplay.compact(secondary),
                font: NSFont.systemFont(ofSize: model.settings.lyrics.fontSize * 0.875)).height
            CompactLyricText(model: model, state: renderState, input: .init(track: media.trackID, position: position,
                marqueePosition: media.interpolatedMarqueePosition(), playing: media.playing,
                timelineRevision: media.timelineRevision, scrollMode: model.settings.lyrics.translationScrollMode,
                line: line, frame: LyricsTimeline.frame(document, position: position, delayMilliseconds: model.settings.lyrics.delayMilliseconds),
                waiting: display.waiting, interlude: display.interlude, primary: primary, secondary: LyricsDisplay.compact(secondary),
                fontSize: size, secondarySize: model.settings.lyrics.fontSize * 0.875,
                delay: model.settings.lyrics.delayMilliseconds, wordSynced: show && model.settings.lyrics.wordSyncedHighlighting,
                scrolling: show && model.settings.lyrics.scrolling,
                reduced: model.settings.overlayMotion == .reduced || model.settings.overlayMotion == .system && NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
                geometrySettled: model.island?.motion.animating != true, active: active))
                .frame(maxWidth: .infinity).frame(height: display.interlude ? 28 : primaryHeight + secondaryHeight)
                .help(model.ui(model.settings.lyrics.translationScrollMode == .adaptive ?
                    "Mac.LyricsAdaptiveScrollHelp" : "Mac.LyricsScrollBaseline"))
        }
    }
}

private struct CompactLyricInput: Equatable {
    var track: String
    var position: Double
    var marqueePosition: Double
    var playing: Bool
    var timelineRevision: UInt64
    var scrollMode: LyricsTranslationScrollMode
    var line: LyricLine?
    var frame: LyricsFrame
    var waiting: Bool, interlude: Bool
    var primary: String, secondary: String
    var fontSize: Double, secondarySize: Double
    var delay: Int
    var wordSynced: Bool, scrolling: Bool, reduced: Bool, geometrySettled: Bool, active: Bool
}
private struct CompactLyricText: NSViewRepresentable {
    let model: WorkspaceModel
    let state: CompactLyricRenderState
    let input: CompactLyricInput
    func makeNSView(context: Context) -> CompactLyricTextView { .init(state: state) }
    func updateNSView(_ view: CompactLyricTextView, context: Context) {
        view.update(input)
        Task { @MainActor [weak view, weak model] in
            guard let view else { return }
            view.connect(model?.island?.glow)
        }
    }
    static func dismantleNSView(_ view: CompactLyricTextView, coordinator: ()) { view.connect(nil) }
}

private final class CompactLyricRenderState {
    let identity = UUID()
    var primaryOffset = 0.0, secondaryOffset = 0.0
    var primaryKey = ""
    var secondarySession = LyricsMarqueeSession()
    var adaptiveSession = LyricsAdaptiveScrollSession()
    var timelineRevision: UInt64?
    var scrollMode: LyricsTranslationScrollMode?
    var lastMarqueePosition = 0.0
    var prefixWidths: [String: Double] = [:]
    var interludeOpacity = 0.0
    var previousTick = ProcessInfo.processInfo.systemUptime
}

private final class CompactLyricTextView: NSView {
    private let state: CompactLyricRenderState
    private var input: CompactLyricInput?
    private var primaryOffset: Double { get { state.primaryOffset } set { state.primaryOffset = newValue } }
    private var secondaryOffset: Double { get { state.secondaryOffset } set { state.secondaryOffset = newValue } }
    private var primaryKey: String { get { state.primaryKey } set { state.primaryKey = newValue } }
    private var secondarySession: LyricsMarqueeSession { get { state.secondarySession } set { state.secondarySession = newValue } }
    private var prefixWidths: [String: Double] { get { state.prefixWidths } set { state.prefixWidths = newValue } }
    private var interludeOpacity: Double { get { state.interludeOpacity } set { state.interludeOpacity = newValue } }
    private var previousTick: Double { get { state.previousTick } set { state.previousTick = newValue } }
    private var highlighted = 0.0
    private var primaryWidth = 0.0, secondaryWidth = 0.0, primaryHeight = 28.0, secondaryHeight = 0.0
    #if DEBUG
    private var tracedAt = 0.0
    private var tracedPlaying: Bool?
    private let trace = ProcessInfo.processInfo.environment["DROPSPACE_VALIDATION_LYRICS_TRACE"] == "1"
    #endif
    private let translation = GlowTranslationView()
    override var isFlipped: Bool { true }
    override var isOpaque: Bool { false }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    init(state: CompactLyricRenderState) {
        self.state = state
        super.init(frame: .zero)
        wantsLayer = true; layer?.masksToBounds = true
        addSubview(translation); setAccessibilityElement(true); setAccessibilityRole(.staticText)
        #if DEBUG
        if trace { NSLog("DropSpaceLyrics view_created state=%@", state.identity.uuidString) }
        #endif
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }
    static func measure(_ text: String, font: NSFont) -> CGSize {
        (text as NSString).size(withAttributes: [.font: font])
    }
    func connect(_ glow: IslandGlow?) {
        if translation.glow !== glow { translation.glow?.unregister(translation); translation.glow = glow }
        glow?.register(translation)
    }
    func update(_ value: CompactLyricInput) {
        input = value
        let first = Self.measure(value.primary, font: .systemFont(ofSize: value.fontSize))
        let second = Self.measure(value.secondary, font: .systemFont(ofSize: value.secondarySize))
        primaryWidth = first.width + 4; primaryHeight = max(28, first.height)
        secondaryWidth = second.width + 4; secondaryHeight = value.secondary.isEmpty ? 0 : second.height
        let now = ProcessInfo.processInfo.systemUptime, elapsed = now - previousTick; previousTick = now
        let amount = !value.playing || value.reduced ? 1 : min(1, max(0, elapsed / 0.167))
        interludeOpacity += min(amount, max(-amount, (value.interlude ? 1 : 0) - interludeOpacity))
        setAccessibilityLabel(value.primary + (value.secondary.isEmpty ? "" : "\n" + value.secondary))
        recalculate()
    }
    override func layout() { super.layout(); recalculate() }
    override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); recalculate() }
    override func viewDidChangeBackingProperties() { super.viewDidChangeBackingProperties(); if let input { update(input) } }
    override func viewDidChangeEffectiveAppearance() { super.viewDidChangeEffectiveAppearance(); needsDisplay = true }
    private func recalculate() {
        guard let input, let window, bounds.width > 0, bounds.height > 0 else { return }
        let scale = window.backingScaleFactor
        let key = "\(input.track)|\(input.line?.start ?? -1)|\(input.fontSize)|\(scale)|\(input.line?.text ?? input.primary)"
        if key != primaryKey { primaryKey = key; primaryOffset = 0; prefixWidths.removeAll(keepingCapacity: true) }
        highlighted = primaryWidth
        let effective = input.position + Double(min(30000, max(-30000, input.delay))) / 1000
        if input.waiting, let line = input.line, effective < line.start { highlighted = 0 }
        if input.wordSynced, !input.waiting, let line = input.line, !line.words.isEmpty {
            highlighted = 0
            if let index = input.frame.wordIndex, line.words.indices.contains(index) {
                let before = LyricsDisplay.compact(line.words.prefix(index).map(\.text).joined())
                let end = before + LyricsDisplay.compact(line.words[index].text)
                let beforeWidth = prefixWidth(before)
                highlighted = beforeWidth + (prefixWidth(end) - beforeWidth) * input.frame.wordProgress
            }
        }
        let pixelWidth = (bounds.width * scale).rounded() / scale
        func viewport(_ width: Double) -> Double { width <= pixelWidth + 0.5 / max(1, scale) ? max(width, pixelWidth) : pixelWidth }
        if state.timelineRevision != input.timelineRevision || state.scrollMode != input.scrollMode {
            state.timelineRevision = input.timelineRevision; state.scrollMode = input.scrollMode
            state.lastMarqueePosition = input.marqueePosition
            secondarySession.reset(); state.adaptiveSession.reset()
        } else { state.lastMarqueePosition = max(state.lastMarqueePosition, input.marqueePosition) }
        if !input.scrolling || input.reduced || !input.active {
            primaryOffset = 0; secondaryOffset = 0; secondarySession.reset(); state.adaptiveSession.reset()
        }
        else if input.interlude { /* Retain the line's layout while its text is hidden. */ }
        else if input.geometrySettled {
            if !input.waiting, let line = input.line {
                primaryOffset = LyricsDisplay.primaryOffset(line: line, position: input.position, delay: input.delay,
                    wordSynced: input.wordSynced, highlightWidth: highlighted, textWidth: primaryWidth, viewportWidth: viewport(primaryWidth))
            }
            let layout = LyricsMarqueeLayout(track: input.track, text: input.secondary, start: input.line?.start ?? 0,
                fontSize: input.secondarySize, scale: scale, textWidth: secondaryWidth, viewportWidth: viewport(secondaryWidth),
                enabled: input.scrolling, reduced: input.reduced)
            if input.scrollMode == .adaptive, let line = input.line, line.timeBasis == .timed, line.end > line.start {
                secondaryOffset = state.adaptiveSession.update(layout, lineEnd: line.end,
                    position: effective, timelineRevision: input.timelineRevision).offset
            } else {
                secondaryOffset = secondarySession.update(layout, position: state.lastMarqueePosition)
            }
        } else {
            if primaryWidth <= viewport(primaryWidth) { primaryOffset = 0 }
            if secondaryWidth <= viewport(secondaryWidth) { secondaryOffset = 0 }
        }
        translation.frame = .init(x: 0, y: primaryHeight, width: min(bounds.width, secondaryWidth), height: secondaryHeight)
        translation.track = input.track; translation.start = input.line?.start ?? 0
        translation.text = input.line?.secondary ?? ""; translation.origin = input.line?.translationOrigin ?? .none
        translation.compact = true; translation.active = !input.secondary.isEmpty && interludeOpacity < 0.99
        translation.alphaValue = 1 - interludeOpacity
        #if DEBUG
        let now = ProcessInfo.processInfo.systemUptime
        if trace, input.active, now - tracedAt >= 1 || tracedPlaying != input.playing {
            tracedAt = now; tracedPlaying = input.playing
            NSLog("DropSpaceLyrics position=%.3f marqueePosition=%.3f playing=%d waiting=%d interlude=%d settled=%d viewport=%.2f primaryWidth=%.2f secondaryWidth=%.2f primaryOffset=%.2f secondaryOffset=%.2f highlight=%.2f revision=%llu adaptive=%d",
                input.position, input.marqueePosition, input.playing ? 1 : 0, input.waiting ? 1 : 0, input.interlude ? 1 : 0, input.geometrySettled ? 1 : 0,
                bounds.width, primaryWidth, secondaryWidth, primaryOffset, secondaryOffset, highlighted, input.timelineRevision,
                input.scrollMode == .adaptive && input.line?.timeBasis == .timed ? 1 : 0)
        }
        #endif
        needsDisplay = true
    }
    private func prefixWidth(_ text: String) -> Double {
        if let value = prefixWidths[text] { return value }
        let value = Self.measure(text, font: .systemFont(ofSize: input?.fontSize ?? 13)).width + 4
        prefixWidths[text] = value
        return value
    }
    override func draw(_ dirtyRect: NSRect) {
        guard let input else { return }
        NSGraphicsContext.saveGraphicsState(); defer { NSGraphicsContext.restoreGraphicsState() }
        NSBezierPath(rect: bounds).addClip()
        let font = NSFont.systemFont(ofSize: input.fontSize)
        let textSize = Self.measure(input.primary, font: font)
        let point = CGPoint(x: 2 - primaryOffset, y: max(0, (primaryHeight - textSize.height) / 2))
        let alpha = 1 - interludeOpacity
        func drawText(_ text: String, font: NSFont, color: NSColor, at point: CGPoint) {
            (text as NSString).draw(at: point, withAttributes: [.font: font, .foregroundColor: color])
        }
        // Semantic secondary colors already contain opacity. Apply the transition
        // to the drawing context so it does not erase their contrast distinction.
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current?.cgContext.setAlpha(alpha)
        drawText(input.primary, font: font, color: .secondaryLabelColor, at: point)
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(rect: .init(x: -primaryOffset, y: 0, width: max(0, highlighted), height: primaryHeight)).addClip()
        drawText(input.primary, font: font, color: .labelColor, at: point)
        NSGraphicsContext.restoreGraphicsState()
        drawText(input.secondary, font: .systemFont(ofSize: input.secondarySize),
            color: .secondaryLabelColor, at: .init(x: 2 - secondaryOffset, y: primaryHeight))
        NSGraphicsContext.restoreGraphicsState()
        if interludeOpacity > 0 {
            let center = CGPoint(x: bounds.midX, y: min(28, bounds.height) / 2)
            for index in 0..<3 {
                let opacity = input.reduced ? 0.65 : 0.55 + 0.35 * sin(input.position * .pi - Double(index) * 0.5)
                NSColor.labelColor.withAlphaComponent(interludeOpacity * opacity).setFill()
                NSBezierPath(ovalIn: .init(x: center.x + Double(index - 1) * 9 - 2, y: center.y - 2, width: 4, height: 4)).fill()
            }
        }
    }
}
