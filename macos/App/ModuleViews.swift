import SwiftUI
import DropSpaceCore
import DropSpaceInfrastructure

struct ModuleInventoryView: View {
    let model: WorkspaceModel
    let modules: NativeModules
    @State private var clear: String?
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Button(model.ui("DlcRefresh")) { modules.refresh() }.disabled(!modules.busy.isEmpty).accessibilityIdentifier("modules-refresh")
            ForEach(modules.snapshots.filter { $0.installation.id != "dropspace.sample" }, id: \.installation.id) { snapshot in card(snapshot) }
            if let error = modules.error { Text(model.ui("DlcOperationFailed") + ": " + error).foregroundStyle(.red).textSelection(.enabled) }
        }
        .confirmationDialog(model.ui("ModuleClearData"), isPresented: Binding(get: { clear != nil }, set: { if !$0 { clear = nil } })) {
            Button(model.ui("ModuleClearData"), role: .destructive) { if let id = clear { modules.clearData(id) }; clear = nil }
            Button(model.ui("CommonCancel"), role: .cancel) { clear = nil }
        } message: { Text(model.ui("ModuleClearDataConfirm")) }
    }
    private func card(_ snapshot: ModuleSnapshot) -> some View {
        let item = snapshot.installation, manifest = snapshot.manifest, package = modules.packages.first { $0.id == item.id }
        let canAct = modules.busy.isEmpty && item.transaction == .none
        let canEnable = modules.busy.isEmpty && [.none, .pendingCleanup].contains(item.transaction)
        return VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) { Image(systemName: manifest?.ui.icon == "Document" ? "doc" : manifest?.ui.icon == "Tools" ? "gearshape" : "puzzlepiece.extension"); Text(manifest.map { ModuleContract.text($0, $0.name, language: model.ui.language) } ?? item.id).font(.system(size: 16, weight: .semibold)) }
            if let version = item.version ?? package?.version { Text(version).foregroundStyle(.secondary) }
            if item.version != nil {
                Text(model.ui(item.enabled ? "ModuleEnabled" : "ModuleDisabled"))
                Text(model.ui(runKey(snapshot.state)))
            } else { Text(model.ui("DlcNotInstalled")) }
            if item.transaction != .none { Text(model.ui(transactionKey(item.transaction))) }
            if let code = item.errorCode { Text(model.ui("DlcOperationFailed") + ": " + code).foregroundStyle(.red) }
            VStack(alignment: .leading, spacing: 8) {
                if let package, item.version == nil || ModuleContract.version(item.version!)!.lexicographicallyPrecedes(ModuleContract.version(package.version)!) {
                    action("DlcDownload", item.id, enabled: canAct) { modules.install(item.id) }
                }
                if item.version != nil {
                    if !item.enabled || snapshot.state == .faulted { action(snapshot.state == .faulted ? "DlcRetry" : "ModuleEnable", item.id, enabled: canEnable) { modules.enable(item.id) } }
                    if let manifest, !manifest.ui.settings.isEmpty, snapshot.visible {
                        action("PageTitleSettings", item.id, enabled: canAct) { model.selectModule(manifest, page: nil) }
                    }
                }
                if item.enabled || snapshot.state != .stopped { action("ModuleDisable", item.id, enabled: !modules.retiring.contains(item.id)) { modules.disable(item.id) } }
                if item.version != nil || item.transaction != .none { action("DlcDelete", item.id, enabled: !modules.retiring.contains(item.id)) { modules.uninstall(item.id) } }
                if item.transaction == .pendingCleanup { action("ModuleRetryCleanup", item.id, enabled: modules.busy.isEmpty) { modules.cleanup(item.id) } }
                if item.dataVersion > 0 || item.errorCode == "ModuleStateInvalid" { action("ModuleClearData", item.id, enabled: canEnable && !item.enabled && snapshot.state == .stopped) { clear = item.id } }
            }
        }.padding(16).frame(maxWidth: .infinity, alignment: .leading).background(.quaternary, in: RoundedRectangle(cornerRadius: 12))
    }
    private func action(_ key: String, _ id: String, enabled: Bool, execute: @escaping () -> Void) -> some View {
        Button(model.ui(key), action: execute).disabled(!enabled).accessibilityIdentifier(key + "-" + id)
    }
    private func runKey(_ value: ModuleRunState) -> String {
        switch value { case .running: "ModuleRunning"; case .starting: "ModuleStarting"; case .stopping: "ModuleStopping"; case .stopped: "ModuleStopped"; case .faulted: "DlcOperationFailed" }
    }
    private func transactionKey(_ value: ModuleTransactionState) -> String {
        switch value { case .downloading: "DlcDownloading"; case .preparing: "DlcVerifying"; case .activating: "ModuleStarting"; case .pendingCleanup: "ModulePendingCleanup"; default: "DlcRemoving" }
    }
}

