import SwiftUI
import AppKit
import Quartz
import UniformTypeIdentifiers

struct NativeTextPreview: NSViewRepresentable {
    let text: String
    let monospaced: Bool
    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        guard let view = scroll.documentView as? NSTextView else { return scroll }
        view.isEditable = false; view.isSelectable = true; view.isRichText = false
        view.textContainerInset = .init(width: 10, height: 10)
        return scroll
    }
    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let view = scroll.documentView as? NSTextView else { return }
        if view.string != text { view.string = text }
        view.font = monospaced ? .monospacedSystemFont(ofSize: 13, weight: .regular) : .systemFont(ofSize: 13)
    }
}

struct QuickLookPreview: NSViewRepresentable {
    let url: URL
    func makeNSView(context: Context) -> QLPreviewView { let view = QLPreviewView(frame: .zero,style: .normal)!; view.autostarts = true; return view }
    func updateNSView(_ view: QLPreviewView,context: Context) { view.previewItem = url as NSURL }
    static func dismantleNSView(_ view: QLPreviewView,coordinator: ()) { view.close() }
}
struct DropContainer<Content: View>: NSViewRepresentable {
    let onFiles: ([URL]) -> Void
    let onText: (String) -> Void
    var onURL: ((String) -> Void)? = nil
    var onPromised: (([NSFilePromiseReceiver]) -> Bool)? = nil
    var onFailure: ((String) -> Void)? = nil
    var onDragPresence: ((Bool) -> Void)? = nil
    var onDropAccepted: (() -> Void)? = nil
    @ViewBuilder let content: () -> Content
    func makeNSView(context: Context) -> NativeDropView<Content> { let view = NativeDropView(content: content(),onFiles: onFiles,onText: onText); configure(view); return view }
    func updateNSView(_ view: NativeDropView<Content>,context: Context) { view.host.rootView = content(); configure(view) }
    private func configure(_ view: NativeDropView<Content>) { view.onFiles = onFiles; view.onText = onText; view.onURL = onURL; view.onPromised = onPromised; view.onFailure = onFailure; view.onDragPresence = onDragPresence; view.onDropAccepted = onDropAccepted }
}
final class NativeDropView<Content: View>: NSView {
    let host: NSHostingView<Content>
    var onFiles: ([URL]) -> Void
    var onText: (String) -> Void
    var onURL: ((String) -> Void)?
    var onPromised: (([NSFilePromiseReceiver]) -> Bool)?
    var onFailure: ((String) -> Void)?
    var onDragPresence: ((Bool) -> Void)?
    var onDropAccepted: (() -> Void)?
    init(content: Content,onFiles: @escaping ([URL]) -> Void,onText: @escaping (String) -> Void) {
        host = NSHostingView(rootView: content); self.onFiles = onFiles; self.onText = onText
        super.init(frame: .zero)
        host.translatesAutoresizingMaskIntoConstraints = false; addSubview(host)
        NSLayoutConstraint.activate([host.leadingAnchor.constraint(equalTo: leadingAnchor),host.trailingAnchor.constraint(equalTo: trailingAnchor),host.topAnchor.constraint(equalTo: topAnchor),host.bottomAnchor.constraint(equalTo: bottomAnchor)])
        registerForDraggedTypes([.fileURL,.URL,.string] + NSFilePromiseReceiver.readableDraggedTypes.map { NSPasteboard.PasteboardType($0) })
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        let accepts = sender.draggingPasteboard.availableType(from: [.fileURL,.URL,.string] + NSFilePromiseReceiver.readableDraggedTypes.map { NSPasteboard.PasteboardType($0) }) != nil
        if accepts { onDragPresence?(true) }; return accepts ? .copy : []
    }
    override func draggingExited(_ sender: NSDraggingInfo?) { onDragPresence?(false) }
    override func draggingEnded(_ sender: NSDraggingInfo) { onDragPresence?(false) }
    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool { true }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let board = sender.draggingPasteboard
        // Like the frozen OLE classifier's filesystem-before-virtual ordering,
        // a real file URL remains a reference even if the source also promises it.
        if let urls = board.readObjects(forClasses: [NSURL.self],options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty { onFiles(urls); onDropAccepted?(); return true }
        if let onPromised, let promises = board.readObjects(forClasses: [NSFilePromiseReceiver.self]) as? [NSFilePromiseReceiver], !promises.isEmpty {
            // No materialization happens in draggingEntered. The application
            // owner survives this view's removal and owns cancellation/cleanup.
            let accepted = onPromised(promises)
            if accepted { onDropAccepted?() }
            return accepted
        }
        if let url = board.string(forType: .URL) { (onURL ?? onText)(url); onDropAccepted?(); return true }
        if let text = board.string(forType: .string) { onText(text); onDropAccepted?(); return true }
        return false
    }
}
