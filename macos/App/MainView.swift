import SwiftUI
import AppKit
import DropSpaceCore
import DropSpacePlatform
import UniformTypeIdentifiers

struct MainView: View {
    @Bindable var model: WorkspaceModel
    @Environment(\.openWindow) private var openWindow
    @FocusState private var searchFocused: Bool
    private var sectionName: String { model.section == "space" ? "Space" : model.section == "pinned" ? "Pinned" : "Clipboard" }
    @State private var confirmClear = false
    var body: some View {
        let projection = model.batchProjection
        GeometryReader { viewport in
        HStack(spacing: 0) {
            WorkspaceNavigation(model: model)
            Divider()
            VStack(spacing: 0) {
                if let selected = model.selectedModule, let modules = model.modules {
                    NativeModulePage(model: model, modules: modules, manifest: selected.manifest, page: selected.page).id(model.section + "/" + selected.manifest.version)
                } else {
                WorkspaceHeader(model: model, searchFocused: $searchFocused, wide: viewport.size.width >= 860)
                if let counts = model.intakeCounts {
                    HStack {
                        Text(counts.rejected == 0 ? model.ui("ItemsAdded", String(counts.accepted)) : model.ui("ItemsAddedWithRejected", String(counts.accepted), String(counts.rejected)))
                        Spacer()
                        Button(model.ui("CommonClose")) { model.intakeCounts = nil }
                    }.padding(12).padding(.horizontal, 28).accessibilityIdentifier("file-intake-result")
                }
                if let promises = model.promisedFiles, promises.active || promises.issueKey != nil || promises.result != nil {
                    VStack(alignment: .leading, spacing: 6) {
                        if promises.active {
                            HStack {
                                ProgressView().controlSize(.small)
                                Text(promises.cancelling ? model.ui("Mac.Promise.cancelling") : model.ui("Mac.Promise.receiving", String(promises.received), String(promises.expected)))
                                Spacer()
                                if !promises.cancelling { Button(model.ui("CommonCancel"), action: promises.cancel) }
                            }
                        }
                        if let message = promises.issueMessage(model.ui) {
                            HStack(alignment: .top) {
                                Text(message).textSelection(.enabled)
                                Spacer()
                                if promises.canRetry { Button(model.ui("Mac.Promise.retry"), action: promises.retry) }
                                if !promises.active { Button(model.ui("CommonClose"), action: promises.dismissIssue) }
                            }
                        }
                    }.padding(12).padding(.horizontal, 28).accessibilityIdentifier("promised-file-progress")
                }
                if let receipt = model.undoReceipt {
                    HStack { Text(model.ui(receipt.kind == .remove ? "UndoRemovedItems" : receipt.pinValue == false ? "UndoUnpinned" : "UndoPinned", String(receipt.count))); Spacer(); Button(model.ui("UndoButton.Content"), action: model.undo) }
                        .padding(12).background(.quaternary, in: RoundedRectangle(cornerRadius: 8)).padding(.horizontal, 28).padding(.bottom, 10)
                }
                if model.section == "music" { MusicView(model: model) }
                else {
                    DropContainer(onFiles: { model.intake($0, acquisition: "main-window-drop") },
                                  onText: { model.addText($0, acquisition: "main-window-text-drop") },
                                  onURL: { model.addText($0, acquisition: "main-window-url-drop") },
                                  onPromised: model.intakePromised, onFailure: { model.error = $0 }) {
                        VStack(spacing: 0) {
                            if model.items.isEmpty {
                                VStack(spacing: 12) {
                                    Image(systemName: "folder").font(.system(size: 42))
                                    Text(model.ui("Empty" + sectionName + "Title")).font(.system(size: 20, weight: .semibold))
                                    Text(model.ui("Empty" + sectionName + "Description")).foregroundStyle(.secondary).multilineTextAlignment(.center)
                                }.frame(maxWidth: 420).frame(maxWidth: .infinity, maxHeight: .infinity)
                            } else {
                                List(selection: $model.selection) {
                                    ForEach(projection.visible) { item in
                                        ItemRow(item: item, model: model, isHeader: projection.headers.contains(item.id)) { itemMenu(item) }.tag(item.id)
                                            .contextMenu { itemMenu(item) }
                                            .onTapGesture(count: 2) { model.open(item) }
                                            .nativeDragRegion(item.id, space: "workspace-file-drag")
                                            .accessibilityElement(children: .contain)
                                            .accessibilityIdentifier("workspace-item-" + item.id.uuidString)
                                            .listRowInsets(EdgeInsets(top: 0, leading: 0, bottom: 4, trailing: 0))
                                            .listRowSeparator(.hidden)
                                    }
                                }.listStyle(.plain).scrollContentBackground(.hidden)
                                    .nativeDragSource(space: "workspace-file-drag", prepare: { id in
                                        try await model.prepareNativeDrag(id, rows: model.batchProjection.visible, useSelection: true)
                                    }, failure: { model.error = $0 })
                                    .onDeleteCommand { model.remove(model.selected) }
                            }
                            if model.hasMoreItems { Button("Load More", action: model.loadMore).padding(8) }
                        }.padding(.horizontal, 28).padding(.bottom, 28)
                    }
                    HStack {
                        Menu {
                            Picker("Type", selection: $model.kindFilter) { Text("All Types").tag(ItemKind?.none); ForEach(ItemKind.allCases, id: \.self) { Text(model.ui($0.labelResource)).tag(Optional($0)) } }
                            Picker("Status", selection: $model.statusFilter) { Text("All Statuses").tag(ItemStatus?.none); ForEach(ItemStatus.allCases, id: \.self) { Text(String(describing: $0)).tag(Optional($0)) } }
                        } label: { Label("Filter", systemImage: "line.3.horizontal.decrease") }
                        Spacer()
                        if let shares = model.shares { NearbySharesButton(shares: shares, model: model) }
                        if let shares = model.internetShares { InternetSharesButton(shares: shares, model: model) }
                        if let devices = model.devices { FileTransfersButton(devices: devices, ui: model.ui) }
                        if model.section == "clipboard" { Button("Clear History", systemImage: "trash") { confirmClear = true } }
                    }.padding(.horizontal, 28).padding(.bottom, 10)
                }
                }
            }
        }
        }.frame(minWidth: 780,minHeight: 480).environment(\.locale, Locale(identifier: model.ui.language))
        .overlay(alignment: .bottom) {
            if let error = model.error {
                HStack(alignment: .top) { Image(systemName: "exclamationmark.triangle"); Text(error).textSelection(.enabled); Spacer(); Button { model.error = nil } label: { Image(systemName: "xmark") }.buttonStyle(.plain) }.padding().background(.regularMaterial,in: RoundedRectangle(cornerRadius: 12)).padding().accessibilityIdentifier("statusError")
            }
        }
        .onAppear {
            model.openMainWindow = { openWindow(id: "workspace"); NSApp.activate(ignoringOtherApps: true) }
            model.openSettings = { openWindow(id: "settings"); NSApp.activate(ignoringOtherApps: true) }
        }
        .onChange(of: model.searchFocusRequest) { searchFocused = true }
        .onChange(of: model.section) { model.resetQuery() }
        .onChange(of: model.search) { model.resetQuery() }
        .onChange(of: model.kindFilter) { model.resetQuery() }
        .onChange(of: model.statusFilter) { model.resetQuery() }
        .sheet(isPresented: $model.showsTextIntake) { TextIntakeView(model: model) }
        .sheet(isPresented: $model.showsOnboarding) { OnboardingView(model: model).interactiveDismissDisabled() }
        .sheet(item: Binding(get: { model.imageExport.request }, set: { if $0 == nil { model.imageExport.dismiss() } })) { request in
            ImageTransformView(model: model, request: request)
        }
        .sheet(item: $model.textPreview) { item in
            VStack(alignment: .leading, spacing: 12) {
                Text(item.title).font(.title2)
                NativeTextPreview(text: item.text ?? "", monospaced: item.kind == .code).frame(minWidth: 620, minHeight: 360)
                HStack { Button(model.ui("CopyItemButton.Content")) { model.copy([item]) }; Spacer(); Button(model.ui("CommonClose")) { model.textPreview = nil }.keyboardShortcut(.cancelAction) }
            }.padding(24)
        }
        .sheet(item: Binding(get: { model.previewURL.map(PreviewItem.init) },set: { model.previewURL = $0?.url })) { item in
            VStack { QuickLookPreview(url: item.url).frame(minWidth: 720,minHeight: 480); Button("Done") { model.previewURL = nil }.keyboardShortcut(.cancelAction).padding() }
        }
        .alert("SHA-256",isPresented: Binding(get: { model.hashResult != nil },set: { if !$0 { model.hashResult = nil } }), presenting: model.hashResult) { digest in
            Button(model.ui("CopyHashResult")) {
                do { model.copy([try FileIntake.text(digest, source: .space, settings: model.settings)]) }
                catch { model.error = error.localizedDescription }
            }
            Button(model.ui("OpenOutputFolder")) { if let url = model.hashOutputURL { NSWorkspace.shared.activateFileViewerSelecting([url]) } }
            Button(model.ui("CommonAcknowledge"),role: .cancel) { }
        } message: { digest in Text(model.ui("HashResultDescription") + "\n\n" + digest) }
        .alert(model.ui("ActionCompleted"),isPresented: Binding(get: { model.lastOutput != nil },set: { if !$0 { model.lastOutput = nil } }), presenting: model.lastOutput) { output in
            Button(model.ui("OpenOutputFolder")) { NSWorkspace.shared.activateFileViewerSelecting([output]) }
            Button(model.ui("CommonAcknowledge"),role: .cancel) { }
        } message: { output in Text(model.ui("ActionOutputSaved", output.path)) }
        .confirmationDialog("Clear unpinned clipboard history?",isPresented: $confirmClear) {
            Button(model.ui("ClearLastHourButton.Content"),role: .destructive) { model.clearClipboard(.lastHour) }
            Button(model.ui("ClearTodayButton.Content"),role: .destructive) { model.clearClipboard(.today) }
            Button("Clear All History",role: .destructive) { model.clearClipboard(.all) }
        }
    }
    @ViewBuilder private func itemMenu(_ item: DropItem) -> some View {
        if item.text != nil { Button(model.ui("PreviewItemMenuItem.Text")) { model.preview(item) } }
        Button(model.ui("OpenItemButton.Content")) { model.open(item) }
        Button(model.ui("CopyItemButton.Content")) { model.copy(model.selection.contains(item.id) ? model.selected : [item]) }
        Button(item.isPinned ? "Unpin" : "Pin") { model.pin([item],!item.isPinned) }
        if item.file != nil || item.payloadPath != nil {
            Button("Quick Look") { model.preview(item) }; Button("Show in Finder") { model.reveal(item) }
            Button("Locate / Replace Reference…") { model.replaceReference(item) }
        }
        if item.kind == .url, let text = item.text, let url = URL(string: text) { Button("Download File…") { model.download(url) } }
        Divider()
        if item.file != nil || item.payloadPath != nil {
            if item.kind != .folder { Button("SHA-256") { model.perform(.hashSha256,[item]) } }
            Button("Create ZIP") { model.perform(.compressZip,model.selection.contains(item.id) ? model.selected : [item]) }
        }
        if item.text != nil { Button("Create QR Code") { model.perform(.generateQr,[item]) } }
        if item.file != nil || item.text != nil || item.kind == .image { Button(model.ui("SendToDeviceAction.Text")) { model.devices?.send(item, selection: model.selection.contains(item.id) ? model.selected : [item]) } }
        if model.settings.enableNearbySharing, item.file != nil || item.payloadPath != nil || item.text != nil {
            Button(model.ui("CreateNearbyLinkAction.Text")) { model.shares?.create(model.selection.contains(item.id) ? model.selected : [item]) }
        }
        if model.settings.enableInternetSharing, item.file != nil || item.payloadPath != nil || item.text != nil {
            Button(model.ui("CreateSecureInternetLinkAction.Text")) { model.internetShares?.create(model.selection.contains(item.id) ? model.selected : [item]) }
        }
        if QuickActionRules.isImage(item) {
            Button(model.ui(QuickAction.resizeImage.labelResource)) { model.perform(.resizeImage,[item]) }
            Button(model.ui(QuickAction.convertImage.labelResource)) { model.perform(.convertImage,[item]) }
            Button(model.ui(QuickAction.stripMetadata.labelResource)) { model.perform(.stripMetadata,[item]) }
        }
        Divider(); Button(model.ui("RemoveItemMenuItem.Text")) { model.remove(model.selection.contains(item.id) ? model.selected : [item]) }
    }

}
private struct PreviewItem: Identifiable { let url: URL; var id: URL { url }; init(_ url: URL) { self.url = url } }
struct ItemRow<More: View>: View {
    let item: DropItem
    let model: WorkspaceModel
    let isHeader: Bool
    @ViewBuilder var more: () -> More
    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            ItemThumbnail(item: item, model: model, side: 48).frame(width: 52)
            VStack(alignment: .leading, spacing: 6) {
                if isHeader, let batch = item.batchID {
                    HStack(spacing: 8) {
                        Text(model.ui("DropBatchLabel", String(item.batchCount ?? model.items.filter { $0.batchID == batch }.count))).font(.system(size: 11, weight: .semibold)).foregroundStyle(.tint)
                        Button(model.ui(model.expandedBatches.contains(batch) ? "CollapseBatch" : "ExpandBatch")) { model.toggleBatch(batch) }.buttonStyle(.plain).font(.system(size: 11))
                            .accessibilityIdentifier("batch-toggle-" + batch.uuidString)
                    }
                }
                HStack(spacing: 8) {
                    Text(item.title).font(.system(size: 14, weight: .semibold)).foregroundStyle(.primary).lineLimit(1).frame(maxWidth: 440, alignment: .leading).fixedSize(horizontal: true, vertical: false)
                    Text(model.ui(item.source == .space ? "ItemSourceSpace" : "ItemSourceClipboard"))
                    Text(model.ui(item.kind.labelResource))
                }.font(.system(size: 12)).foregroundStyle(.secondary)
                Text(item.text ?? item.file?.path ?? "").font(.system(size: 13)).foregroundStyle(.secondary).lineLimit(1).frame(maxWidth: 560, alignment: .leading)
                QuickActionButtons(items: [item], model: model)
                HStack(spacing: 8) {
                    Text(item.createdAt, format: .dateTime.month(.abbreviated).day().hour().minute())
                    if item.status != .available { Text(model.ui("ItemStatus" + String(describing: item.status).capitalized)).foregroundStyle(.red) }
                }.font(.system(size: 11)).foregroundStyle(.tertiary)
                HStack(spacing: 6) {
                    if isHeader, let batch = item.batchID {
                        Button(model.ui("PinBatchButton.Content")) { model.pinBatch(batch) }.accessibilityIdentifier("batch-pin-" + batch.uuidString)
                        Button(model.ui("RemoveBatchButton.Content")) { model.removeBatch(batch) }.accessibilityIdentifier("batch-remove-" + batch.uuidString)
                    }
                    Button(model.ui("OpenItemButton.Content")) { model.open(item) }.disabled(item.status != .available || ![.file, .folder, .url].contains(item.kind))
                    Button(model.ui("CopyItemButton.Content")) { model.copy([item]) }.disabled(item.status != .available || item.kind == .unknown)
                    Button(model.ui(item.isPinned ? "ItemUnpin" : "ItemPin")) { model.pin([item], !item.isPinned) }
                    if item.file != nil && [.missing, .unavailable].contains(item.status) { Button(model.ui("LocateItemButton.Content")) { model.replaceReference(item) } }
                    if item.kind == .image, item.payloadPath != nil { Button(model.ui("ExportItemButton.Content")) { model.exportImage(item) } }
                    Menu(model.ui("MoreItemButton.Content"), content: more)
                }.buttonStyle(.bordered).controlSize(.regular).font(.system(size: 13)).padding(.top, 10)
            }.frame(maxWidth: .infinity, alignment: .leading)
        }.padding(.horizontal, 12).padding(.vertical, 9).frame(minHeight: 76)
            .buttonStyle(.borderless).accessibilityElement(children: .contain)
    }
}
struct ItemInspector: View {
    let item: DropItem; let model: WorkspaceModel
    var body: some View {
        VStack(alignment: .leading,spacing: 16) {
            Text(item.title).font(.headline).textSelection(.enabled)
            if let text = item.text { ScrollView { Text(text).font(item.kind == .code ? .system(.body,design: .monospaced) : .body).textSelection(.enabled).frame(maxWidth: .infinity,alignment: .leading) } }
            else { ResolvedFilePreview(item:item,model:model) }
            HStack { Button("Copy") { model.copy([item]) }; Button(item.isPinned ? "Unpin" : "Pin") { model.pin([item],!item.isPinned) } }
            QuickActionButtons(items:model.selected,model:model)
            Spacer(minLength: 0)
        }.padding()
    }
}

