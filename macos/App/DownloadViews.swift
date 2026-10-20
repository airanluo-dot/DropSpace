import SwiftUI
import AppKit
import DropSpaceCore

struct DownloadHistoryView: View {
    let model: WorkspaceModel
    @State private var source = ""
    @State private var directory = ""
    @State private var name = ""
    @State private var history: [DownloadRecord] = []
    @State private var historyLimit = 50
    @State private var busy = Set<String>()
    @State private var failed = false
    private var defaultDirectory: String { downloadDirectory(model) }
    var body: some View {
        VStack(spacing: 18) {
            DownloadCard {
                Text(model.ui("DownloadCustom")).font(.system(size: 18, weight: .semibold))
                DownloadField(title: model.ui("DownloadUrl")) { TextField(model.ui("DownloadUrl"), text: $source).labelsHidden().accessibilityIdentifier("DownloadUrl") }
                DownloadField(title: model.ui("DownloadSaveTo")) { DownloadFolderField(title: model.ui("DownloadSaveTo"), id: "DownloadSaveTo", path: $directory) }
                DownloadField(title: model.ui("DownloadFileName")) { TextField(model.ui("DownloadAutoName"), text: $name).accessibilityIdentifier("DownloadFileName") }
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 8) { start; openFolder(directory) }
                    VStack(alignment: .leading, spacing: 8) { start; openFolder(directory) }
                }
                if failed { Text(model.ui("DownloadError")).foregroundStyle(.red).textSelection(.enabled) }
                if !history.isEmpty {
                    ScrollView { LazyVStack(spacing: 10) { ForEach(DownloadHistory.visible(history, limit: historyLimit)) { task($0) } } }
                        .frame(maxHeight: 600).fixedSize(horizontal: false, vertical: true)
                }
                Button(model.ui("DownloadMoreHistory")) { historyLimit += 50 }.accessibilityIdentifier("DownloadMoreHistory")
            }
            DownloadPreferencesView(model: model)
        }.onAppear { if directory.isEmpty { directory = defaultDirectory } }
            .onChange(of: defaultDirectory) { previous, value in if directory == previous { directory = value } }
            .task {
                while !Task.isCancelled {
                    history = await model.downloads?.history() ?? []
                    do { try await Task.sleep(for: .milliseconds(250)) } catch { return }
                }
            }
    }
    private var start: some View {
        Button { change("start") {
            guard let manager = model.downloads, let url = URL(string: source.trimmingCharacters(in: .whitespacesAndNewlines)), directory.hasPrefix("/") else { throw DropSpaceError.invalid("Download request") }
            _ = try await manager.add(source: url, directory: URL(fileURLWithPath: directory), name: name.isEmpty ? nil : name)
            source = ""; name = ""; directory = defaultDirectory
        } } label: { Label(model.ui("DownloadStart"), systemImage: "arrow.down") }
            .disabled(busy.contains("start")).accessibilityIdentifier("DownloadStart")
    }
    private func task(_ record: DownloadRecord) -> some View {
        DownloadCard {
            Text(record.destination.lastPathComponent).fontWeight(.semibold).textSelection(.enabled)
            Text(model.ui(stateKey(record.phase)) + " · " + detail(record)).font(.callout).foregroundStyle(.secondary)
            if let total = record.total, total > 0 { ProgressView(value: min(Double(total), Double(record.received)), total: Double(total)).accessibilityLabel(record.destination.lastPathComponent) }
            else if [.queued, .connecting, .downloading].contains(record.phase) { ProgressView().controlSize(.small).accessibilityLabel(record.destination.lastPathComponent) }
            if record.error != nil || record.cleanupError != nil { Text(model.ui("DownloadError")).foregroundStyle(.red).font(.callout) }
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 8) { actions(record) }
                VStack(alignment: .leading, spacing: 8) { actions(record) }
            }
        }
    }
    @ViewBuilder private func actions(_ record: DownloadRecord) -> some View {
        let id = record.id.uuidString
        switch record.phase {
        case .queued, .connecting, .downloading, .verifying:
            action("DownloadPause", id, "pause") { try await model.downloads?.pause(record.id) }
        case .paused, .failed:
            action(record.phase == .paused ? "DownloadResume" : "DownloadRetry", id, "play") { try await model.downloads?.resume(record.id) }
        default: EmptyView()
        }
        if [.completed, .cancelled].contains(record.phase) { action("DownloadRemoveHistory", id, "trash") { try await model.downloads?.removeHistory(record.id) } }
        else { action("DownloadCancel", id, "xmark") { try await model.downloads?.cancel(record.id) } }
        openFolder(record.destination.deletingLastPathComponent().path)
        if record.cleanupPending == true { action("ModuleRetryCleanup", id, "arrow.clockwise") { try await model.downloads?.retryCleanup(record.id) } }
    }
    private func action(_ key: String, _ id: String, _ icon: String, execute: @escaping @MainActor () async throws -> Void) -> some View {
        Button { change(id, execute) } label: { Label(model.ui(key), systemImage: icon) }.disabled(busy.contains(id)).accessibilityIdentifier(key + "-" + id)
    }
    private func change(_ id: String, _ action: @escaping @MainActor () async throws -> Void) {
        guard busy.insert(id).inserted else { return }; failed = false
        Task { defer { busy.remove(id) }; do { try await action(); history = await model.downloads?.history() ?? [] } catch { failed = true } }
    }
    private func detail(_ item: DownloadRecord) -> String {
        let formatter = NumberFormatter(); formatter.locale = Locale(identifier: model.ui.language); formatter.minimumFractionDigits = 2; formatter.maximumFractionDigits = 2
        func mib(_ bytes: Int64) -> String { formatter.string(from: NSNumber(value: Double(bytes) / 1_048_576)) ?? "0" }
        let speed = formatter.string(from: NSNumber(value: (item.bytesPerSecond ?? 0) / 1_048_576)) ?? "0"
        return mib(item.received) + " / " + (item.total.map { mib($0) + " MiB" } ?? model.ui("DownloadUnknownSize")) + " · " + speed + " MiB/s · " + model.ui("DownloadActiveConnections", String(item.activeConnections ?? 0))
    }
    private func stateKey(_ value: DownloadPhase) -> String {
        let suffix = switch value { case .queued: "Queued"; case .connecting: "Resolving"; case .downloading: "DownloadingFile"; case .verifying: "Finalizing"; case .paused: "Paused"; case .completed: "Completed"; case .cancelled: "Cancelled"; case .failed: "Failed" }
        return "DownloadState" + suffix
    }
    private func openFolder(_ path: String) -> some View {
        Button { if path.hasPrefix("/") { NSWorkspace.shared.open(URL(fileURLWithPath: path)) } else { failed = true } } label: { Label(model.ui("DownloadOpenFolder"), systemImage: "folder") }
    }
}

