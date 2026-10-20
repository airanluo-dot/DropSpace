import SwiftUI
import UniformTypeIdentifiers
import DropSpaceCore
import DropSpaceInfrastructure
import DropSpacePlatform

@MainActor @Observable final class NativeInternetShares {
    var showsPanel = false
    private(set) var records: [InternetShareRecord] = []
    private(set) var selected: UUID?
    private(set) var descriptor: ShareDescriptor?
    private(set) var creating = false
    private(set) var revoking = false
    var showsQR = false
    var error: String?
    var feedbackKey: String?
    let isConfigured: Bool
    private let service: InternetShareService
    private weak var model: WorkspaceModel?
    private var descriptors: [UUID: ShareDescriptor] = [:]
    private var creation: Task<Void, Never>?
    private var revocation: Task<Void, Never>?
    private var initialization: Task<Void, Never>?
    private var stopped = false
    init(model: WorkspaceModel) throws {
        self.model = model
        let configured = ProcessInfo.processInfo.environment["DROPSPACE_SHARE_BACKEND_URL"].flatMap(URL.init(string:))
        let namespace = ProcessInfo.processInfo.environment["DROPSPACE_DATA_DIRECTORY"].map { ".validation." + Digests.sha256(Data($0.utf8)).prefix(16) } ?? ""
        service = try InternetShareService(directory: model.paths.root.appendingPathComponent("InternetShares"), endpoint: configured,
            budget: model.downloadBudget, keys: KeychainStore(service: "io.github.airanluo-dot.DropSpace.InternetShare" + namespace))
        isConfigured = service.isConfigured
        initialization = Task { [weak self] in
            guard let self else { return }
            do { try await service.initialize(); await reload() } catch { self.error = error.localizedDescription }
        }
    }
    func reload() async {
        guard !stopped else { return }
        do {
            records = try await service.records()
            let active = Set(records.filter { $0.state == .completed && $0.expiresAt > Date() }.map(\.id))
            descriptors = descriptors.filter { active.contains($0.key) }
            if let selected { descriptor = descriptors[selected]; if descriptor == nil { showsQR = false } }
        } catch { self.error = error.localizedDescription }
    }
    func select(_ id: UUID?) { selected = id; descriptor = id.flatMap { descriptors[$0] }; showsQR = false; feedbackKey = nil }
    func create(_ items: [DropItem]) {
        guard !stopped, !creating, let model else { return }
        showsPanel = true; error = nil; feedbackKey = nil
        guard model.settings.enableInternetSharing, isConfigured else { return }
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
                        inputs.append(.payload(url, name: item.title, mime: UTType(filenameExtension: url.pathExtension)?.preferredMIMEType ?? "application/octet-stream"))
                    } else if let text = item.text { inputs.append(.text(text, name: item.title, isURL: item.kind == .url)) }
                }
                let sources = try await ShareSourcePreparation().prepare(inputs); try Task.checkCancellation()
                let result = try await service.create(sources, enabled: model.settings.enableInternetSharing)
                if Task.isCancelled || stopped {
                    let cleanup = Task { try await self.service.revoke(result.id) }; try await cleanup.value; throw CancellationError()
                }
                descriptors[result.id] = result; selected = result.id; descriptor = result; feedbackKey = "ShareReadyTitle"
            } catch is CancellationError { }
            catch { self.error = error.localizedDescription }
            await reload()
        }
    }
    func cancel() { creation?.cancel(); revocation?.cancel() }
    func copy() {
        guard let descriptor, descriptor.expiresAt > Date() else { return }
        model?.copy([.init(source: .space, kind: .url, title: "Secure share link", text: descriptor.url.absoluteString)])
    }
    func revoke() {
        guard !stopped, !revoking, let selected else { return }
        revoking = true; error = nil; feedbackKey = nil
        revocation = Task { [weak self] in
            guard let self else { return }; defer { revoking = false; revocation = nil }
            do {
                try await service.revoke(selected)
                descriptors[selected] = nil; descriptor = nil; showsQR = false; feedbackKey = "RevokeShareCompleted"
            } catch is CancellationError { }
            catch { self.error = error.localizedDescription }
            await reload()
        }
    }
    func shutdown() async -> Bool {
        stopped = true; creation?.cancel(); revocation?.cancel(); initialization?.cancel()
        let retired = await service.shutdown()
        if retired { await creation?.value; await revocation?.value; await initialization?.value }
        descriptor = nil; descriptors.removeAll(); showsQR = false
        return retired
    }
}

