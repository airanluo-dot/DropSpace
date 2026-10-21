import SwiftUI
import AppKit
import ServiceManagement
import DropSpaceCore

struct SettingsView: View {
    @Bindable var model: WorkspaceModel
    private var section: String { model.settingsSection }
    private let categories = ["General","Island","Shortcuts","Widgets","DLC","Downloads","SystemActivities","Devices","Updates","About"]
    var body: some View {
        VStack(spacing: 0) {
            SettingsNavigation(model: model, categories: categories, selection: $model.settingsSection)
            Divider()
            if let error = model.error { Text(error).foregroundStyle(.red).textSelection(.enabled).padding(.horizontal, 24) }
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    switch section {
                    case "General": general
                    case "Island": island
                    case "Shortcuts": shortcuts
                    case "Widgets": WidgetEditor(model: model)
                    case "Downloads": downloads
                    case "SystemActivities": activities
                    case "Devices": devices
                    case "Updates": updates
                    case "DLC": components
                    default: about
                    }
                }.frame(maxWidth: 780, alignment: .leading).padding(24).frame(maxWidth: .infinity, alignment: .leading)
            }.id(section)
        }.environment(\.locale, Locale(identifier: model.ui.language))
    }
    private func binding<T>(_ key: WritableKeyPath<AppSettings,T>) -> Binding<T> { Binding(get: { model.settings[keyPath: key] },set: { value in model.update { $0[keyPath: key] = value } }) }
    private func toggle(_ title: String,_ key: WritableKeyPath<AppSettings,Bool>) -> some View { Toggle(title,isOn: binding(key)) }
    private func integer(_ title: String,_ key: WritableKeyPath<AppSettings,Int>,_ range: ClosedRange<Int>) -> some View { TextField(title,value: binding(key),format: .number).onSubmit { }.help("\(range.lowerBound)…\(range.upperBound)") }
    @ViewBuilder private var general: some View {
        SettingsGroup("Appearance & Language") {
            Picker("Appearance",selection: binding(\.theme)) { ForEach(Theme.allCases,id: \.self) { Text($0.rawValue.capitalized).tag($0) } }
            Picker("Display language",selection: binding(\.language)) {
                Text("Follow System").tag("system")
                ForEach(AppSettings.languages,id: \.self) { code in Text(Locale(identifier: code).localizedString(forIdentifier: code) ?? code).tag(code) }
            }
        }
        SettingsGroup("Lifecycle") {
            toggle("Open at login",\.startAtLogin)
            if SMAppService.mainApp.status == .requiresApproval { Button("Review Login Item in System Settings") { SMAppService.openSystemSettingsLoginItems() } }
            Picker("When closing the window",selection: binding(\.closeBehavior)) { Text("Keep running in menu bar").tag(CloseBehavior.hideToMenuBar); Text("Quit").tag(CloseBehavior.exit) }
            Picker("Launch page",selection: binding(\.launchPage)) { Text("Temporary Space").tag("space"); Text("Clipboard").tag("clipboard"); Text("Pinned").tag("pinned"); Text("Music").tag("music") }
        }
        SettingsGroup("Clipboard") {
            toggle("Pause recording",\.clipboardPaused); toggle("Capture images",\.captureImages); toggle("Capture files",\.captureFiles); toggle("Capture folders",\.captureFolders)
            integer("Keep unpinned history (days)",\.retentionDays,1...3650)
            integer("Maximum unpinned records",\.retentionItemCount,10...100000)
            integer("Maximum text characters",\.maxTextCharacters,1024...16777216)
            integer("Maximum files per capture",\.maxClipboardFileItems,1...1000)
            TextField("Maximum image bytes",value: binding(\.maxImageBytes),format: .number)
            TextField("Maximum image pixels",value: binding(\.maxImagePixels),format: .number)
            TextField("Maximum bytes per file",value: binding(\.maxClipboardFileBytes),format: .number)
            TextField("Maximum bytes per file selection",value: binding(\.maxClipboardFileTotalBytes),format: .number)
            if let capture = model.clipboard, capture.missedObservations > 0 { Text("\(capture.missedObservations) intermediate clipboard changes could not be observed between polls.").foregroundStyle(.secondary) }
        }
        SettingsGroup("Data") { Text(model.paths.root.path).textSelection(.enabled); Button("Open Data Folder") { NSWorkspace.shared.open(model.paths.root) } }
        SettingsGroup(model.ui("QuickActionsSection")) {
            Text(model.ui("QuickActionsDescription")).foregroundStyle(.secondary)
            ForEach(["file","image","text","url"],id: \.self) { profile in
                QuickActionEditor(model: model,profile: profile)
            }
            Button(model.ui("ResetAllQuickActions")) { model.update { $0.quickActionPreferences = AppSettings().quickActionPreferences } }
                .accessibilityIdentifier("quick-actions-reset-all")
        }
    }
    @ViewBuilder private var island: some View {
        VStack(spacing: 10) {
            SettingsRow(title: model.ui("IslandAppearance")) {
                Picker("Appearance", selection: binding(\.islandAppearance.theme)) {
                    Text(model.ui("IslandAppearanceFollow")).tag(Theme.system)
                    Text(model.ui("IslandAppearanceLight")).tag(Theme.light)
                    Text(model.ui("IslandAppearanceDark")).tag(Theme.dark)
                }.frame(width: 150)
            }
            SettingsRow(title: model.ui("IslandContentPriority")) {
                Picker("Content priority", selection: binding(\.islandContentPriority)) {
                    Text(model.ui("IslandContentPriorityMusic")).tag("music")
                    Text(model.ui("IslandContentPriorityTemporarySpace")).tag("space")
                }.frame(width: 150)
            }
            islandToggle("IslandShowLogoWhenIdle", \.islandAppearance.showLogoWhenIdle)
            islandToggle("IslandResident", \.islandAppearance.resident)
            islandToggle("IslandForceShowOverFullscreen", \.islandAppearance.forceShowOverFullscreen)
            Text(model.ui("IslandForceShowOverFullscreenDescription")).font(.callout).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 14)
            SettingsValueSlider(title: model.ui("IslandHideDelay"), model: model, key: \.islandHideDelaySeconds, range: 0...10, step: 1) { model.ui("IslandDelaySeconds", String(Int($0))) }
            SettingsValueSlider(title: model.ui("IslandCompactScale"), model: model, key: \.islandAppearance.compactScale, range: 0.5...2, step: 0.1) { String(format: "%.1f×", $0) }
            SettingsValueSlider(title: model.ui("IslandExpandedScale"), model: model, key: \.islandAppearance.expandedScale, range: 0.5...2, step: 0.1) { String(format: "%.1f×", $0) }
            islandToggle("IslandRightHoldMove", \.islandAppearance.rightClickHoldToMove)
        }
        SettingsRow(title: "Motion") { Picker("Motion",selection: binding(\.overlayMotion)) { ForEach(Motion.allCases,id: \.self) { Text($0.rawValue.capitalized).tag($0) } }.frame(width: 150) }
        SettingsRow(title: "Display") { Picker("Display",selection: binding(\.overlayMonitor)) { Text("Automatic").tag("automatic"); Text("Primary").tag("primary") }.frame(width: 150) }
        SettingsRow(title: "Drag activation") { Picker("Drag activation",selection: binding(\.fileDragWakeMode)) { Text("Smart").tag(DragWakeMode.smart); Text("Top Edge").tag(DragWakeMode.classicTopEdge); Text("Disabled").tag(DragWakeMode.disabled) }.frame(width: 150) }
        SettingsCard {
            HStack { Button("Adjust Island Position…") { model.island?.beginPositionEdit() }; Button("Reset Current Display Position") { model.island?.resetPosition() } }
        }
        SettingsCard {
            TextField("Excluded bundle IDs / process names (comma separated)",text: Binding(get: { model.settings.smartDragExcludedProcesses.joined(separator: ", ") },set: { text in model.update { $0.smartDragExcludedProcesses = text.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty } } }))
        }
    }
    private func islandToggle(_ key: String, _ path: WritableKeyPath<AppSettings, Bool>) -> some View {
        SettingsRow(title: model.ui(key)) { Toggle(model.ui(key), isOn: binding(path)).toggleStyle(.switch).accessibilityIdentifier(key) }
    }
    @ViewBuilder private var shortcuts: some View {
        SettingsGroup("Global") { ShortcutRecorder(label: "Show Island",value: model.settings.quickPanelHotkey,model: model) { value in model.update { $0.quickPanelHotkey = value } } }
        SettingsGroup("Main Window") { ForEach(AppSettings.shortcutDefaults.keys.sorted(),id: \.self) { id in ShortcutRecorder(label: id,value: model.settings.shortcut(id),model: model) { value in model.update { $0.keyboardShortcuts[id] = value } } } }
        SettingsGroup("Context Keys") { Text("Return: open · Space: preview · Delete: remove from DropSpace · Escape: cancel") }
    }
    @ViewBuilder private var downloads: some View {
        DownloadHistoryView(model: model)
    }
    @ViewBuilder private var activities: some View {
        toggle("Show DropSpace activity notifications",\.systemActivities.showApplicationNotifications)
        toggle("Show output volume changes",\.systemActivities.showVolumeChanges)
        if model.settings.systemActivities.showApplicationNotifications {
            LabeledContent("Notification permission", value: notificationPermissionLabel)
            if let error = model.activities.error { Text(error).foregroundStyle(.red) }
        }
        if model.settings.systemActivities.showVolumeChanges {
            if let current = model.activities.volume.current { Text("Current output: \(current.percent)%\(current.muted ? " · Muted" : "")") }
            if let reason = model.activities.volume.unavailableReason { Text(reason).foregroundStyle(.secondary) }
        }
        Text("macOS does not expose other applications’ notification history through UserNotifications. This setting covers DropSpace activities only.").foregroundStyle(.secondary)
    }
    private var notificationPermissionLabel: String {
        switch model.activities.notificationAuthorization {
        case .authorized: "Allowed"
        case .provisional: "Quiet delivery"
        case .denied: "Denied in System Settings"
        case .notDetermined: "Not requested"
        default: "Unavailable"
        }
    }
    private var devices: some View { DeviceSettingsView(model: model) }
    @ViewBuilder private var updates: some View {
        Link("Download the latest Mac beta", destination: URL(string: "https://airanluo-dot.github.io/DropSpace/macos/")!)
        Text("Beta 1: use the Mac DMG for manual updates. Quit DropSpace, replace the app in Applications, then reopen it. Keep your application data. Automatic installation is experimental and has not passed public update acceptance.").font(.callout).foregroundStyle(.secondary)
        if let updates = model.updates {
            LabeledContent("Update status", value: updates.phase.rawValue.capitalized)
            if let release = updates.release {
                Text(release.tag).font(.headline)
                Text(release.releaseNotes).textSelection(.enabled)
                Link("Release details and manual download", destination: release.releaseURL)
            }
            if updates.phase == .downloading {
                ProgressView(value: Double(updates.received), total: Double(max(1, updates.total ?? 1)))
                Text(ByteCountFormatter.string(fromByteCount: updates.received, countStyle: .file))
            }
            HStack {
                Button("Check for Updates") { updates.check() }.disabled(updates.isBusy)
                if updates.release != nil && !updates.isBusy && updates.phase != .ready {
                    Button("Download Update") { updates.download() }
                }
                if updates.phase == .ready { Button("Install and Restart") { updates.install() } }
                if updates.isBusy && updates.phase != .installing { Button("Cancel") { updates.cancel() } }
            }
            if let message = updates.message { Text(message).foregroundStyle(.secondary).textSelection(.enabled) }
            ForEach(updates.history, id: \.id) { attempt in
                DisclosureGroup("\(attempt.manifest.tag): \(attempt.phase)") {
                    if let message = attempt.message { Text(message).textSelection(.enabled) }
                    Text("Previous application: \(attempt.backup.path)").textSelection(.enabled)
                    Button("Show Recovery Files") { NSWorkspace.shared.activateFileViewerSelecting([attempt.application.deletingLastPathComponent()]) }
                }
            }
        }
        toggle("Automatically check for updates",\.autoCheckForUpdates); toggle("Automatically download updates",\.autoDownloadUpdates); toggle("Automatically install updates",\.autoInstallUpdates)
        Picker("Channel",selection: binding(\.updateChannel)) { Text("Stable").tag(UpdateChannel.stable); Text("Beta").tag(UpdateChannel.beta) }
        Text(Baseline.display).textSelection(.enabled)
        Text("Not notarized by Apple. No Developer ID signature. Updates use the official HTTPS release source, SHA-256 and local ad-hoc bundle integrity checks; these are not Apple identity authentication.").font(.caption).foregroundStyle(.secondary)
    }
    @ViewBuilder private var components: some View {
        SettingsGroup("Local Translation") {
            Text("Install a model here, then enable Local AI translation in Music Settings. Translation runs on this Mac.").foregroundStyle(.secondary)
            Picker("Model",selection: binding(\.lyrics.aiModelID)) { Text("Hy-MT2 1.8B Q8_0").tag("hy-mt2-18-q8-plain-beta"); Text("Hy-MT2 7B Q8_0").tag("hy-mt2-7b-q8-plain-beta") }
            Text("The Windows CUDA package is not compatible with Apple Silicon. The native acceleration backend is Metal.").foregroundStyle(.secondary)
        }
        if let models = model.aiModels { AIModelComponents(models: models, model: model) }
        if let modules = model.modules { ModuleInventoryView(model: model, modules: modules) }
    }
    @ViewBuilder private var about: some View {
        Text("DropSpace").font(.largeTitle)
        Text(Baseline.display).textSelection(.enabled)
        Text("macOS 26 or later · Apple Silicon")
        Text("Development build. Feature parity and release qualification are still in progress.").foregroundStyle(.secondary)
        Link("Project",destination: URL(string: "https://github.com/airanluo-dot/DropSpace")!)
    }
}
struct QuickActionEditor: View {
    let model: WorkspaceModel; let profile: String
    var preference: QuickActionPreference { model.settings.quickActionPreferences[profile] ?? .init() }
    var body: some View {
        DisclosureGroup(model.ui(["file": "QuickActionFiles", "image": "QuickActionImages", "text": "QuickActionText", "url": "QuickActionUrls"][profile] ?? "QuickActionsSection")) {
            Toggle(model.ui("QuickActionAutomatic"),isOn: Binding(get: { preference.automatic },set: { value in model.update {
                if value { $0.quickActionPreferences[profile] = .init() }
                else { $0.quickActionPreferences[profile,default: .init()].automatic = false }
            } })).accessibilityIdentifier("quick-actions-automatic-" + profile)
            HStack {
                ForEach(0..<3,id: \.self) { index in
                    Picker(model.ui("QuickActionSlot", String(index + 1)),selection: Binding(get: { preference.actions.indices.contains(index) ? preference.actions[index]?.rawValue ?? "" : "" },set: { value in
                        model.update { settings in
                            var current = settings.quickActionPreferences[profile,default: .init()]
                            while current.actions.count < 3 { current.actions.append(nil) }
                            let selected = QuickAction(rawValue: value)
                            current.actions[index] = current.actions.enumerated().contains { $0.offset != index && selected != nil && $0.element == selected } ? nil : selected
                            settings.quickActionPreferences[profile] = current
                        }
                    })) {
                        Text(model.ui("QuickActionNone")).tag("")
                        ForEach(QuickActionRules.registeredLocalActions, id: \.self) { Text(model.ui($0.labelResource)).tag($0.rawValue) }
                        // Keep older defined-but-unregistered saved IDs readable.
                        if let saved = preference.actions.indices.contains(index) ? preference.actions[index] : nil,
                           !QuickActionRules.registeredLocalActions.contains(saved) { Text(model.ui(saved.labelResource)).tag(saved.rawValue) }
                    }.disabled(preference.automatic).accessibilityIdentifier("quick-actions-\(profile)-\(index)")
                }
            }
            Button(model.ui("ResetQuickActions")) { model.update { $0.quickActionPreferences[profile] = .init() } }
                .accessibilityIdentifier("quick-actions-reset-" + profile)
        }
    }
}

extension AppSettings {
    var islandHideDelaySeconds: Double {
        get { Double(islandAppearance.hideDelayMilliseconds) / 1000 }
        set { islandAppearance.hideDelayMilliseconds = Int(newValue.rounded()) * 1000 }
    }
}