private struct DownloadPreferencesView: View {
    let model: WorkspaceModel
    @State private var directory = ""
    @State private var dirty = false
    @State private var revision = 0
    private var limits: DownloadLimitEditor { model.downloadLimits }
    var body: some View {
        DownloadCard {
            Text(model.ui("DownloadSettings")).font(.system(size: 18, weight: .semibold))
            DownloadField(title: model.ui("DownloadConcurrentCount")) {
                Picker(model.ui("DownloadConcurrentCount"), selection: Binding(get: { limits.concurrent }, set: { limits.queue(concurrent: $0) })) { ForEach(1...3, id: \.self) { Text(String($0)).tag($0) } }
                    .labelsHidden().frame(width: 100).accessibilityIdentifier("DownloadConcurrentCount")
            }
            DownloadLimitRow(title: model.ui("DownloadConnections"), identifier: "DownloadConnections", value: Binding(get: { limits.connections }, set: { limits.queue(connections: $0) }), range: 1...256, editor: limits)
            Text(model.ui("DownloadConnectionsHint")).font(.callout).foregroundStyle(.secondary)
            DownloadLimitRow(title: model.ui("DownloadSpeed"), identifier: "DownloadSpeed", value: Binding(get: { limits.speed }, set: { limits.queue(speed: $0) }), range: 0...1024, editor: limits)
            Text(limits.speed == 0 ? model.ui("DownloadUnlimited") : "\(limits.speed.formatted(.number.locale(Locale(identifier: model.ui.language)))) MiB/s").foregroundStyle(.secondary)
            DownloadField(title: model.ui("DownloadDefaultDirectory")) {
                DownloadFolderField(title: model.ui("DownloadDefaultDirectory"), id: "DownloadDefaultDirectory",
                    path: Binding(get: { directory }, set: { directory = $0; dirty = true; revision += 1 }), commit: saveDirectory)
            }
        }.onAppear { directory = downloadDirectory(model); limits.refresh() }
            .onChange(of: model.settings) { _, _ in limits.refresh(); if !dirty { directory = downloadDirectory(model) } }
            .onDisappear { saveDirectory(); limits.commit() }
    }
    private func saveDirectory() {
        guard dirty else { return }; dirty = false; let version = revision, path = directory.trimmingCharacters(in: .whitespacesAndNewlines)
        let save = model.update { $0.defaultDownloadDirectory = path }
        Task { await save.value; if revision == version && !dirty { directory = downloadDirectory(model) } }
    }
}
private struct DownloadLimitRow: View {
    let title: String, identifier: String
    @Binding var value: Int
    let range: ClosedRange<Int>, editor: DownloadLimitEditor
    @FocusState private var focused: Bool
    var body: some View {
        DownloadField(title: title) {
            HStack(spacing: 12) {
                Slider(value: Binding(get: { Double(value) }, set: { if $0.isFinite { value = min(range.upperBound, max(range.lowerBound, Int($0.rounded(.toNearestOrEven)))) } }),
                       in: Double(range.lowerBound)...Double(range.upperBound), step: 1, onEditingChanged: editor.interaction)
                    .accessibilityLabel(title).accessibilityIdentifier(identifier + "Slider")
                TextField(title, value: $value, format: .number).labelsHidden().frame(width: 100).focused($focused).onSubmit(editor.commit).accessibilityIdentifier(identifier)
            }
        }.onChange(of: focused) { _, value in if !value { editor.commit() } }
    }
}
private struct DownloadFolderField: View {
    let title: String, id: String
    @Binding var path: String
    var commit: () -> Void = {}
    @FocusState private var focused: Bool
    @Environment(\.locale) private var locale
    var body: some View {
        HStack(spacing: 8) {
            TextField(title, text: $path).labelsHidden().focused($focused).onSubmit(commit).accessibilityIdentifier(id)
            Button(Localizer(Localizer.match([locale.identifier]))("DownloadChoose")) {
                let panel = NSOpenPanel(); panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.canCreateDirectories = true
                if path.hasPrefix("/") { panel.directoryURL = URL(fileURLWithPath: path) }
                let complete: (NSApplication.ModalResponse) -> Void = { response in if response == .OK, let url = panel.url { path = url.path; commit() } }
                if let window = NSApp.keyWindow { panel.beginSheetModal(for: window, completionHandler: complete) } else { panel.begin(completionHandler: complete) }
            }.accessibilityIdentifier(id + "Choose")
        }.onChange(of: focused) { _, value in if !value { commit() } }
    }
}
private struct DownloadField<Content: View>: View {
    let title: String
    @ViewBuilder var content: () -> Content
    var body: some View { VStack(alignment: .leading, spacing: 4) { Text(title); content() } }
}
private struct DownloadCard<Content: View>: View {
    @ViewBuilder var content: () -> Content
    var body: some View {
        VStack(alignment: .leading, spacing: 12, content: content).padding(16).frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 8))
            .overlay { RoundedRectangle(cornerRadius: 8).stroke(.primary.opacity(0.08), lineWidth: 1) }
    }
}
@MainActor private func downloadDirectory(_ model: WorkspaceModel) -> String {
    model.settings.defaultDownloadDirectory.isEmpty ? FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0].path : model.settings.defaultDownloadDirectory
}