struct InternetSharesButton: View {
    @Bindable var shares: NativeInternetShares
    let model: WorkspaceModel
    var body: some View {
        Button(model.ui("Mac.InternetManage"), systemImage: "lock.shield") { shares.showsPanel = true }
            .accessibilityIdentifier("InternetSharesButton")
            .sheet(isPresented: $shares.showsPanel) { InternetSharesView(shares: shares, model: model) }
    }
}
private struct InternetSharesView: View {
    @Bindable var shares: NativeInternetShares
    let model: WorkspaceModel
    @State private var confirmsRevoke = false
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(model.ui("InternetSharingTitle.Text")).font(.title2)
            Text(model.ui("EncryptedShareReady")).font(.callout)
            if !shares.isConfigured { Text(model.ui("Mac.InternetBackendMissing")).accessibilityIdentifier("InternetBackendUnavailable") }
            if !model.settings.enableInternetSharing { Text(model.ui("Mac.InternetDisabled")).foregroundStyle(.secondary) }
            if shares.creating || shares.revoking { HStack { ProgressView(); Text(model.ui("Mac.InternetWorking")); Button(model.ui("CommonCancel")) { shares.cancel() } } }
            if shares.records.isEmpty { Text(model.ui("Mac.InternetEmpty")).foregroundStyle(.secondary) }
            else {
                List(selection: Binding(get: { shares.selected }, set: shares.select)) {
                    ForEach(shares.records) { record in
                        VStack(alignment: .leading) {
                            Text(record.displayName)
                            Text(model.ui("Mac.InternetState." + record.state.rawValue)).font(.caption).foregroundStyle(.secondary)
                            if let error = record.error { Text(error).font(.caption).foregroundStyle(.red) }
                            if let error = record.cleanupError { Text(error).font(.caption).foregroundStyle(.red) }
                        }.tag(record.id)
                    }
                }.frame(minHeight: 90, maxHeight: 180).accessibilityIdentifier("InternetSharesList")
            }
            if let record = shares.records.first(where: { $0.id == shares.selected }) {
                Text(model.ui("ShareExpires", expiry(record.expiresAt)))
                if let descriptor = shares.descriptor {
                    Text(descriptor.url.absoluteString).font(.callout.monospaced()).textSelection(.enabled).accessibilityIdentifier("InternetShareURL")
                    HStack {
                        Button(model.ui("CopyShareLink")) { shares.copy() }
                        Button(model.ui("OpenShareQr")) { shares.showsQR.toggle() }
                    }
                    if shares.showsQR { NearbyShareQR(value: descriptor.url.absoluteString, failureText: model.ui("Mac.ShareQRFailed")).frame(width: 240, height: 240) }
                } else if record.state == .completed { Text(model.ui("Mac.InternetLinkNotRetained")).font(.caption) }
                if !shares.revoking { Button(model.ui(record.state == .revoked ? "ModuleRetryCleanup" : "RevokeShareButton"), role: .destructive) { confirmsRevoke = true } }
            }
            if let key = shares.feedbackKey { Text(model.ui(key)) }
            if let error = shares.error { Text(error).foregroundStyle(.red).textSelection(.enabled) }
            HStack { Spacer(); Button(model.ui("CommonClose")) { shares.showsPanel = false }.keyboardShortcut(.cancelAction) }
        }.padding(24).frame(width: 560)
            .confirmationDialog(model.ui("RevokeShareTitle"), isPresented: $confirmsRevoke) {
                Button(model.ui("RevokeShareButton"), role: .destructive) { shares.revoke() }
            } message: { Text(model.ui("RevokeShareContent")) }
            .task { while !Task.isCancelled { await shares.reload(); do { try await Task.sleep(for: .seconds(3)) } catch { return } } }
    }
    private func expiry(_ date: Date) -> String {
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: model.ui.language); formatter.dateStyle = .medium; formatter.timeStyle = .short; return formatter.string(from: date)
    }
}
