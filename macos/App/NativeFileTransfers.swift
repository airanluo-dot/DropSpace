import SwiftUI
import Observation
import DropSpaceCore
import DropSpaceInfrastructure

@MainActor @Observable final class NativeFileTransfers {
    enum Stage { case preparing, waiting, sending, finalizing, cancelling, completed, cancelled, unknownCancellation, failed }
    struct Entry: Identifiable {
        let id: UUID, name: String, peerName: String
        var stage: Stage = .preparing
        var bytes: Int64 = 0, total: Int64 = 0
        var settled = false, mayHaveReachedPeer = false
        var messageKey: String?, error: String?, completedPaths: [String] = []
        var canCancel: Bool { !settled && ![.finalizing, .cancelling].contains(stage) }
        var stageKey: String {
            if let messageKey { return messageKey }
            switch stage {
            case .preparing: return "FileTransferPreparing"
            case .waiting: return "FileTransferWaitingForApproval"
            case .sending: return "FileTransferSending"
            case .finalizing: return "FileTransferFinalizing"
            case .cancelling: return "FileTransferCanceling"
            case .completed: return "TransferSentTitle"
            case .cancelled: return "FileTransferCanceled"
            case .unknownCancellation: return "FileTransferCanceledOutcomeUnknown"
            case .failed: return "FileTransferFailed"
            }
        }
    }
    private struct Context {
        let client: DropLinkTransferClient, peer: DropLinkPeer, endpoint: URL, sources: [URL]
        var prepared: PreparedTransfer?
    }
    private(set) var entries: [Entry] = []
    var showsPanel = false
    private var jobs: [UUID: Task<Void, Never>] = [:]
    private var contexts: [UUID: Context] = [:]
    var activeCount: Int { entries.filter { !$0.settled }.count }
    func start(client: DropLinkTransferClient, peer: DropLinkPeer, endpoint: URL, sources: [URL], name: String) {
        guard activeCount < 2 else { return }
        let id = UUID(); contexts[id] = .init(client: client, peer: peer, endpoint: endpoint, sources: sources)
        entries.insert(.init(id: id, name: name, peerName: peer.displayName), at: 0); showsPanel = true
        jobs[id] = Task { await execute(id, resume: false) }
    }
    func canResume(_ id: UUID) -> Bool { entries.contains { $0.id == id && $0.settled && $0.stage == .failed } && contexts[id]?.prepared != nil }
    func resume(_ id: UUID) {
        guard canResume(id), activeCount < 2 else { return }
        update(id) { $0.settled = false; $0.stage = .waiting; $0.error = nil; $0.messageKey = nil }
        jobs[id] = Task { await execute(id, resume: true) }
    }
    func cancel(_ id: UUID) {
        guard entries.first(where: { $0.id == id })?.canCancel == true else { return }
        update(id) { $0.stage = .cancelling }; jobs[id]?.cancel()
    }
    func cancelAll() { for entry in entries where entry.canCancel { cancel(entry.id) } }
    func shutdown() async {
        let owned = Array(jobs.values); for task in owned { task.cancel() }; for task in owned { await task.value }
    }
    func clearFinished() {
        let ids = Set(entries.filter(\.settled).map(\.id)); entries.removeAll { ids.contains($0.id) }
        for id in ids { contexts[id] = nil }
    }
    private func execute(_ id: UUID, resume: Bool) async {
        guard var context = contexts[id] else { return }
        defer {
            update(id) { $0.settled = true }; jobs[id] = nil
            let excess = Set(entries.filter(\.settled).dropFirst(10).map(\.id)); entries.removeAll { excess.contains($0.id) }
            for id in excess { contexts[id] = nil }
        }
        do {
            if context.prepared == nil {
                context.prepared = try await TransferSourcePreparation().prepare(context.sources, filenames: context.peer.platform == .windows ? .windows : .macOS)
                contexts[id] = context
            }
            guard let prepared = context.prepared else { throw DropSpaceError.invalid("Transfer preparation") }
            let result = try await context.client.send(peer: context.peer, endpoint: context.endpoint, prepared: prepared, resume: resume) { [weak self] progress in await self?.progress(id, progress) }
            update(id) {
                $0.completedPaths = result.completedRelativePaths
                $0.stage = result.state == .completed ? .completed : result.state == .cancelled ? .cancelled : .failed
                if result.state == .rejected { $0.messageKey = "FileTransferRejected" }
                else if result.errorCategory == "approval-timeout" { $0.messageKey = "FileTransferApprovalTimeout" }
                else if ![.completed, .cancelled].contains(result.state) { $0.error = result.errorCategory }
            }
        } catch is CancellationError {
            update(id) { $0.stage = $0.mayHaveReachedPeer ? .unknownCancellation : .cancelled }
        } catch {
            update(id) { $0.stage = .failed; $0.error = error.localizedDescription }
        }
    }
    private func progress(_ id: UUID, _ value: DropLinkTransferProgress) {
        update(id) {
            $0.bytes = value.transferredBytes; $0.total = value.totalBytes
            if value.stage != .preparing { $0.mayHaveReachedPeer = true }
            guard $0.stage != .cancelling else { return }
            switch value.stage { case .preparing: $0.stage = .preparing; case .waitingForApproval: $0.stage = .waiting; case .transferring: $0.stage = .sending; case .finalizing: $0.stage = .finalizing }
        }
    }
    private func update(_ id: UUID, _ mutation: (inout Entry) -> Void) { if let index = entries.firstIndex(where: { $0.id == id }) { mutation(&entries[index]) } }
}

