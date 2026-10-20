import Foundation
import Observation
import DropSpaceCore
import DropSpaceInfrastructure
import DropSpacePlatform

@MainActor @Observable final class NativeDevices {
    private(set) var enabled = false
    private(set) var changing = false
    private(set) var discovering = false
    private(set) var pairing = false
    private(set) var sending = 0
    private(set) var clipboardStatus: String?
    private(set) var devices: [DropLinkDevice] = []
    private(set) var peers: [DropLinkPeer] = []
    private(set) var endpoint: URL?
    private(set) var fingerprint: String?
    var error: String? { didSet { if let error { onError?(error) } } }
    var onError: (@MainActor (String) -> Void)?
    var selected: UUID?
    let prompts = NativePairingPrompts()
    let fileTransfers = NativeFileTransfers()
    private(set) var receivedTransfers: [DropLinkTransferRecord] = []
    var onReceive: (@MainActor (HandoffMessage) async throws -> Void)?
    var onPaired: (@MainActor (DropLinkPeer) -> Void)?
    var onUnpaired: (@MainActor (UUID) -> Void)?
    private let clipboard: CrossDeviceClipboardService
    private let clipboardBrowser: DropLinkBonjourBrowser
    private let identities: DropLinkIdentityStore
    private let peerStore: DropLinkPeerStore
    private let registration = DropLinkBonjourRegistration()
    private let browser = DropLinkBonjourBrowser()
    private var identity: DropLinkIdentity?
    private var host: DropLinkPairingHost?
    private var server: DropLinkHTTPServer?
    private var transferHost: DropLinkTransferHost?
    private let transferDirectory: URL
    private let receiveDestination: URL
    private let transferRepository: DropLinkTransferRepository
    private let resolveReference: @MainActor (DropItem) async throws -> URL
    private var jobs: [UUID: Task<Void, Never>] = [:]
    private var failureTask: Task<Void, Never>?
    private var stopped = false
    private var retiring = false
    private let loopbackValidation: Bool
    // Advertise only connected business capabilities; this grows as their real
    // receivers/senders are integrated, never by claiming unsupported actions.
    private let capabilities: PeerCapabilities = [.handoffFiles, .handoffFolders, .handoffText, .handoffURL, .clipboardText, .clipboardURL, .clipboardImage]
    init(directory: URL, validationNamespace: String?, capture: ClipboardCapture, payloads: PayloadStore,
         resolveReference: @escaping @MainActor (DropItem) async throws -> URL) throws {
        self.resolveReference = resolveReference
        #if DEBUG
        loopbackValidation = validationNamespace != nil && ProcessInfo.processInfo.environment["DROPSPACE_DROPLINK_LOOPBACK_VALIDATION"] == "1"
        receiveDestination = validationNamespace != nil ? directory.appendingPathComponent("Received") : FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads/DropSpace")
        #else
        loopbackValidation = false
        receiveDestination = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads/DropSpace")
        #endif
        transferDirectory = directory
        transferRepository = try DropLinkTransferRepository(directory: directory)
        let service = validationNamespace.map { "io.github.airanluo-dot.DropSpace.validation." + Digests.sha256(Data($0.utf8)) } ?? "io.github.airanluo-dot.DropSpace.macOS"
        let keychain = KeychainStore(service: service)
        identities = DropLinkIdentityStore(keychain: keychain)
        peerStore = try DropLinkPeerStore(directory: directory, keychain: keychain)
        let clipboardBrowser = DropLinkBonjourBrowser(); self.clipboardBrowser = clipboardBrowser
        clipboard = try CrossDeviceClipboardService(capture: capture, payloads: payloads, peers: peerStore, discover: { seconds in try await clipboardBrowser.discover(seconds: seconds) })
        registration.onFailure = { [weak self] code in
            guard let self, enabled else { return }
            error = "Bonjour registration stopped: \(code)"
            failureTask = Task { [weak self] in await self?.disable() }
        }
    }
    func configure(_ settings: AppSettings) async throws {
        await failureTask?.value; failureTask = nil
        guard !stopped else { throw CancellationError() }
        await clipboard.setFailureHandler { [weak self] message in await self?.clipboardFailed(message) }
        prompts.language = settings.language
        if !settings.enableDeviceHandoff { await disable(); try await refreshPeers(); return }
        if enabled { try await clipboard.configure(settings, identity: identity); return }
        changing = true; defer { changing = false }
        do {
            try await refreshPeers()
            let identity = try await identities.getOrCreate(name: Host.current().localizedName ?? "Mac")
            let network = loopbackValidation ? nil : try DropLinkNetworkInterface.preferred()
            let host = DropLinkPairingHost(identity: identity, capabilities: capabilities, peers: peerStore) { [weak self] offer in
                guard let self else { throw CancellationError() }
                let accepted = try await self.prompts.ask(offer, incoming: true)
                // The router commits after this callback. The native peer list is
                // refreshed on discovery and when the settings page is revisited.
                return accepted
            }
            let auth = DropLinkAuthenticator { [peerStore] in try await peerStore.secretForTrustedPeer($0) }
            let transfers = try DropLinkTransferHost(directory: transferDirectory, destination: receiveDestination) { [weak self] offer in
                guard let self, await self.enabled else { throw CancellationError() }
                return try await self.prompts.receiveFiles(offer.manifest)
            }
            self.transferHost = transfers; try await transfers.start()
            let handoff = DropLinkHandoffHost { [weak self] message in
                guard let self, await self.enabled else { throw CancellationError() }
                return try await self.receive(message)
            }
            let server = DropLinkHTTPServer(identity: identity, admit: { try await auth.admit($0, address: $1) }, handler: { [weak self] request in
                if request.head.path.lowercased().hasPrefix("/v1/transfers/") { return try await transfers.handle(request) }
                if request.head.method == "POST", request.head.path.trimmingCharacters(in: CharacterSet(charactersIn: "/")).lowercased() == "v1/handoff/text" {
                    return try await handoff.handle(request)
                }
                if request.head.method == "POST", request.head.path.trimmingCharacters(in: CharacterSet(charactersIn: "/")).lowercased() == "v1/clipboard" {
                    let receiver = await self?.clipboard.receiver()
                    return try await DropLinkClipboardHost(receive: receiver).handle(request)
                }
                let response = try await host.handle(request)
                if request.head.path.lowercased().contains("pairing/confirm"), !Task.isCancelled { await self?.pairingDidChange() }
                return response
            })
            self.identity = identity; self.host = host; self.server = server
            let endpoint = try await server.start(address: network?.address ?? "127.0.0.1"); await host.setEndpoint(endpoint)
            if let network {
                try await registration.publish(.init(deviceID: identity.deviceID, name: identity.displayName, platform: .macOS, capabilities: capabilities, fingerprint: identity.fingerprint, endpoint: endpoint), interface: network)
            }
            try Task.checkCancellation()
            self.endpoint = endpoint; fingerprint = identity.fingerprint; enabled = true
            try await clipboard.configure(settings, identity: identity); error = nil
        } catch { await disable(); self.error = error.localizedDescription; throw error }
    }
    func discover() {
        guard !discovering else { return }
        guard enabled else { error = "Enable device handoff before discovering devices."; return }
        discovering = true
        run { owner in
            defer { owner.discovering = false }
            do {
                let found = try await owner.browser.discover(seconds: 3)
                guard owner.enabled, !Task.isCancelled else { return }
                owner.devices = found.filter { $0.id != owner.identity?.deviceID }
                if !owner.devices.contains(where: { $0.id == owner.selected }) { owner.selected = nil }
                try await owner.refreshPeers(); owner.error = nil
            } catch is CancellationError {} catch { owner.error = error.localizedDescription }
        }
    }
    func pairSelected() {
        guard !pairing, enabled, let identity, let device = devices.first(where: { $0.id == selected }) else { return }
        pairing = true
        run { owner in
            defer { owner.pairing = false }
            do {
                let client = DropLinkPairingClient(identity: identity, peers: owner.peerStore)
                let peer = try await client.pair(endpoint: device.endpoint, fingerprint: device.identityFingerprint, expectedDevice: device.id, capabilities: owner.capabilities) { [weak owner] offer in
                    guard let owner else { throw CancellationError() }; return try await owner.prompts.ask(offer, incoming: false)
                }
                try await owner.refreshPeers(); owner.onPaired?(peer); owner.error = nil
            } catch is CancellationError {} catch { owner.error = error.localizedDescription }
        }
    }
    func unpair(_ peer: DropLinkPeer) {
        run { owner in do { try await owner.peerStore.remove(peer.id); try await owner.refreshPeers(); owner.onUnpaired?(peer.id) } catch { owner.error = error.localizedDescription } }
    }
    private func clipboardFailed(_ message: String) { clipboardStatus = message }
    private func receive(_ message: HandoffMessage) async throws -> Bool {
        guard let onReceive else { throw DropSpaceError.unavailable("Handoff receiver unavailable") }
        guard try await prompts.receive(message) else { return false }
        try Task.checkCancellation()
        guard enabled else { throw CancellationError() }
        try await onReceive(message); return true
    }
    func send(_ item: DropItem, selection: [DropItem] = []) {
        let ui = Localizer(prompts.language)
        guard enabled, let identity else { error = ui("DevicesDisabledContent"); return }
        guard !discovering, sending + fileTransfers.activeCount < 2 else { error = ui("TransferBusyContent"); return }
        guard item.file != nil || item.text != nil || item.kind == .image && item.payloadPath != nil else { error = ui("ActionUnavailable"); return }
        sending += 1; discovering = true
        run { owner in
            defer { owner.sending -= 1 }
            do {
                let found: [DropLinkDevice]
                do { found = try await owner.browser.discover(seconds: 3); owner.discovering = false }
                catch { owner.discovering = false; throw error }
                try await owner.refreshPeers(); try Task.checkCancellation()
                let reachable = owner.peers.filter { peer in peer.trustState == .trusted && found.contains { $0.id == peer.id && $0.identityFingerprint.lowercased() == peer.identityFingerprint.lowercased() } }
                guard !reachable.isEmpty else { try await owner.prompts.inform(title: ui("NoPairedDevicesTitle"), content: ui("NoPairedDevicesContent")); return }
                guard let id = try await owner.prompts.choose(reachable), let peer = reachable.first(where: { $0.id == id }), let device = found.first(where: { $0.id == id && $0.identityFingerprint.lowercased() == peer.identityFingerprint.lowercased() }) else { return }
                try Task.checkCancellation()
                guard owner.enabled else { throw CancellationError() }
                if item.file != nil {
                    let selected = !selection.isEmpty && selection.allSatisfy({ $0.file != nil }) ? selection : [item]
                    var urls: [URL] = []
                    for value in selected { urls.append(try await owner.resolveReference(value)) }
                    let required: PeerCapabilities = selected.contains(where: { $0.kind == .folder }) ? [.handoffFiles, .handoffFolders] : [.handoffFiles]
                    guard peer.capabilities.isSuperset(of: required) else { throw DropSpaceError.unavailable("This device does not support the selected file transfer capability.") }
                    owner.fileTransfers.start(client: .init(identity: identity, peers: owner.peerStore), peer: peer, endpoint: device.endpoint, sources: urls, name: selected.map(\.title).joined(separator: ", "))
                    return
                }
                let accepted: Bool, category: String?
                if let text = item.text {
                    let payload = try item.kind == .url ? HandoffMessagePolicy.normalizeURL(text.trimmingCharacters(in: .whitespacesAndNewlines)) : text
                    let result = try await DropLinkHandoffClient(identity: identity, peers: owner.peerStore).send(peer: peer, endpoint: device.endpoint, kind: item.kind == .url ? .url : .text, payload: payload, label: item.title)
                    accepted = result.accepted; category = result.errorCategory
                } else {
                    let result = try await owner.clipboard.sendManual(peer: peer, endpoint: device.endpoint, item: item)
                    accepted = result.accepted; category = result.errorCategory
                }
                owner.error = nil
                try await owner.prompts.inform(title: ui(accepted ? "TransferSentTitle" : "TransferUnavailableTitle"), content: accepted ? ui("TransferSentContent") : category ?? ui("ActionUnavailable"))
            } catch is CancellationError {} catch { owner.error = error.localizedDescription }
        }
    }
    func reloadPeers() { run { owner in do { try await owner.refreshPeers() } catch { owner.error = error.localizedDescription } } }
    private func pairingDidChange() { if enabled { reloadPeers() } }
    func cancelOperations() { for job in jobs.values { job.cancel() }; fileTransfers.cancelAll(); browser.stop(); prompts.stop() }
    func refreshTransfers() async { do { receivedTransfers = try await transferRepository.history() } catch { self.error = error.localizedDescription } }
    func retryTransferCleanup() {
        run { owner in
            do {
                if let host = owner.transferHost { try await host.retryCleanup() }
                else {
                    let recovery = try DropLinkTransferHost(directory: owner.transferDirectory, destination: owner.receiveDestination) { _ in false }
                    try await recovery.retryCleanup()
                }
                await owner.refreshTransfers()
            } catch { owner.error = error.localizedDescription }
        }
    }
    func shutdown() async { stopped = true; await failureTask?.value; failureTask = nil; await disable(); await clipboard.shutdown() }
    private func disable() async {
        retiring = true; defer { retiring = false }
        enabled = false; registration.stop(); browser.stop(); clipboardBrowser.stop(); prompts.stop()
        for job in jobs.values { job.cancel() }
        await fileTransfers.shutdown(); await transferHost?.stop()
        await server?.stop(); await host?.shutdown()
        try? await clipboard.configure(AppSettings(), identity: nil)
        let owned = Array(jobs.values); for job in owned { job.cancel() }
        for job in owned { await job.value }
        server = nil; host = nil; transferHost = nil; endpoint = nil; fingerprint = nil
        devices.removeAll(); selected = nil
    }
    private func refreshPeers() async throws { try await peerStore.recover(); peers = try await peerStore.peers() }
    private func run(_ action: @escaping @MainActor (NativeDevices) async -> Void) {
        guard !stopped, !retiring else { return }
        let id = UUID(); jobs[id] = Task { [weak self] in guard let self else { return }; await action(self); jobs[id] = nil }
    }
}
