import AppKit
import SwiftUI
import DropSpaceCore

enum NativeDragContent: Sendable {
    case file(URL, folder: Bool)
    case text(String, url: URL?)
}

private struct DragRegionKey: PreferenceKey {
    static let defaultValue: [UUID: CGRect] = [:]
    static func reduce(value: inout [UUID: CGRect], nextValue: () -> [UUID: CGRect]) {
        value.merge(nextValue(), uniquingKeysWith: { _, latest in latest })
    }
}
extension View {
    func nativeDragRegion(_ id: UUID, space: String) -> some View {
        background(GeometryReader { geometry in
            // List rows may be hosted in a separate native subtree. A named
            // coordinate space does not cross that hosting boundary reliably.
            Color.clear.preference(key: DragRegionKey.self, value: [id: geometry.frame(in: .global)])
        })
    }
    func nativeDragSource(space: String, prepare: @escaping @MainActor (UUID) async throws -> [NativeDragContent],
                          failure: @escaping @MainActor (String) -> Void) -> some View {
        modifier(NativeDragSourceModifier(space: space, prepare: prepare, failure: failure))
    }
}
private struct NativeDragSourceModifier: ViewModifier {
    let space: String
    let prepare: @MainActor (UUID) async throws -> [NativeDragContent]
    let failure: @MainActor (String) -> Void
    @State private var regions: [UUID: CGRect] = [:]
    func body(content: Content) -> some View {
        content.coordinateSpace(name: space)
            .onPreferenceChange(DragRegionKey.self) { regions = $0 }
            .background {
                GeometryReader { geometry in
                    let origin = geometry.frame(in: .global).origin
                    NativeDragBridge(regions: regions.mapValues { $0.offsetBy(dx: -origin.x, dy: -origin.y) },
                                     prepare: prepare, failure: failure)
                        .frame(width: geometry.size.width, height: geometry.size.height)
                }
            }
    }
}
private struct NativeDragBridge: NSViewRepresentable {
    let regions: [UUID: CGRect]
    let prepare: @MainActor (UUID) async throws -> [NativeDragContent]
    let failure: @MainActor (String) -> Void
    func makeNSView(context: Context) -> NativeDragView { NativeDragView() }
    func updateNSView(_ view: NativeDragView, context: Context) {
        view.regions = regions; view.prepare = prepare; view.failure = failure
    }
    static func dismantleNSView(_ view: NativeDragView, coordinator: ()) { view.retire() }
}
/// Observes only this view's window and row regions. Normal clicks, buttons,
/// context menus and selection stay with SwiftUI; only an admitted drag is owned.
private final class NativeDragView: NSView, NSDraggingSource {
    var regions: [UUID: CGRect] = [:]
    var prepare: (@MainActor (UUID) async throws -> [NativeDragContent])?
    var failure: (@MainActor (String) -> Void)?
    private var monitor: Any?
    private var down: (id: UUID, point: CGPoint)?
    private var latestDrag: NSEvent?
    private var preparation: Task<Void, Never>?
    private var generation = 0
    private var dragging = false
    private func trace(_ message: @autoclosure () -> String) {
        #if DEBUG
        if ProcessInfo.processInfo.environment["DROPSPACE_VALIDATION_DRAG_TRACE"] == "1" {
            NSLog("DropSpace drag: %@", message())
        }
        #endif
    }
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if window == nil { retire(); return }
        guard monitor == nil else { return }
        setAccessibilityElement(false)
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .leftMouseDragged, .leftMouseUp, .keyDown]) { [weak self] event in
            guard let self else { return event }
            return self.observe(event)
        }
    }
    private func observe(_ event: NSEvent) -> NSEvent? {
        guard event.window === window, let window, window.isVisible else { return event }
        if event.type == .keyDown, event.keyCode == 53 {
            if preparation != nil { cancelPreparation(); return nil }
            return event
        }
        if event.type == .leftMouseUp { cancelPreparation(); return event }
        if event.type == .leftMouseDown {
            cancelPreparation()
            let point = convert(event.locationInWindow, from: nil)
            trace("down point=\(point) bounds=\(bounds) visible=\(visibleRect) regions=\(regions.values.map { $0 })")
            guard bounds.contains(point), visibleRect.contains(point),
                  let row = regions.first(where: { $0.value.contains(point) }) else { return event }
            down = (row.key, point)
            trace("row admitted")
            return event
        }
        guard event.type == .leftMouseDragged, let down, !dragging else { return event }
        let point = convert(event.locationInWindow, from: nil)
        guard hypot(point.x - down.point.x, point.y - down.point.y) >= 4 else { return event }
        trace("drag point=\(point) buttons=\(NSEvent.pressedMouseButtons)")
        latestDrag = event
        guard preparation == nil, let prepare else { return nil }
        let run = generation
        preparation = Task { [weak self] in
            defer {
                if let self, generation == run {
                    preparation = nil; self.down = nil; latestDrag = nil
                }
            }
            do {
                let content = try await prepare(down.id)
                try Task.checkCancellation()
                self?.trace("prepared count=\(content.count) buttons=\(NSEvent.pressedMouseButtons)")
                guard let self, generation == run, let event = latestDrag,
                      window.isVisible, NSEvent.pressedMouseButtons & 1 != 0 else { return }
                preparation = nil; self.down = nil
                guard !content.isEmpty else { return }
                var items: [NSDraggingItem] = []
                for value in content {
                    let writer: any NSPasteboardWriting
                    let symbol: String
                    switch value {
                    case .file(let url, let folder):
                        writer = url as NSURL; symbol = folder ? "folder" : "doc"
                    case .text(let text, let url):
                        let item = NSPasteboardItem(); item.setString(text, forType: .string)
                        if let url { item.setString(url.absoluteString, forType: .URL) }
                        writer = item; symbol = url == nil ? "text.alignleft" : "link"
                    }
                    let item = NSDraggingItem(pasteboardWriter: writer)
                    let point = convert(event.locationInWindow, from: nil)
                    item.setDraggingFrame(CGRect(x: point.x - 18, y: point.y - 18, width: 36, height: 36),
                                          contents: NSImage(systemSymbolName: symbol, accessibilityDescription: nil))
                    items.append(item)
                }
                dragging = true
                trace("session begin count=\(items.count)")
                let session = beginDraggingSession(with: items, event: event, source: self)
                session.animatesToStartingPositionsOnCancelOrFail = true
                session.draggingFormation = .stack
            } catch is CancellationError {}
            catch {
                guard let self, generation == run else { return }
                preparation = nil; self.down = nil; failure?(error.localizedDescription)
            }
        }
        return nil
    }
    private func cancelPreparation() {
        generation += 1; preparation?.cancel(); preparation = nil; down = nil; latestDrag = nil
    }
    func retire() {
        cancelPreparation()
        if let monitor { NSEvent.removeMonitor(monitor); self.monitor = nil }
    }
    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { .copy }
    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        trace("session ended operation=\(operation.rawValue)")
        dragging = false; cancelPreparation()
    }
}