struct FileTransfersButton: View {
    @Bindable var devices: NativeDevices
    let ui: Localizer
    var body: some View {
        @Bindable var transfers = devices.fileTransfers
        Button { transfers.showsPanel.toggle() } label: {
            Label(ui("FileTransfersTitle") + (transfers.activeCount == 0 ? "" : " (\(transfers.activeCount))"), systemImage: "arrow.left.arrow.right")
        }.accessibilityIdentifier("FileTransfersButton")
            .popover(isPresented: $transfers.showsPanel) {
                VStack(alignment: .leading, spacing: 12) {
                    Text(ui("FileTransfersTitle")).font(.headline)
                    Text(ui("FileTransfersContinue")).font(.caption).foregroundStyle(.secondary)
                    if transfers.entries.isEmpty { Text(ui("FileTransfersEmpty")).foregroundStyle(.secondary) }
                    ScrollView {
                        VStack(alignment: .leading, spacing: 16) {
                            ForEach(transfers.entries) { entry in
                                VStack(alignment: .leading, spacing: 6) {
                                    Text(entry.name).fontWeight(.semibold)
                                    Text(ui("FileTransferTarget", entry.peerName)).foregroundStyle(.secondary)
                                    Text(ui(entry.stageKey)).accessibilityIdentifier("FileTransferStage-" + entry.id.dropLinkN)
                                    if entry.total > 0 { ProgressView(value: Double(entry.bytes), total: Double(entry.total)); Text(ui("FileTransferByteProgress", ByteCountFormatter.string(fromByteCount: entry.bytes, countStyle: .file), ByteCountFormatter.string(fromByteCount: entry.total, countStyle: .file))).font(.caption) }
                                    if let error = entry.error { Text(error).foregroundStyle(.red).textSelection(.enabled) }
                                    if !entry.completedPaths.isEmpty { Text(entry.completedPaths.joined(separator: "\n")).font(.caption).textSelection(.enabled) }
                                    if entry.canCancel { Button(ui("CommonCancel")) { transfers.cancel(entry.id) }.accessibilityLabel(ui("FileTransferCancelAutomationName", entry.name, entry.peerName)) }
                                    if devices.enabled && transfers.canResume(entry.id) { Button(ui("DownloadResume")) { transfers.resume(entry.id) }.disabled(transfers.activeCount + devices.sending >= 2) }
                                }.frame(maxWidth: .infinity, alignment: .leading)
                                Divider()
                            }
                        }
                    }.frame(maxHeight: 440)
                    Button(ui("FileTransfersClearCompleted")) { transfers.clearFinished() }.disabled(!transfers.entries.contains(where: \.settled))
                }.padding(20).frame(width: 380)
            }
    }
}