private struct QuickActionButtons: View {
    let items: [DropItem]; let model: WorkspaceModel
    var body: some View {
        let partition = QuickActionRules.partition(available:QuickActionRules.localCapabilities(items),items:items,preferences:model.settings.quickActionPreferences)
        VStack(alignment:.leading) {
            ForEach(partition.primary,id:\.self) { action in Button(model.ui(action.labelResource)) { model.perform(action,items) }.buttonStyle(.bordered).controlSize(.regular) }
            if !partition.more.isEmpty {
                Menu(model.ui("MoreItemButton.Content")) {
                    ForEach(partition.more,id:\.self) { action in Button(model.ui(action.labelResource)) { model.perform(action,items) } }
                }
            }
        }
    }

}

private struct ResolvedFilePreview: View {
    let item: DropItem; let model: WorkspaceModel
    @State private var url: URL?
    @State private var failure: String?
    var body: some View {
        VStack(alignment:.leading) {
            if let path = item.file?.path { Text(path).textSelection(.enabled).font(.caption) }
            if let url { QuickLookPreview(url:url) }
            else if let failure { Text(failure).foregroundStyle(.secondary); Button("Locate File…") { model.replaceReference(item) } }
            else { ProgressView() }
        }.task(id:"\(item.id)-\(item.revision)") {
            url = nil; failure = nil
            do { let resolved = try await model.actions.contentURL(item); try Task.checkCancellation(); url = resolved }
            catch is CancellationError {} catch { failure = error.localizedDescription }
        }
    }
}
