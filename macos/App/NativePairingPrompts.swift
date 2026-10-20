import AppKit
import SwiftUI
import DropSpaceCore

/// One owned native confirmation at a time. Cancellation removes queued prompts
/// and closes the active panel before resuming its caller.
@MainActor final class NativePairingPrompts: NSObject, NSWindowDelegate {
    private struct Entry {
        let id: UUID; let title: String; let content: String; let confirm: String; let cancel: String
        let choices: [DropLinkPeer]; let identifier: String
        let continuation: CheckedContinuation<UUID?, any Error>
    }
    var language = "system"
    private var entries: [Entry] = []
    private var panel: NSPanel?
    func ask(_ offer: PairingOffer, incoming: Bool) async throws -> Bool {
        let ui = Localizer(language), remoteName = incoming ? offer.remoteHello.displayName : offer.localHello.displayName
        return try await enqueue(title: ui("PairingSasTitleWithDevice", remoteName), content: ui("PairingSasContent", String(format: "%06d", offer.sas)), confirm: ui("PairingSasConfirm"), cancel: ui("CommonCancel"), identifier: "DropLinkPairingConfirmation") != nil
    }
    func receive(_ message: HandoffMessage) async throws -> Bool {
        let ui = Localizer(language)
        let preview = String(decoding: message.utf8Payload.utf16.prefix(600), as: UTF16.self) + (message.utf8Payload.utf16.count > 600 ? "…" : "")
        return try await enqueue(title: ui("IncomingHandoffTitle"), content: ui("IncomingHandoffContent", message.senderDisplayName, ui(message.kind == HandoffMessageKind.url.rawValue ? "HandoffUrlKind" : "HandoffTextKind"), preview, String(message.byteLength)), confirm: ui("IncomingTransferAccept"), cancel: ui("IncomingTransferReject"), identifier: "DropLinkHandoffConfirmation") != nil
    }
    func choose(_ peers: [DropLinkPeer]) async throws -> UUID? {
        let ui = Localizer(language)
        return try await enqueue(title: ui("SelectDeviceTitle"), content: "", confirm: ui("SendToDeviceAction.Text"), cancel: ui("CommonCancel"), identifier: "DropLinkHandoffDevice", choices: peers)
    }
    func receiveFiles(_ manifest: TransferManifest) async throws -> Bool {
        let ui = Localizer(language)
        let content = ui("IncomingTransferContent", String(manifest.items.count), String(manifest.totalBytes)).replacingOccurrences(of: "Downloads\\DropSpace", with: "Downloads/DropSpace")
        let names = manifest.items.map(\.relativePath).joined(separator: "\n")
        return try await enqueue(title: ui("IncomingTransferTitle"), content: content + "\n\n" + names,
                                 confirm: ui("IncomingTransferAccept"), cancel: ui("IncomingTransferReject"), identifier: "DropLinkFileConfirmation") != nil
    }
    func inform(title: String, content: String) async throws {
        let ui = Localizer(language)
        _ = try await enqueue(title: title, content: content, confirm: ui("CommonClose"), cancel: "", identifier: "DropLinkResult")
    }
    private func enqueue(title: String, content: String, confirm: String, cancel: String, identifier: String, choices: [DropLinkPeer] = []) async throws -> UUID? {
        try Task.checkCancellation()
        guard entries.count < 16 else { throw DropSpaceError.limit("Device confirmation queue") }
        let id = UUID()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                entries.append(.init(id: id, title: title, content: content, confirm: confirm, cancel: cancel, choices: choices, identifier: identifier, continuation: continuation))
                present()
            }
        } onCancel: { Task { @MainActor [weak self] in self?.finish(id, result: .failure(CancellationError())) } }
    }
    func stop() {
        let pending = entries; entries.removeAll()
        panel?.orderOut(nil); panel?.delegate = nil; panel?.close(); panel = nil
        for entry in pending { entry.continuation.resume(throwing: CancellationError()) }
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if let first = entries.first { finish(first.id, result: .success(nil)) }; return false
    }
    private func present() {
        guard panel == nil, let first = entries.first else { return }
        let panel = NSPanel(contentRect: .init(x: 0, y: 0, width: 440, height: 250), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        panel.title = first.title; panel.isReleasedWhenClosed = false
        panel.delegate = self; panel.identifier = .init(first.identifier)
        panel.contentView = NSHostingView(rootView: PairingConfirmationView(title: panel.title, content: first.content, confirm: first.confirm, cancel: first.cancel, choices: first.choices, acceptance: first.id, identifier: first.identifier) { [weak self] accepted in
            self?.finish(first.id, result: .success(accepted))
        })
        self.panel = panel; panel.center(); NSApp.activate(ignoringOtherApps: true); panel.makeKeyAndOrderFront(nil)
    }
    private func finish(_ id: UUID, result: Result<UUID?, any Error>) {
        guard let index = entries.firstIndex(where: { $0.id == id }) else { return }
        let entry = entries.remove(at: index)
        if index == 0 { panel?.orderOut(nil); panel?.delegate = nil; panel?.close(); panel = nil }
        entry.continuation.resume(with: result); present()
    }
}
private struct PairingConfirmationView: View {
    let title: String, content: String, confirm: String, cancel: String
    let choices: [DropLinkPeer]; let acceptance: UUID; let identifier: String
    let decide: (UUID?) -> Void
    @State private var selected: UUID?
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text(title).font(.title2.weight(.semibold))
            if !content.isEmpty {
                ScrollView { Text(content).textSelection(.enabled).fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading) }
                    .frame(maxHeight: content.utf16.count > 600 ? 420 : 150).accessibilityIdentifier(identifier + "Content")
            }
            if !choices.isEmpty {
                Picker(title, selection: Binding(get: { selected ?? choices[0].id }, set: { selected = $0 })) {
                    ForEach(choices) { peer in Text(peer.displayName).tag(peer.id) }
                }.labelsHidden().accessibilityIdentifier("HandoffPeerChoice")
            }
            HStack {
                Spacer()
                if !cancel.isEmpty { Button(cancel) { decide(nil) }.keyboardShortcut(.cancelAction).accessibilityIdentifier(identifier + "Cancel") }
                Button(confirm) { decide(choices.isEmpty ? acceptance : selected ?? choices[0].id) }.keyboardShortcut(.defaultAction).accessibilityIdentifier(identifier + "Confirm")
            }
        }.padding(24).frame(width: 440)
    }
}
