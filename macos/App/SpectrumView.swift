import SwiftUI
import AppKit
import DropSpaceCore
import DropSpacePlatform

struct SpectrumView: View {
    let model: WorkspaceModel
    var active = true
    var height: CGFloat = 24
    var body: some View {
        ZStack {
            Color.clear.frame(width: 1, height: 1)
            if model.settings.islandActivity.showSpectrum, model.spectrum.hasPresentation {
                HStack(alignment: .center, spacing: 3) {
                    ForEach(0..<6, id: \.self) { index in
                        RoundedRectangle(cornerRadius: 2)
                            .fill(.tint)
                            .frame(width: 4, height: 2 + (height - 2) * model.spectrum.frame.bands[index])
                    }
                }.frame(width: 39, height: height).accessibilityHidden(true)
            }
        }
        // Presence stays installed before the first packet, even with no bars.
        .background(SpectrumPresence(service: model.spectrum, active: active && model.settings.islandActivity.showSpectrum))
    }
}
struct SpectrumStatus: View {
    let model: WorkspaceModel
    var body: some View {
        if model.settings.islandActivity.showSpectrum || model.settings.lyrics.glowMode != "off" {
            VStack(alignment: .leading, spacing: 4) {
                switch model.spectrum.state {
                case .stopped: EmptyView()
                case .starting: Text(model.ui("Mac.SpectrumStarting"))
                case .sourceUnconfirmed: Text(model.ui("Mac.SpectrumSourceUnavailable"))
                case .waiting: Text(model.ui("Mac.SpectrumWaiting"))
                case .capturing: Text(model.ui("Mac.SpectrumSource", model.media.snapshot.sourceName))
                case .failed:
                    Text(model.ui("Mac.SpectrumFailed", model.spectrum.failure ?? ""))
                    Button(model.ui("Mac.SpectrumRetry")) { model.spectrum.retry() }.accessibilityIdentifier("spectrum-retry")
                }
            }.font(.caption).foregroundStyle(.secondary).accessibilityIdentifier("spectrum-status")
        }
    }
}
private struct SpectrumPresence: NSViewRepresentable {
    let service: NativeSpectrumService
    let active: Bool
    func makeNSView(context: Context) -> PresenceView { PresenceView(service: service) }
    func updateNSView(_ view: PresenceView, context: Context) {
        view.active = active
        Task { @MainActor [weak view] in view?.publish() }
    }
    static func dismantleNSView(_ view: PresenceView, coordinator: ()) { view.retire() }
    final class PresenceView: NSView {
        let owner = UUID()
        weak var service: NativeSpectrumService?
        var active = false
        private var observers: [NSObjectProtocol] = []
        init(service: NativeSpectrumService) { self.service = service; super.init(frame: .zero) }
        required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observers.forEach(NotificationCenter.default.removeObserver); observers = []
            if let window {
                for name in [NSWindow.didChangeOcclusionStateNotification, NSWindow.didMiniaturizeNotification, NSWindow.didDeminiaturizeNotification, NSWindow.willCloseNotification] {
                    observers.append(NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { [weak self] note in
                        let closing = note.name == NSWindow.willCloseNotification
                        MainActor.assumeIsolated {
                            guard let self else { return }
                            if closing { self.service?.setPresentation(self.owner, visible: false) } else { self.publish() }
                        }
                    })
                }
            }
            publish()
        }
        func publish() { service?.setPresentation(owner, visible: active && window?.occlusionState.contains(.visible) == true && window?.isMiniaturized == false && !isHiddenOrHasHiddenAncestor) }
        func retire() {
            observers.forEach(NotificationCenter.default.removeObserver); observers = []
            service?.setPresentation(owner, visible: false); service = nil
        }
    }
}
