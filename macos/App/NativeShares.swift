import SwiftUI
import Observation
import Network
import CoreImage
import UniformTypeIdentifiers
import DropSpaceCore
import DropSpaceInfrastructure
import DropSpacePlatform

@MainActor @Observable final class NativeShares {
    var showsPanel = false
    private(set) var items: [NearbyShareSummary] = []
    private(set) var selected: UUID?
    private(set) var descriptor: ShareDescriptor?
    private(set) var creating = false
    var feedbackKey: String?
    var error: String?
    var showsQR = false
    private weak var model: WorkspaceModel?
    private let server: NearbyShareServer
    private let monitor = NWPathMonitor()
    private var sawInitialPath = false
    private var observation: Task<Void, Never>?
    private var creation: Task<Void, Never>?
    private var actions: [UUID: Task<Void, Never>] = [:]
    private var stopped = false
    init(model: WorkspaceModel) throws {
        self.model = model; server = try NearbyShareServer { try DropLinkNetworkInterface.preferred().address }
        monitor.pathUpdateHandler = { [weak self] _ in Task { @MainActor in
            guard let self, !self.stopped else { return }
            if !self.sawInitialPath { self.sawInitialPath = true; return }
            self.run { owner in await owner.server.networkChanged() }
        } }
        monitor.start(queue: DispatchQueue(label: "DropSpace.NearbyShare.Network"))
        observation = Task { [weak self, server] in
            for await snapshot in await server.snapshots() {
                guard !Task.isCancelled else { return }; await self?.apply(snapshot)
            }
        }
    }
    private func apply(_ snapshot: NearbyShareSnapshot) async {
        items = snapshot.items
        if let selected, !items.contains(where: { $0.id == selected }) {
            self.selected = nil; descriptor = nil; showsQR = false; feedbackKey = "NearbyLinkUnavailable"
        }
    }
    func select(_ id: UUID?) {
        selected = id; descriptor = nil; showsQR = false; feedbackKey = nil
        guard let id else { return }
        run { owner in
            let value = await owner.server.descriptor(id)
            guard owner.selected == id else { return }; owner.descriptor = value
            if value == nil { owner.feedbackKey = "NearbyLinkUnavailable" }
        }
    }
    func create(_ items: [DropItem]) {
        guard !stopped, !creating, let model else { return }
        showsPanel = true; error = nil; feedbackKey = nil
        guard model.settings.enableNearbySharing else { feedbackKey = "NearbyLinksDisabled"; return }
        creating = true
        creation = Task { [weak self, weak model] in
            guard let self, let model else { return }
            defer { creating = false; creation = nil }
            do {
                var inputs: [ShareSourceInput] = []
                for item in items {
                    try Task.checkCancellation()
                    if item.file != nil {
                        let url = try await model.checkedContentURL(item); inputs.append(item.kind == .folder ? .folder(url) : .file(url))
                    } else if let payload = item.payloadPath {
                        let url = try await model.payloads.url(payload)
                        let mime = UTType(filenameExtension: url.pathExtension)?.preferredMIMEType ?? "application/octet-stream"
                        inputs.append(.payload(url, name: item.title, mime: mime))
                    } else if let text = item.text { inputs.append(.text(text, name: item.title, isURL: item.kind == .url)) }
                }
                let sources = try await ShareSourcePreparation().prepare(inputs)
                try Task.checkCancellation()
                guard model.settings.enableNearbySharing else { feedbackKey = "NearbyLinksDisabled"; return }
                let result = try await server.create(sources)
                if Task.isCancelled || stopped { _ = await server.revoke(result.id); throw CancellationError() }
                self.items = await server.snapshot().items; selected = result.id; descriptor = result; feedbackKey = "ShareReadyTitle"
            } catch is CancellationError { }
            catch { self.error = error.localizedDescription }
        }
    }
    func cancelCreation() { creation?.cancel() }
    func copy() {
        guard let selected else { return }
        run { owner in
            guard let value = await owner.server.descriptor(selected), owner.selected == selected else { owner.feedbackKey = "NearbyLinkUnavailable"; return }
            owner.model?.copy([.init(source: .space, kind: .url, title: "Nearby link", text: value.url.absoluteString)])
        }
    }
    func revoke() {
        guard let selected else { return }
        run { owner in
            let revoked = await owner.server.revoke(selected)
            owner.descriptor = nil; owner.selected = nil; owner.showsQR = false
            owner.feedbackKey = revoked ? "RevokeNearbyLinkCompleted" : "NearbyLinkUnavailable"
        }
    }
    private func run(_ operation: @escaping @MainActor (NativeShares) async -> Void) {
        guard !stopped else { return }; let id = UUID()
        actions[id] = Task { [weak self] in guard let self else { return }; await operation(self); actions[id] = nil }
    }
    func shutdown() async -> Bool {
        stopped = true; monitor.cancel(); monitor.pathUpdateHandler = nil
        creation?.cancel(); await creation?.value
        observation?.cancel(); await observation?.value; observation = nil
        let owned = Array(actions.values); for task in owned { task.cancel() }; for task in owned { await task.value }
        descriptor = nil; selected = nil; items.removeAll(); showsQR = false
        return await server.stop()
    }
}

