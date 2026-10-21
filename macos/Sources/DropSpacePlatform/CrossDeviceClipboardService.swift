import Foundation
import UniformTypeIdentifiers
import DropSpaceCore
import DropSpaceInfrastructure

public actor CrossDeviceClipboardService {
    public typealias Discover = @Sendable (TimeInterval) async throws -> [DropLinkDevice]
    public typealias Receiver = @Sendable (ClipboardEnvelope) async throws -> Void
    private struct Channel: Equatable {
        let peer: DropLinkPeer
        let endpoint: URL
        let mode: ClipboardSyncMode
        static func == (lhs: Self, rhs: Self) -> Bool { lhs.peer.id == rhs.peer.id && lhs.peer.identityFingerprint == rhs.peer.identityFingerprint && lhs.endpoint == rhs.endpoint && lhs.mode == rhs.mode }
    }
    private let capture: ClipboardCapture
    private let payloads: PayloadStore
    private let peers: DropLinkPeerStore
    private let discover: Discover
    private var failure: @Sendable (String) async -> Void
    private let lifecycle = DownloadPermitPool(limit: 1)
    private let receipts: ClipboardLoopGuard
    private var settings = AppSettings()
    private var identity: DropLinkIdentity?
    private var queue: ClipboardPropagationQueue?
    private var generation = 0
    private var sequence: Int64 = 0
    private var channels: [UUID: Channel] = [:]
    private var nextRefresh = Date.distantPast
    private var enabled = false
    private var disposed = false
    public init(capture: ClipboardCapture, payloads: PayloadStore, peers: DropLinkPeerStore, discover: @escaping Discover, failure: @escaping @Sendable (String) async -> Void = { _ in }) throws {
        self.capture = capture; self.payloads = payloads; self.peers = peers; self.discover = discover; self.failure = failure
        receipts = try ClipboardLoopGuard()
    }
    public func setFailureHandler(_ handler: @escaping @Sendable (String) async -> Void) { failure = handler }
    public func configure(_ value: AppSettings, identity: DropLinkIdentity?) async throws {
        let permit = try await lifecycle.acquire()
        do {
            guard !disposed else { throw CancellationError() }
            let previous = settings; settings = value
            guard value.enableCrossDeviceClipboard, value.enableDeviceHandoff, let identity else {
                await disableOwned(); await lifecycle.release(permit); return
            }
            if enabled {
                if previous.clipboardPeerModes != value.clipboardPeerModes {
                    channels = channels.compactMapValues { channel in mode(for: channel.peer.id).map { Channel(peer: channel.peer, endpoint: channel.endpoint, mode: $0) } }
                    nextRefresh = .distantPast
                }
                await lifecycle.release(permit); return
            }
            self.identity = identity; generation += 1; let run = generation
            if !value.clipboardPeerModes.isEmpty { try await refresh(seconds: 3) }
            try Task.checkCancellation()
            let queue = ClipboardPropagationQueue(propagate: { [weak self] item in try await self?.propagate(item, run: run) }, failure: { [failure] error in await failure(error.localizedDescription) })
            self.queue = queue; enabled = true
            // Snapshot this queue, never the mutable property of a later lifetime.
            await MainActor.run { [capture] in capture.onCaptured = { item in Task { await queue.enqueue(item) } } }
            await lifecycle.release(permit)
        } catch { await disableOwned(); await lifecycle.release(permit); throw error }
    }
    public func receiver() -> Receiver? {
        guard enabled else { return nil }; let run = generation
        return { [weak self] envelope in
            guard let self else { throw ClipboardReceiveError.unavailable }
            try await receive(envelope, run: run)
        }
    }
    private func receive(_ envelope: ClipboardEnvelope, run: Int) async throws {
        let permit = try await lifecycle.acquire()
        do {
            guard enabled, !disposed, run == generation, let identity else { throw ClipboardReceiveError.unavailable }
            if envelope.originDeviceId == identity.deviceID { await lifecycle.release(permit); return }
            guard !(await capture.isPaused) else { throw ClipboardReceiveError.paused }
            try ClipboardEnvelopePolicy.validate(envelope)
            if await receipts.accept(envelope) {
                do { try await capture.importRemote(envelope) }
                catch { await receipts.remove(envelope); throw error }
            }
            await lifecycle.release(permit)
        } catch { await lifecycle.release(permit); throw error }
    }
    public func sendManual(peer: DropLinkPeer, endpoint: URL, item: DropItem) async throws -> ClipboardSyncResponse {
        guard enabled, !disposed, let identity else { throw ClipboardReceiveError.unavailable }
        let run = generation
        guard let envelope = try await makeEnvelope(item, identity: identity, maximum: ClipboardEnvelopePolicy.hardImageBytes) else { throw DropSpaceError.invalid("Unsupported clipboard payload") }
        try Task.checkCancellation()
        guard enabled, generation == run else { throw ClipboardReceiveError.unavailable }
        guard await receipts.accept(envelope) else { return .init(accepted: false, errorCategory: "duplicate-loop-guard") }
        do {
            let response = try await DropLinkClipboardClient(identity: identity, peers: peers).send(peer: peer, endpoint: endpoint, envelope: envelope)
            if !response.accepted { await receipts.remove(envelope) }; return response
        } catch { await receipts.remove(envelope); throw error }
    }
    private func propagate(_ item: DropItem, run: Int) async throws {
        guard enabled, generation == run, let identity, !(await capture.isPaused) else { return }
        if !settings.clipboardPeerModes.isEmpty, Date() >= nextRefresh {
            nextRefresh = Date().addingTimeInterval(30)
            try await refresh(seconds: 2)
        }
        try Task.checkCancellation()
        guard enabled, generation == run, !(await capture.isPaused), let envelope = try await makeEnvelope(item, identity: identity, maximum: ClipboardEnvelopePolicy.automaticImageBytes) else { return }
        try Task.checkCancellation()
        guard enabled, generation == run, !(await capture.isPaused), await receipts.accept(envelope) else { return }
        let selected = Array(channels.values)
        for channel in selected {
            try Task.checkCancellation()
            guard enabled, generation == run, !(await capture.isPaused) else { return }
            guard channels[channel.peer.id] == channel, ClipboardEnvelopePolicy.allowedAutomatically(envelope, mode: channel.mode) else { continue }
            do {
                let response = try await DropLinkClipboardClient(identity: identity, peers: peers).send(peer: channel.peer, endpoint: channel.endpoint, envelope: envelope)
                if !response.accepted { await failure("Clipboard sync to \(channel.peer.displayName): \(response.errorCategory ?? "not accepted")") }
            } catch is CancellationError { throw CancellationError() }
            catch { await failure("Clipboard sync to \(channel.peer.displayName): \(error.localizedDescription)") }
        }
    }
    private func mode(for id: UUID) -> ClipboardSyncMode? { settings.clipboardPeerModes.first { UUID(uuidString: $0.key) == id }?.value }
    private func refresh(seconds: TimeInterval) async throws {
        let found = try await discover(seconds), trusted = try await peers.peers()
        try Task.checkCancellation()
        channels.removeAll()
        for peer in trusted where peer.trustState == .trusted {
            if let mode = mode(for: peer.id), let device = found.first(where: { $0.id == peer.id && $0.identityFingerprint.lowercased() == peer.identityFingerprint.lowercased() }) {
                channels[peer.id] = Channel(peer: peer, endpoint: device.endpoint, mode: mode)
            }
        }
    }
    private func makeEnvelope(_ item: DropItem, identity: DropLinkIdentity, maximum: Int) async throws -> ClipboardEnvelope? {
        guard sequence < Int64.max else { throw DropSpaceError.limit("Clipboard origin sequence exhausted") }; sequence += 1; let current = sequence
        if let text = item.text {
            let payload = try item.kind == .url ? HandoffMessagePolicy.normalizeURL(text.trimmingCharacters(in: .whitespacesAndNewlines)) : text
            return try ClipboardEnvelopePolicy.text(origin: identity.deviceID, sequence: current, text: payload, kind: item.kind == .url ? .url : .text)
        }
        guard item.kind == .image, let path = item.payloadPath else { return nil }
        let url = try await payloads.url(path)
        let owned = Task.detached(priority: .utility) {
            let file = try FileHandle(forReadingFrom: url); defer { try? file.close() }
            let size = try file.seekToEnd(); try file.seek(toOffset: 0)
            guard size <= maximum else { throw DropSpaceError.limit("Clipboard image exceeds the transfer read budget") }
            var data = Data()
            while let bytes = try file.read(upToCount: 81920), !bytes.isEmpty {
                try Task.checkCancellation()
                guard data.count <= maximum - bytes.count else { throw DropSpaceError.limit("Clipboard image exceeds the transfer read budget") }; data.append(bytes)
            }
            return data
        }
        let bytes = try await withTaskCancellationHandler { try await owned.value } onCancel: { owned.cancel() }
        try Task.checkCancellation()
        let mime = UTType(filenameExtension: url.pathExtension)?.preferredMIMEType ?? "image/unknown"
        return try ClipboardEnvelopePolicy.image(origin: identity.deviceID, sequence: current, bytes: bytes, mime: mime)
    }
    private func disableOwned() async {
        enabled = false; generation += 1
        await MainActor.run { [capture] in capture.onCaptured = nil }
        let owned = queue; queue = nil; await owned?.stop()
        identity = nil; channels.removeAll()
    }
    public func shutdown() async {
        guard let permit = try? await lifecycle.acquire() else { return }
        disposed = true; await disableOwned(); await receipts.clear(); await lifecycle.release(permit)
    }
}