struct NativeModulePage: View {
    let model: WorkspaceModel
    let modules: NativeModules
    let manifest: ModuleManifest
    let page: ModulePage?
    @State private var values: [String: String] = [:]
    @State private var resultKey: String?
    @State private var failed = false
    @State private var busy = false
    @State private var operation: Task<Void, Never>?
    @State private var generation = UUID()
    private func text(_ value: ModuleText) -> String { ModuleContract.text(manifest, value, language: model.ui.language) }
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Button(model.ui("CommonClose"), action: model.closeModule)
                Text(text(page?.title ?? manifest.name)).font(.system(size: 28, weight: .semibold))
                if let page { Text(text(page.description)).foregroundStyle(.secondary) }
                if let page, !page.actions.isEmpty {
                    SettingsCard {
                        VStack(alignment: .leading, spacing: 8) {
                            ForEach(page.actions, id: \.id) { action in
                                Button(text(action.label)) { perform { runtime in
                                    let reply = try await runtime.invoke(manifest.id, action: action.id)
                                    return reply.result["messageKey"]?.string.flatMap { manifest.resources["en-US"]?[$0] != nil ? $0 : nil }
                                } }.disabled(busy).accessibilityIdentifier("ModuleAction-" + manifest.id + "-" + action.id)
                            }
                        }
                    }
                }
                if !manifest.ui.settings.isEmpty {
                    SettingsCard {
                        VStack(alignment: .leading, spacing: 14) {
                            ForEach(manifest.ui.settings, id: \.id) { setting in
                                if setting.kind == "boolean" {
                                    Toggle(text(setting.label), isOn: Binding(get: { (values[setting.id] ?? setting.defaultValue) == "true" }, set: { values[setting.id] = $0 ? "true" : "false" })).toggleStyle(.switch)
                                        .accessibilityIdentifier("ModuleSetting-" + manifest.id + "-" + setting.id)
                                } else {
                                    TextField(text(setting.label), text: Binding(get: { values[setting.id] ?? setting.defaultValue }, set: { values[setting.id] = String($0.prefix(4096)) }))
                                        .accessibilityIdentifier("ModuleSetting-" + manifest.id + "-" + setting.id)
                                }
                            }
                            Button(model.ui("ModuleSave")) {
                                let current = values
                                perform { runtime in
                                    for setting in manifest.ui.settings { try Task.checkCancellation(); try await runtime.writeSetting(manifest.id, key: setting.id, value: current[setting.id] ?? setting.defaultValue) }
                                    return nil
                                }
                            }.disabled(busy).accessibilityIdentifier("ModuleSave-" + manifest.id)
                        }
                    }
                }
                if failed { Text(model.ui("DlcOperationFailed")).foregroundStyle(.red) }
                if let resultKey { Text(text(.init(key: resultKey))).textSelection(.enabled).accessibilityIdentifier("ModuleResult-" + manifest.id) }
            }.frame(maxWidth: 780, alignment: .leading).padding(24).frame(maxWidth: .infinity, alignment: .leading)
        }
        .task {
            do { values = try await modules.runtime.readSettings(manifest.id) } catch is CancellationError {} catch { failed = true }
        }
        .onDisappear { generation = UUID(); operation?.cancel(); operation = nil }
    }
    private func perform(_ execute: @escaping @MainActor (FeatureModuleRuntime) async throws -> String?) {
        guard !busy else { return }; busy = true; failed = false; resultKey = nil; let run = generation
        operation = Task {
            defer { if generation == run { busy = false; operation = nil } }
            do { let key = try await execute(modules.runtime); try Task.checkCancellation(); if generation == run { resultKey = key } }
            catch is CancellationError {} catch { if generation == run { failed = true } }
        }
    }
}

struct ModuleIslandStrip: View {
    let model: WorkspaceModel
    let modules: NativeModules
    var body: some View {
        ScrollView(.horizontal) {
            HStack(spacing: 8) { ForEach(modules.contributions, id: \.installation.id) { snapshot in
                if let manifest = snapshot.manifest, let content = snapshot.island { ModuleIslandChip(model: model, modules: modules, manifest: manifest, content: content).id(content.expiresAt) }
            } }
        }.scrollIndicators(.hidden).frame(maxHeight: 24).padding(.horizontal, 22).padding(.bottom, 2)
    }
}
private struct ModuleIslandChip: View {
    let model: WorkspaceModel, modules: NativeModules, manifest: ModuleManifest, content: ModuleIslandContent
    @State private var open = false
    @State private var busy = false
    @State private var failed = false
    @State private var operation: Task<Void, Never>?
    private func text(_ value: ModuleText) -> String { ModuleContract.text(manifest, value, language: model.ui.language) }
    var body: some View {
        Button(text(content.expanded)) { open = true }.font(.system(size: 11)).lineLimit(1).frame(maxWidth: 300)
            .accessibilityLabel(text(manifest.name) + ": " + text(content.expanded))
            .popover(isPresented: $open) {
                VStack(alignment: .leading, spacing: 10) {
                    Text(text(manifest.name)).fontWeight(.semibold); Text(text(content.expanded))
                    if failed { Text(model.ui("DlcOperationFailed")).foregroundStyle(.red) }
                    ForEach(content.actions, id: \.id) { action in
                        Button(text(action.label)) {
                            guard content.expiresAt > Date(), !busy else { return }; busy = true
                            operation = Task {
                                defer { busy = false }
                                do { _ = try await modules.runtime.invoke(manifest.id, action: action.id) }
                                catch is CancellationError {} catch { if !Task.isCancelled { failed = true } }
                            }
                        }.disabled(busy)
                    }
                }.padding(16).frame(maxWidth: 360)
            }.onDisappear { open = false; operation?.cancel(); operation = nil }
    }
}
