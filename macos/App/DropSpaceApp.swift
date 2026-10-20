import SwiftUI
import AppKit
import DropSpaceCore
import DropSpaceInfrastructure
import DropSpacePlatform

@main struct DropSpaceMacApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @State private var model = WorkspaceModel()
    var body: some Scene {
        Window("DropSpace",id: "workspace") {
            MainView(model: model)
                .task { delegate.model = model; model.requestQuit = { delegate.requestQuit() }; await model.start() }
        }
        .defaultSize(width: 1060,height: 720)
        .commands { WorkspaceCommands(model: model) }
        Window("DropSpace Settings",id: "settings") { SettingsView(model: model).frame(minWidth: 720,minHeight: 560) }.defaultSize(width: 820,height: 660)
        MenuBarExtra("DropSpace",systemImage: "tray.and.arrow.down") {
            Button("Open DropSpace") { model.openMainWindow?() }
            Button(model.settings.clipboardPaused ? "Resume Clipboard" : "Pause Clipboard") { model.update { $0.clipboardPaused.toggle() } }
            Button("Show Island") { model.island?.showFromHotkey() }
            Button("Settings…") { model.openSettings?() }
            Divider()
            Button("Quit DropSpace") { model.requestQuit?() }.keyboardShortcut("q")
        }
    }
}
@MainActor final class AppDelegate: NSObject,NSApplicationDelegate,NSWindowDelegate {
    weak var model: WorkspaceModel? {
        didSet {
            guard let model else { return }
            let files = pendingFiles; pendingFiles.removeAll()
            for request in files { model.intake(request.urls, acquisition: request.acquisition) }
            let texts = pendingTexts; pendingTexts.removeAll()
            for request in texts { model.addText(request.text, acquisition: request.acquisition) }
        }
    }
    private let instance = SingleInstance()
    private var pendingFiles: [WorkspaceModel.PendingIntake] = []
    private var pendingTexts: [(text: String, acquisition: String)] = []
    private var quitting = false
    private var quitSheetObserver: NSObjectProtocol?
    private var explainingClose = false
    private var closeExplanationTask: Task<Void, Never>?
    func applicationWillFinishLaunching(_ notification: Notification) {
        let override = ProcessInfo.processInfo.environment["DROPSPACE_DATA_DIRECTORY"].map { URL(fileURLWithPath:$0,isDirectory:true) }
        do {
            guard try instance.acquire(directory:StoragePaths(root:override).root,onActivate:{ [weak self] in self?.model?.openMainWindow?() }) else {
                // No database, clipboard monitor or worker exists before this launch gate.
                Darwin.exit(EXIT_SUCCESS)
            }
        } catch {
            let alert = NSAlert(); alert.messageText = "DropSpace could not open its data directory"; alert.informativeText = error.localizedDescription; alert.runModal()
            Darwin.exit(EXIT_FAILURE)
        }
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.servicesProvider = self
        NSUpdateDynamicServices()
        NSApp.activate(ignoringOtherApps: true)
        NotificationCenter.default.addObserver(self,selector: #selector(windowBecameMain(_:)),name: NSWindow.didBecomeMainNotification,object: nil)
        // SwiftUI may have created/made the initial scene main before this
        // delegate callback. Future notifications alone miss its first close.
        for window in NSApp.windows { attachWorkspaceLifecycle(window) }
    }
    @objc private func windowBecameMain(_ notification: Notification) {
        if let window = notification.object as? NSWindow { attachWorkspaceLifecycle(window) }
    }
    private func attachWorkspaceLifecycle(_ window: NSWindow) {
        if window.identifier?.rawValue == "workspace" || window.title == "DropSpace" { window.delegate = self }
    }
    func applicationShouldHandleReopen(_ sender: NSApplication,hasVisibleWindows flag: Bool) -> Bool { model?.openMainWindow?(); return true }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard let model, model.ready else { requestQuit(); return false }
        if model.settings.closeBehavior == .exit { requestQuit(); return false }
        guard !model.isQuitting else { return false }
        if model.settings.closeExplanationShown { sender.orderOut(nil); return false }
        guard !explainingClose, sender.attachedSheet == nil else { return false }
        explainingClose = true
        let alert = NSAlert()
        alert.messageText = model.ui("CloseToTrayTitle")
        alert.informativeText = model.ui("CloseToTrayContent")
        alert.addButton(withTitle: model.ui("CommonAcknowledge"))
        alert.beginSheetModal(for: sender) { [weak self, weak sender, weak model] response in
            guard let self else { return }
            guard response == .alertFirstButtonReturn, let model, !model.isQuitting else {
                explainingClose = false; return
            }
            closeExplanationTask = Task { [weak self, weak sender] in
                defer { self?.explainingClose = false; self?.closeExplanationTask = nil }
                // Merge only this acknowledgement into the latest settings.
                // A failed atomic save leaves the window visible and notice retryable.
                await model.update { $0.closeExplanationShown = true }.value
                guard !Task.isCancelled, !model.isQuitting, model.settings.closeExplanationShown else { return }
                if model.settings.closeBehavior == .exit { self?.requestQuit() }
                else { sender?.orderOut(nil) }
            }
        }
        return false
    }
    func requestQuit() {
        closeExplanationTask?.cancel()
        let hasSheets = NSApp.windows.contains { $0.attachedSheet != nil }
        if hasSheets && quitSheetObserver == nil {
            quitSheetObserver = NotificationCenter.default.addObserver(forName:NSWindow.didEndSheetNotification,object:nil,queue:.main) { [weak self] _ in
                Task { @MainActor in self?.finishQuitAfterSheets() }
            }
        }
        model?.showsOnboarding = false; model?.showsTextIntake = false; model?.imageExport.dismiss()
        model?.isQuitting = true
        model?.shares?.showsPanel = false; model?.internetShares?.showsPanel = false
        model?.devices?.fileTransfers.showsPanel = false
        model?.previewURL = nil; model?.textPreview = nil; model?.hashResult = nil; model?.lastOutput = nil
        for window in NSApp.windows {
            if let panel = window as? NSOpenPanel { panel.cancel(nil) }
            if let sheet = window.attachedSheet { window.endSheet(sheet) }
        }
        // AppKit cancels termination while a sheet's dismissal animation is active.
        // Wait for the actual end-sheet notification rather than a timing delay.
        if !hasSheets { finishQuitAfterSheets() }
    }
    private func finishQuitAfterSheets() {
        guard !NSApp.windows.contains(where:{ $0.attachedSheet != nil }) else { return }
        if let quitSheetObserver { NotificationCenter.default.removeObserver(quitSheetObserver); self.quitSheetObserver = nil }
        // terminateLater enters a nested AppKit event loop. Invoking terminate
        // inside a Swift task or a main-dispatch block holds that executor and
        // prevents our async shutdown/reply task from making progress.
        RunLoop.main.perform(inModes:[.common]) { MainActor.assumeIsolated { NSApp.terminate(nil) } }
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !quitting else { return .terminateLater }; quitting = true
        for window in sender.windows { if let sheet = window.attachedSheet { window.endSheet(sheet) } }
        Task { await model?.shutdown(); instance.release(); sender.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
    func application(_ application: NSApplication,open urls: [URL]) {
        let files = urls.filter(\.isFileURL)
        submitFiles(files, acquisition: "macos-open-files")
    }
    private func submitFiles(_ urls: [URL], acquisition: String) {
        guard !urls.isEmpty else { return }
        if let model { model.intake(urls, acquisition: acquisition) }
        else { pendingFiles.append(.init(urls: urls, acquisition: acquisition)) }
    }
    private func submitText(_ text: String, acquisition: String) {
        if let model { model.addText(text, acquisition: acquisition) }
        else { pendingTexts.append((text, acquisition)) }
    }
    @objc func addToDropSpace(_ pasteboard: NSPasteboard,userData: String?,error: AutoreleasingUnsafeMutablePointer<NSString?>) {
        if let urls = pasteboard.readObjects(forClasses: [NSURL.self],options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty { submitFiles(urls, acquisition: "macos-finder-services") }
        else if let text = pasteboard.string(forType: .string) { submitText(text, acquisition: "macos-service-text") }
    }
}
struct WorkspaceCommands: Commands {
    let model: WorkspaceModel
    @Environment(\.openWindow) private var openWindow
    var body: some Commands {
        CommandGroup(replacing:.appTermination) { Button("Quit DropSpace") { model.requestQuit?() }.keyboardShortcut("q") }
        CommandGroup(replacing: .newItem) {
            Button("Add Files…") { model.chooseFiles() }.keyboardShortcut("o")
            Button("Add Text or URL…") { model.showsTextIntake = true }.keyboardShortcut("n")
        }
        CommandGroup(replacing: .undoRedo) { Button("Undo") { model.undo() }.configuredShortcut("Undo",model: model).disabled(model.undoReceipt == nil) }
        CommandGroup(replacing: .appSettings) {
            Button("Settings…") { openWindow(id: "settings") }.configuredShortcut("Settings",model: model)
            Button("Settings… (Alternate)") { openWindow(id: "settings") }.configuredShortcut("SettingsAlternate",model: model)
        }
        CommandGroup(after: .textEditing) {
            Button("Search Items") { model.searchFocusRequest = UUID() }.configuredShortcut("Search",model: model)
            Button("Search Items (Alternate)") { model.searchFocusRequest = UUID() }.configuredShortcut("SearchAlternate",model: model)
        }
        CommandMenu("Navigate") {
            Button(model.ui("NavSpace.Content")) { model.section = "space"; model.refresh() }.configuredShortcut("Space",model: model)
            Button(model.ui("NavClipboard.Content")) { model.section = "clipboard"; model.refresh() }.configuredShortcut("Clipboard",model: model)
            Button(model.ui("NavPinned.Content")) { model.section = "pinned"; model.refresh() }.configuredShortcut("Pinned",model: model)
            Button("Music") { model.section = "music" }
        }
    }
}
