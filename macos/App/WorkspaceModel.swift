import AppKit
import SwiftUI
import Observation
import ServiceManagement
import Quartz
import DropSpaceCore
import DropSpaceInfrastructure
import DropSpacePlatform

@MainActor @Observable final class WorkspaceModel {
    var settings = AppSettings()
    var items: [DropItem] = []
    var islandSpace: [DropItem] = []
    var islandSpaceCount = 0
    var islandClipboard: [DropItem] = []
    var selection = Set<UUID>()
    var expandedBatches = Set<UUID>()
    var section = "space"
    var settingsSection = "General"
    var search = ""
    var kindFilter: ItemKind?
    var statusFilter: ItemStatus?
    var error: String?
    var status: String?
    var ready = false
    var savingSettings = false
    private var settingsTask: Task<Void,Never>?
    var undoReceipt: UndoReceipt?
    var showsOnboarding = false
    var showsTextIntake = false
    let imageExport: NativeImageExport
    var hashResult: String?
    var hashOutputURL: URL?
    var lastOutput: URL?
    var previewURL: URL?
    var textPreview: DropItem?
    var store: ItemStore?
    let paths: StoragePaths
    let preferences: SettingsStore
    let payloads: PayloadStore
    let actions: ItemActions
    let actionJobs = NativeActionJobs()
    let fileReferences = FileReferenceService()
    var clipboard: ClipboardCapture?
    var refreshTask: Task<Void,Never>?
    var undoTask: Task<Void,Never>?
    var refreshGeneration = 0
    var pageLimit = 250
    var hasMoreItems = false
    var openMainWindow: (() -> Void)?
    var openSettings: (() -> Void)?
    var requestQuit: (() -> Void)?
    var island: IslandController?
    let hotkey = GlobalHotkey()
    struct PendingIntake { let urls: [URL]; let acquisition: String }
    var pendingIntake: [PendingIntake] = []
    private var pendingText: [(text: String, acquisition: String)] = []
    var intakeCounts: (accepted: Int, rejected: Int)?
    @ObservationIgnored private var intakeTasks: [UUID: Task<Void, Never>] = [:]
    var searchFocusRequest = UUID()
    let media = NativeMediaService()
    let musicRestart = NativeMusicRestart()
    let neteaseControls = NativeNeteaseControls()
    private var musicSuspended = false
    let spectrum = NativeSpectrumService()
    let activities = NativeSystemActivities()
    let downloadBudget = DownloadBudget()
    @ObservationIgnored lazy var downloadLimits = DownloadLimitEditor(model: self)
    var downloads: DownloadManager?
    var updates: NativeUpdateService?
    var lyrics: NativeLyricsService?
    var aiModels: NativeAIModels?
    var modules: NativeModules?
    var devices: NativeDevices?
    var shares: NativeShares?
    var internetShares: NativeInternetShares?
    var promisedFiles: NativePromisedFiles?
    var isQuitting = false
    var moduleReturnSection = "space"
    var ui: Localizer { Localizer(settings.language) }
    func openComponentSettings() { settingsSection = "DLC"; openSettings?() }
    var batchProjection: BatchProjection { BatchProjection(items: items, expanded: expandedBatches) }
    var selected: [DropItem] { batchProjection.visible.filter { selection.contains($0.id) } }
    func toggleBatch(_ id: UUID) {
        if !expandedBatches.insert(id).inserted { expandedBatches.remove(id) }
        selection.formIntersection(batchProjection.visible.map(\.id))
    }
    func pinBatch(_ id: UUID) {
        Task { do { undoReceipt = try await store?.toggleBatchPin(id); scheduleUndo(); refresh() } catch { self.error = error.localizedDescription } }
    }
    func removeBatch(_ id: UUID) {
        Task { do { undoReceipt = try await store?.removeBatch(id); scheduleUndo(); refresh() } catch { self.error = error.localizedDescription } }
    }
    init() {
        let path = ProcessInfo.processInfo.environment["DROPSPACE_DATA_DIRECTORY"].map { URL(fileURLWithPath: $0,isDirectory: true) }
        paths = StoragePaths(root: path)
        preferences = SettingsStore(file: paths.settings)
        payloads = PayloadStore(directory: paths.payloads)
        actions = ItemActions(payloads: payloads, references: fileReferences)
        let imageActions = actions
        imageExport = NativeImageExport(
            readSize: { try await imageActions.imageSize($0) },
            export: { try await imageActions.image($0.item, format: $0.format, width: $0.width, height: $0.height,
                                                 stripMetadata: true, keepAspectRatio: $0.keepAspect,
                                                 outputSuffix: $0.action == .resizeImage ? "resized" : "converted", directory: $0.directory) })
        imageExport.onOutput = { [weak self] in self?.lastOutput = $0 }
    }
    func start() async {
        guard !ready else { return }
        do {
            settings = try await preferences.load()
            store = try ItemStore(directory: paths.root)
            try await downloadBudget.configure(settings)
            downloads = try DownloadManager(directory: paths.root.appendingPathComponent("Downloads"),budget: downloadBudget)
            await downloads?.activate()
            guard let store else { return }
            try await store.recoverAfterLaunch()
            try await payloads.reconcile(store)
            promisedFiles = NativePromisedFiles(importer: PromisedFileImporter(store: store, payloads: payloads, references: fileReferences))
            promisedFiles?.onChange = { [weak self] in self?.refresh() }
            await promisedFiles?.activate()
            undoReceipt = try await store.pendingUndo(); scheduleUndo()
            var captureBoard = NSPasteboard.general
            #if DEBUG
            if ProcessInfo.processInfo.environment["DROPSPACE_DATA_DIRECTORY"] != nil,
               let name = ProcessInfo.processInfo.environment["DROPSPACE_VALIDATION_PASTEBOARD"], name.hasPrefix("DropSpace.Validation.") {
                captureBoard = NSPasteboard(name: .init(name))
            }
            #endif
            clipboard = ClipboardCapture(store: store,payloads: payloads,pasteboard: captureBoard,references: fileReferences)
            clipboard?.onChange = { [weak self] in self?.refresh() }
            clipboard?.onError = { [weak self] message in self?.error = message }
            ready = true; section = settings.launchPage
            let pending = pendingIntake; pendingIntake.removeAll()
            for request in pending { intake(request.urls, acquisition: request.acquisition) }
            let texts = pendingText; pendingText.removeAll()
            for request in texts { addText(request.text, acquisition: request.acquisition) }
            showsOnboarding = !settings.privacyChoicesCompleted
            try await clipboard?.configure(settings)
            island = IslandController(model: self)
            activities.onChange = { [weak self] in self?.island?.activityChanged() }
            await activities.configure(settings.systemActivities)
            await downloads?.setCompletionHandler { [weak self] output in
                await self?.activities.notify(title: "Download complete", body: output.lastPathComponent)
            }
            hotkey.action = { [weak self] in self?.island?.showFromHotkey() }
            do { try hotkey.register(settings.quickPanelHotkey) } catch { self.error = error.localizedDescription }
            let models = try NativeAIModels(directory: paths.root.appendingPathComponent("Models"), budget: downloadBudget)
            aiModels = models
            lyrics = NativeLyricsService(budget: downloadBudget, dataDirectory: paths.root, models: models.store)
            aiModels?.beforeChange = { [weak self] in await self?.lyrics?.stop() }
            aiModels?.afterChange = { [weak self] in
                guard let self, !musicSuspended, !isQuitting else { return }; lyrics?.configure(snapshot: media.snapshot, settings: settings.lyrics, target: settings.lyricsTranslationTarget, refresh: true, resetExecution: true)
            }
            await aiModels?.retryCleanup()
            spectrum.onPresentationChange = { [weak self] in self?.island?.refresh() }
            media.onChange = { [weak self] snapshot in
                guard let self, !musicSuspended, !isQuitting else { return }; lyrics?.configure(snapshot: snapshot, settings: settings.lyrics, target: settings.lyricsTranslationTarget)
                spectrum.configure(snapshot: snapshot, settings: settings); island?.refresh()
            }
            media.start(settings: settings)
            updates = NativeUpdateService(dataDirectory: paths.root, budget: downloadBudget)
            updates?.onChecked = { [weak self] date in self?.update { $0.lastUpdateCheck = date } }
            updates?.onInstall = { [weak self] in self?.requestQuit?() }
            updates?.configure(settings)
            try NativeUpdateService.acknowledgeLaunch(dataDirectory: paths.root)
            modules = try NativeModules(directory: paths.root.appendingPathComponent("Modules"), budget: downloadBudget)
            modules?.onChange = { [weak self] in
                guard let self else { return }
                if section.hasPrefix("module:"), selectedModule == nil { closeModule() }
                island?.refresh()
            }
            await modules?.start()
            guard let clipboard else { throw DropSpaceError.unavailable("Clipboard capture unavailable") }
            devices = try NativeDevices(directory: paths.root.appendingPathComponent("DropLink"), validationNamespace: ProcessInfo.processInfo.environment["DROPSPACE_DATA_DIRECTORY"], capture: clipboard, payloads: payloads, resolveReference: { [weak self] item in
                guard let self else { throw CancellationError() }
                return try await checkedContentURL(item)
            })
            devices?.onPaired = { [weak self] peer in
                self?.update { value in value.clipboardPeerModes[peer.id.uuidString] = value.defaultClipboardSyncMode }
            }
            devices?.onUnpaired = { [weak self] id in self?.update { value in value.clipboardPeerModes = value.clipboardPeerModes.filter { UUID(uuidString: $0.key) != id } } }
            devices?.onError = { [weak self] in self?.error = $0 }
            devices?.onReceive = { [weak self, store] message in
                guard let self else { throw CancellationError() }
                let item = try FileIntake.handoff(message, settings: settings)
                try Task.checkCancellation(); try await store.insert([item]); refresh()
            }
            do { try await devices?.configure(settings) } catch { self.error = error.localizedDescription }
            shares = try NativeShares(model: self)
            internetShares = try NativeInternetShares(model: self)
            refresh()
        } catch { self.error = error.localizedDescription }
    }
    func refresh() {
        refreshTask?.cancel(); refreshGeneration += 1; let run = refreshGeneration
        refreshTask = Task { [weak self] in
            guard let self, let store else { return }
            do {
                if !search.isEmpty { try await Task.sleep(for: .milliseconds(180)) }
                var query = ItemQuery(source: search.isEmpty ? (section == "space" ? .space : section == "clipboard" ? .clipboard : nil) : nil,pinnedOnly: section == "pinned",text: search)
                query.kind = kindFilter; query.status = statusFilter
                query.limit = pageLimit + 1
                let values = try await store.items(query)
                guard !Task.isCancelled, run == refreshGeneration else { return }
                hasMoreItems = values.count > pageLimit
                items = Array(values.prefix(pageLimit)); selection.formIntersection(Set(items.map(\.id)))
                var islandQuery = ItemQuery(source: .space); islandQuery.limit = 50
                islandSpace = try await store.items(islandQuery)
                islandSpaceCount = try await store.visibleCount(source: .space)
                islandQuery.source = .clipboard
                islandClipboard = try await store.items(islandQuery)
                island?.refresh()
            } catch is CancellationError { } catch { self.error = error.localizedDescription }
        }
    }
    func resetQuery() { pageLimit = 250; refresh() }
    func loadMore() { pageLimit += 250; refresh() }
    func save(_ proposed: AppSettings) async {
        guard !savingSettings else { return }
        savingSettings = true; defer { savingSettings = false }
        let old = settings
        let previousLoginStatus = SMAppService.mainApp.status
        do {
            let value = try proposed.validated()
            if value.quickPanelHotkey != old.quickPanelHotkey {
                let canonical = Hotkey.parse(value.quickPanelHotkey)!.canonical
                guard !AppSettings.shortcutDefaults.keys.contains(where: { Hotkey.parse(value.shortcut($0))?.canonical == canonical }) else { throw DropSpaceError.invalid("Shortcut is already assigned") }
                try hotkey.register(value.quickPanelHotkey)
            }
            if value.startAtLogin != old.startAtLogin || (!old.privacyChoicesCompleted && value.privacyChoicesCompleted && value.startAtLogin) {
                if value.startAtLogin { try SMAppService.mainApp.register() }
                else if SMAppService.mainApp.status == .enabled || SMAppService.mainApp.status == .requiresApproval { try await SMAppService.mainApp.unregister() }
            }
            try await clipboard?.configure(value)
            try await devices?.configure(value)
            try await preferences.save(value)
            try await downloadBudget.configure(value)
            settings = value; media.start(settings: value); island?.refresh(); updates?.configure(value)
            if !musicSuspended {
                spectrum.configure(snapshot: media.snapshot, settings: value)
                lyrics?.configure(snapshot: media.snapshot, settings: value.lyrics, target: value.lyricsTranslationTarget)
            }
            await activities.configure(value.systemActivities, requestPermission: value.systemActivities.showApplicationNotifications && !old.systemActivities.showApplicationNotifications)
            NSApp.appearance = value.theme == .system ? nil : NSAppearance(named: value.theme == .dark ? .darkAqua : .aqua)
            showsOnboarding = !value.privacyChoicesCompleted
            self.error = nil
        } catch {
            try? await clipboard?.configure(old)
            try? await devices?.configure(old)
            if proposed.quickPanelHotkey != old.quickPanelHotkey { try? hotkey.register(old.quickPanelHotkey) }
            if SMAppService.mainApp.status != previousLoginStatus {
                if previousLoginStatus == .enabled || previousLoginStatus == .requiresApproval { try? SMAppService.mainApp.register() }
                else { try? await SMAppService.mainApp.unregister() }
            }
            self.error = error.localizedDescription
        }
    }
    @discardableResult func update(_ change: (inout AppSettings) -> Void) -> Task<Void, Never> {
        let baseline = settings; var requested = baseline; change(&requested)
        let previous = settingsTask
        settingsTask = Task { [weak self] in
            await previous?.value
            guard let self else { return }
            do { await save(try SettingsMerge.merge(baseline: baseline,requested: requested,latest: settings)) }
            catch { self.error = error.localizedDescription }
        }
        return settingsTask!
    }
    func intake(_ urls: [URL]) {
        intake(urls, acquisition: "file-drop")
    }
    func intake(_ urls: [URL], acquisition: String) {
        guard !isQuitting else { return }
        guard ready else { pendingIntake.append(.init(urls: urls, acquisition: acquisition)); return }
        let id = UUID()
        intakeTasks[id] = Task {
            defer { intakeTasks.removeValue(forKey: id) }
            do {
                guard let store else { return }
                let configuration = settings
                var result = try await fileReferences.prepare(urls,source: .space,settings: configuration)
                for index in result.items.indices { result.items[index].batchAcquisition = acquisition }
                try Task.checkCancellation(); guard !isQuitting else { throw CancellationError() }
                let committed = try await store.insertFileCandidates(result.items)
                try Task.checkCancellation(); guard !isQuitting else { throw CancellationError() }
                intakeCounts = (committed.items.count, committed.rejected + result.failures.count)
                if !result.failures.isEmpty { error = result.failures.joined(separator: "\n") }
                refresh()
            } catch is CancellationError {} catch { self.error = error.localizedDescription }
        }
    }
    func intakePromised(_ receivers: [NSFilePromiseReceiver]) -> Bool {
        guard ready, !isQuitting, let promisedFiles else { return false }
        return promisedFiles.receive(receivers, settings: settings)
    }
    func addText(_ text: String) {
        addText(text, acquisition: "manual-text-url")
    }
    func addText(_ text: String, acquisition: String) {
        guard !isQuitting else { return }
        guard ready else { pendingText.append((text, acquisition)); return }
        let id = UUID()
        intakeTasks[id] = Task {
            defer { intakeTasks.removeValue(forKey: id) }
            do {
                guard let store else { return }
                var item = try FileIntake.text(text, source: .space, settings: settings)
                item.batchID = UUID(); item.batchIndex = 0; item.batchCount = 1; item.batchAcquisition = acquisition
                try Task.checkCancellation(); guard !isQuitting else { throw CancellationError() }
                try await store.insert([item])
                try Task.checkCancellation(); guard !isQuitting else { throw CancellationError() }
                refresh()
            } catch is CancellationError {} catch { self.error = error.localizedDescription }
        }
    }
    func chooseFiles() { chooseFiles(foldersOnly: false) }
    func chooseFiles(foldersOnly: Bool) {
        let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = !foldersOnly; panel.allowsMultipleSelection = true
        panel.begin { [weak self] response in if response == .OK { self?.intake(panel.urls) } }
    }
    func pin(_ values: [DropItem], _ pinned: Bool) {
        Task { do { undoReceipt = try await store?.setPinned(values.map(\.id),pinned: pinned); scheduleUndo(); refresh() } catch { self.error = error.localizedDescription } }
    }
    func remove(_ values: [DropItem]) {
        Task { do { undoReceipt = try await store?.remove(values.map(\.id)); scheduleUndo(); refresh() } catch { self.error = error.localizedDescription } }
    }
    func clearClipboard(_ range: ClipboardClearRange = .all) {
        Task { do {
            var query = ItemQuery(source: .clipboard); query.limit = Int.max
            let from = range.from(now: Date())
            let values = try await store?.items(query) ?? []
            remove(values.filter { !$0.isPinned && (from == nil || $0.createdAt >= from!) })
        } catch { self.error = error.localizedDescription } }
    }
    func undo() {
        guard let receipt = undoReceipt else { return }
        Task { do { try await store?.undo(receipt.id); undoReceipt = nil; undoTask?.cancel(); refresh() } catch { self.error = error.localizedDescription } }
    }
    func scheduleUndo() {
        undoTask?.cancel(); guard let receipt = undoReceipt else { return }
        undoTask = Task {
            do { try await Task.sleep(for: .seconds(max(0,receipt.expiresAt.timeIntervalSinceNow))); if let store { try await store.finalizeUndo(); try await payloads.reconcile(store) }; if undoReceipt?.id == receipt.id { undoReceipt = nil } }
            catch is CancellationError { } catch { self.error = error.localizedDescription }
        }
    }
    /// Resolve off the main actor, then reconfirm the exact record before using
    /// the URL. A late probe can refresh a card but cannot authorize another file.
    func checkedContentURL(_ item: DropItem) async throws -> URL {
        guard let reference = item.file, let store else { return try await actions.contentURL(item) }
        let generation = refreshGeneration
        let availability = try await fileReferences.check(reference, kind: item.kind)
        try Task.checkCancellation()
        guard !isQuitting, generation == refreshGeneration else { throw CancellationError() }
        let result = try await store.refreshFileStatus(expected: item, availability: availability)
        try Task.checkCancellation()
        guard !isQuitting, generation == refreshGeneration else { throw CancellationError() }
        publishFileRefresh(expected: item, current: result.current)
        guard result.accepted else { throw DropSpaceError.unavailable("The record changed. Refresh before trying again.") }
        guard availability.status == .available, let url = availability.resolvedURL else { throw availability.reason ?? .storageUnavailable }
        return url
    }
    private func publishFileRefresh(expected: DropItem, current: DropItem?) {
        func reconcile(_ values: inout [DropItem]) -> Bool {
            guard let index = values.firstIndex(where: { $0.id == expected.id && $0.revision == expected.revision }) else { return false }
            if let current {
                guard values[index] != current else { return false }
                values[index] = current
            } else { values.remove(at: index) }
            return true
        }
        let mainChanged = reconcile(&items)
        let spaceChanged = reconcile(&islandSpace)
        let clipboardChanged = reconcile(&islandClipboard)
        if current == nil { selection.remove(expected.id) }
        if spaceChanged || clipboardChanged { island?.refresh() }
        if mainChanged, let statusFilter, current?.status != statusFilter { refresh() }
    }
    func copy(_ values: [DropItem]) {
        Task { do {
            guard let clipboard else { return }
            var urls: [URL] = []
            if values.allSatisfy({ $0.file != nil }) {
                for item in values { urls.append(try await checkedContentURL(item)) }
            }
            try Task.checkCancellation()
            try await clipboard.copy(values, resolvedFileURLs: urls.isEmpty ? nil : urls)
            try await store?.markUsed(values.map(\.id)); refresh()
        } catch is CancellationError {} catch { self.error = error.localizedDescription } }
    }
    func open(_ item: DropItem) {
        Task { do {
            if item.kind == .url, let text = item.text, let url = URL(string: text), ["https","http"].contains(url.scheme ?? "") {
                guard NSWorkspace.shared.open(url) else { throw DropSpaceError.unavailable("The URL could not be opened") }
            } else if item.text != nil { selection = [item.id]; return }
            else {
                let url = try await checkedContentURL(item)
                try Task.checkCancellation()
                guard NSWorkspace.shared.open(url) else { throw DropSpaceError.unavailable("The file could not be opened") }
            }
            try await store?.markUsed([item.id]); refresh()
        } catch is CancellationError {} catch { self.error = error.localizedDescription } }
    }
    func preview(_ item: DropItem) {
        if item.text != nil { textPreview = item; return }
        Task { do { previewURL = try await checkedContentURL(item) } catch is CancellationError {} catch { self.error = error.localizedDescription } }
    }
    func reveal(_ item: DropItem) { Task { do { NSWorkspace.shared.activateFileViewerSelecting([try await checkedContentURL(item)]) } catch is CancellationError {} catch { self.error = error.localizedDescription } } }
    func replaceReference(_ item: DropItem) {
        let generation = refreshGeneration
        let panel = NSOpenPanel(); panel.canChooseDirectories = true
        panel.begin { [weak self] result in
            guard let self, result == .OK, let url = panel.url else { return }
            Task { do {
                // Locate is explicit user intake; clipboard capture switches and
                // size limits do not veto repairing an existing reference.
                let result = try await fileReferences.prepare([url],source: .space,settings: settings)
                try Task.checkCancellation()
                guard !isQuitting, refreshGeneration == generation else { throw CancellationError() }
                guard let replacement = result.items.first else { throw DropSpaceError.unavailable(result.failures.joined(separator: "\n")) }
                var changed = item; changed.file = replacement.file; changed.payloadPath = nil; changed.title = replacement.title; changed.kind = replacement.kind; changed.status = replacement.status; changed.revision += 1
                try await store?.replace(changed)
                if let store { try await payloads.reconcile(store) }
                refresh()
            } catch is CancellationError {} catch { self.error = error.localizedDescription } }
        }
    }
    var exportDirectory: URL { paths.exports }
    func perform(_ action: QuickAction, _ values: [DropItem]) {
        guard !isQuitting, let first = values.first else { return }
        if QuickActionRules.registeredLocalActions.contains(action),
           !QuickActionRules.localCapabilities(values).contains(action) {
            error = ui("ActionUnavailable"); return
        }
        switch action {
        case .open: open(first); return
        case .showInFolder: reveal(first); return
        case .copy: copy(values); return
        case .preview: preview(first); return
        case .resizeImage, .convertImage:
            if !imageExport.begin(item: first, action: action, directory: exportDirectory) { error = ui("ActionUnavailable") }
            return
        case .sendToDevice: devices?.send(first, selection: values); return
        case .nearbyLink: shares?.create(values); return
        case .internetLink: internetShares?.create(values); return
        default: break
        }
        let directory = exportDirectory
        actionJobs.start(operation: { [self] () async throws -> (output: URL?, digest: String?) in
            switch action {
            case .copyPath:
                var paths: [String] = []
                for item in values where item.file != nil { paths.append(try await checkedContentURL(item).path) }
                guard !paths.isEmpty, let clipboard else { throw DropSpaceError.unavailable("No file paths or clipboard service are available") }
                try Task.checkCancellation()
                try await clipboard.copy([FileIntake.text(paths.joined(separator:"\n"),source:.space,settings:settings)])
                return (nil, nil)
            case .hashSha256:
                let result = try await actions.exportHash(first,directory:directory)
                return (result.url, result.digest)
            case .compressZip: return (try await actions.zip(values,directory: directory), nil)
            case .generateQr: return (try await actions.qr(first.text ?? first.file?.path ?? first.title,name: first.title,directory: directory), nil)
            case .stripMetadata:
                return (try await actions.image(first,format: ImageRules.nativeOutputFormat(forPath: first.file?.path ?? first.payloadPath ?? ""),
                                                width: nil,height: nil,stripMetadata: true,outputSuffix: "clean",directory: directory), nil)
            default: throw DropSpaceError.unavailable("Unsupported local action")
            }
        }, completed: { [weak self] result in
            guard let self, !isQuitting else { return }
            switch result {
            case .success(let value):
                if let digest = value.digest { hashResult = digest; hashOutputURL = value.output }
                else if let output = value.output { lastOutput = output }
            case .failure(let failure):
                if !(failure is CancellationError) { error = failure.localizedDescription }
            }
        })
    }
    func download(_ url: URL) {
        Task { do {
            let directory = settings.defaultDownloadDirectory.isEmpty ? FileManager.default.urls(for: .downloadsDirectory,in: .userDomainMask)[0] : URL(fileURLWithPath: settings.defaultDownloadDirectory)
            _ = try await downloads?.add(source: url,directory: directory)
        } catch { self.error = error.localizedDescription } }
    }
    func reconnectMusic() {
        guard ready, !isQuitting else { return }
        musicRestart.begin { [weak self] in
            guard let self else { throw CancellationError() }
            musicSuspended = true; media.suspend()
            defer {
                musicSuspended = false
                if !isQuitting {
                    media.resume()
                    // Stop reset the query fence: same-track recovery must reload
                    // while still allowing the persistent lyric cache to be used.
                    lyrics?.resumeAfterReconnect(snapshot: media.snapshot, settings: settings.lyrics, target: settings.lyricsTranslationTarget)
                    spectrum.resumeAfterReconnect(snapshot: media.snapshot, settings: settings)
                    island?.refresh()
                }
            }
            await lyrics?.suspendForReconnect()
            try await spectrum.suspendForReconnect()
            await media.drain()
            try Task.checkCancellation()
            guard !isQuitting else { throw CancellationError() }
            try await media.reconnect()
        }
    }
    func shutdown() async {
        isQuitting = true
        await imageExport.shutdown()
        await actionJobs.shutdown()
        await musicRestart.shutdown()
        await neteaseControls.shutdown()
        if await promisedFiles?.shutdown() == false { error = "Promised-file provider is still retiring; its staging lease remains for recovery." }
        let pendingIntakes = Array(intakeTasks.values)
        for task in pendingIntakes { task.cancel() }
        if await fileReferences.shutdown() == false { error = "File metadata native IO is still retiring" }
        for task in pendingIntakes { await task.value }
        await downloadLimits.flush()
        await settingsTask?.value
        refreshTask?.cancel(); undoTask?.cancel(); hotkey.stop(); media.suspend(); island?.stop()
        await media.drain()
        if await spectrum.shutdown() == false { error = "Audio capture cleanup remains pending." }
        await lyrics?.stop()
        aiModels?.afterChange = nil; await aiModels?.shutdown()
        await clipboard?.stopAndDrain()
        await devices?.shutdown()
        if await shares?.shutdown() == false { error = "Nearby share native IO is still retiring" }
        if await internetShares?.shutdown() == false { error = "Internet share native IO is still retiring" }
        await updates?.shutdown()
        if await modules?.shutdown() == false { error = "ModuleStopUnconfirmed" }
        await downloads?.shutdown()
        activities.stop()
        do { try await store?.finalizeOnShutdown(); if let store { try await payloads.reconcile(store) } } catch { self.error = error.localizedDescription }
    }
}