struct NearbySharesButton: View {
    @Bindable var shares: NativeShares
    let model: WorkspaceModel
    var body: some View {
        Button(model.ui("ManageNearbyLinksLabel.Text"), systemImage: "link") { shares.showsPanel = true }
            .accessibilityIdentifier("NearbySharesButton")
            .sheet(isPresented: $shares.showsPanel) { NearbySharesView(shares: shares, model: model) }
    }
}
private struct NearbySharesView: View {
    @Bindable var shares: NativeShares
    let model: WorkspaceModel
    @State private var confirmsRevoke = false
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(model.ui("NearbyLinksTitle")).font(.title2)
            Text(model.ui("NearbyShareReady"))
            Text(model.ui("NearbyLinksLifetime")).font(.caption).foregroundStyle(.secondary)
            if !model.settings.enableNearbySharing { Text(model.ui("NearbyLinksDisabled")).foregroundStyle(.secondary) }
            if shares.creating { HStack { ProgressView(); Button(model.ui("CommonCancel")) { shares.cancelCreation() } } }
            if shares.items.isEmpty { Text(model.ui("NearbyLinksEmpty")).foregroundStyle(.secondary) }
            else {
                List(selection: Binding(get: { shares.selected }, set: shares.select)) {
                    ForEach(shares.items) { item in
                        VStack(alignment: .leading) {
                            Text(item.displayName)
                            Text(model.ui("NearbyLinksSummary", String(item.itemCount), ByteCountFormatter.string(fromByteCount: item.totalBytes, countStyle: .file))).font(.caption).foregroundStyle(.secondary)
                        }.tag(item.id)
                    }
                }.frame(minHeight: 90, maxHeight: 180).accessibilityIdentifier("NearbySharesList")
            }
            if let descriptor = shares.descriptor {
                Text(model.ui("ShareExpires", expiryText(descriptor.expiresAt)))
                Text(descriptor.url.absoluteString).font(.callout.monospaced()).textSelection(.enabled).accessibilityIdentifier("NearbyShareURL")
                HStack {
                    Button(model.ui("CopyShareLink")) { shares.copy() }.accessibilityIdentifier("CopyNearbyShare")
                    Button(model.ui("OpenShareQr")) { shares.showsQR.toggle() }
                    Button(model.ui("RevokeShareButton"), role: .destructive) { confirmsRevoke = true }.accessibilityIdentifier("RevokeNearbyShare")
                }
                if shares.showsQR { NearbyShareQR(value: descriptor.url.absoluteString, failureText: model.ui("Mac.ShareQRFailed")).frame(width: 240, height: 240).accessibilityLabel(model.ui("NearbyLinkQrAutomationName")) }
            }
            if let key = shares.feedbackKey { Text(model.ui(key)).accessibilityIdentifier("NearbyShareFeedback") }
            if let error = shares.error { Text(error).foregroundStyle(.red).textSelection(.enabled) }
            HStack { Spacer(); Button(model.ui("CommonClose")) { shares.showsPanel = false }.keyboardShortcut(.cancelAction) }
        }.padding(24).frame(width: 540)
            .confirmationDialog(model.ui("RevokeNearbyLinkTitle"), isPresented: $confirmsRevoke) {
                Button(model.ui("RevokeShareButton"), role: .destructive) { shares.revoke() }
            } message: { Text(model.ui("RevokeNearbyLinkContent", shares.items.first(where: { $0.id == shares.selected })?.displayName ?? model.ui("NearbyLinkLabel"))) }
    }
    private func expiryText(_ date: Date) -> String {
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: model.ui.language)
        formatter.dateStyle = .medium; formatter.timeStyle = .short; return formatter.string(from: date)
    }
}
struct NearbyShareQR: View {
    let value: String
    let failureText: String
    var body: some View {
        if let image = image {
            Image(image, scale: 1, label: Text(value)).interpolation(.none).resizable().scaledToFit().padding(16).background(.white)
        } else { Text(failureText).foregroundStyle(.red) }
    }
    private var image: CGImage? {
        guard let filter = CIFilter(name: "CIQRCodeGenerator") else { return nil }
        filter.setValue(Data(value.utf8), forKey: "inputMessage"); filter.setValue("Q", forKey: "inputCorrectionLevel")
        guard let output = filter.outputImage else { return nil }; return CIContext().createCGImage(output, from: output.extent)
    }
}
