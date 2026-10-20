import AppKit
import SwiftUI
import QuickLookThumbnailing
import UniformTypeIdentifiers
import DropSpaceCore

struct WorkspaceNavigation: View {
    let model: WorkspaceModel
    @State private var expanded = false
    private let pages = [("space", "NavSpace.Content", "folder"), ("clipboard", "NavClipboard.Content", "clipboard"), ("pinned", "NavPinned.Content", "pin"), ("music", "NavMusic.Content", "speaker.wave.2")]
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Button { expanded.toggle() } label: { Image(systemName: "line.3.horizontal").frame(width: 40, height: 36) }.accessibilityLabel("Toggle navigation")
            ScrollView {
            VStack(alignment: .leading, spacing: 4) {
            ForEach(pages, id: \.0) { page in
                Button { model.section = page.0 } label: {
                    HStack(spacing: 12) { Image(systemName: page.2).frame(width: 40, height: 36); if expanded { Text(model.ui(page.1)).lineLimit(1); Spacer() } }
                        .background(model.section == page.0 ? Color.primary.opacity(0.08) : .clear, in: RoundedRectangle(cornerRadius: 6))
                        .overlay(alignment: .leading) { if model.section == page.0 { Capsule().fill(.tint).frame(width: 3, height: 18) } }
                }.help(model.ui(page.1)).accessibilityLabel(model.ui(page.1)).accessibilityIdentifier("navigation-" + page.0)
            }
            ForEach(model.modules?.active ?? [], id: \.installation.id) { snapshot in
                if let manifest = snapshot.manifest {
                    ForEach(manifest.ui.pages, id: \.id) { page in
                        let title = ModuleContract.text(manifest, page.title, language: model.ui.language)
                        Button { model.selectModule(manifest, page: page) } label: {
                            HStack(spacing: 12) { Image(systemName: manifest.ui.icon == "Document" ? "doc" : manifest.ui.icon == "Tools" ? "gearshape" : "puzzlepiece.extension").frame(width: 40, height: 36); if expanded { Text(title).lineLimit(1); Spacer() } }
                                .background(model.section == "module:" + manifest.id + "/" + page.id ? Color.primary.opacity(0.08) : .clear, in: RoundedRectangle(cornerRadius: 6))
                                .overlay(alignment: .leading) { if model.section == "module:" + manifest.id + "/" + page.id { Capsule().fill(.tint).frame(width: 3, height: 18) } }
                        }.help(title).accessibilityLabel(title).accessibilityIdentifier("navigation-module-" + manifest.id + "-" + page.id)
                    }
                }
            }
            }
            }.scrollIndicators(.hidden)
            Button { model.openSettings?() } label: { HStack(spacing: 12) { Image(systemName: "gearshape").frame(width: 40, height: 36); if expanded { Text(model.ui("NavSettings.Content")); Spacer() } } }
                .help(model.ui("NavSettings.Content")).accessibilityLabel(model.ui("NavSettings.Content")).accessibilityIdentifier("navigation-settings")
        }.buttonStyle(.plain).padding(4).frame(width: expanded ? 240 : 48).background(.bar)
    }
}

struct WorkspaceHeader: View {
    @Bindable var model: WorkspaceModel
    var searchFocused: FocusState<Bool>.Binding
    let wide: Bool
    private var page: String { model.section.prefix(1).uppercased() + model.section.dropFirst() }
    private var title: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 10) { Text(model.ui("PageTitle" + page)).font(.system(size: 28, weight: .semibold)); if model.section != "music" { Text(model.ui("ItemCount", String(model.items.count))).foregroundStyle(.secondary) } }
            Text(model.ui("PageDescription" + page)).foregroundStyle(.secondary).frame(maxWidth: 520, alignment: .leading)
            if model.section == "clipboard", model.settings.clipboardPaused { Text(model.ui("ClipboardPausedStatus")).font(.system(size: 12, weight: .semibold)).foregroundStyle(.tint).padding(.top, 5) }
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
    private var search: some View {
        HStack { TextField(model.ui("SearchBox.PlaceholderText"), text: $model.search).textFieldStyle(.plain).focused(searchFocused); Image(systemName: "magnifyingglass").foregroundStyle(.secondary) }
            .padding(8).background(.background, in: RoundedRectangle(cornerRadius: 6)).overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary))
            .accessibilityIdentifier("workspace-search")
    }
    private var add: some View {
        Menu(model.ui("AddButton.Content")) {
            Button(model.ui("AddFilesMenuItem.Text")) { model.chooseFiles() }
            Button(model.ui("AddFolderMenuItem.Text")) { model.chooseFiles(foldersOnly: true) }
            Button(model.ui("AddTextUrlMenuItem.Text")) { model.showsTextIntake = true }
        }.fixedSize().controlSize(.large).accessibilityIdentifier("workspace-add")
    }
    var body: some View {
        Group {
            if wide { HStack(alignment: .top, spacing: 20) { title; if model.section != "music" { search.frame(width: 320); add } } }
            else { VStack(alignment: .leading, spacing: 12) { title; if model.section != "music" { HStack(spacing: 20) { search; add } } } }
        }.padding(.horizontal, 28).padding(.top, 20).padding(.bottom, 14)
    }
}

struct ItemThumbnail: View {
    let item: DropItem
    let model: WorkspaceModel
    let side: CGFloat
    @State private var thumbnail: NSImage?
    var body: some View {
        Group {
            if let thumbnail { Image(nsImage: thumbnail).resizable().scaledToFit().padding(2) }
            else { Image(systemName: item.kind.symbol).font(.system(size: 22)) }
        }.frame(width: side, height: side).background(.quaternary, in: RoundedRectangle(cornerRadius: 8)).accessibilityHidden(true)
            .task(id: "\(item.id)-\(item.revision)") {
                thumbnail = nil
                guard item.file != nil || item.payloadPath != nil else { return }
                do {
                    let url = try await model.checkedContentURL(item)
                    try Task.checkCancellation()
                    thumbnail = NSWorkspace.shared.icon(forFile: url.path)
                    let request = QLThumbnailGenerator.Request(fileAt: url, size: .init(width: side, height: side), scale: NSScreen.main?.backingScaleFactor ?? 2, representationTypes: .all)
                    let result = try await withTaskCancellationHandler {
                        try await QLThumbnailGenerator.shared.generateBestRepresentation(for: request)
                    } onCancel: { QLThumbnailGenerator.shared.cancel(request) }
                    try Task.checkCancellation(); thumbnail = result.nsImage
                } catch { /* The truthful file-type icon survives unsupported previews. */ }
            }
    }
}
extension ItemKind {
    var labelResource: String { "ItemKind" + String(describing: self).prefix(1).uppercased() + String(describing: self).dropFirst() }
    var symbol: String { switch self { case .folder: "folder"; case .file: "doc"; case .image: "photo"; case .url: "link"; case .color: "paintpalette"; case .code: "curlybraces"; default: "text.alignleft" } }
}
extension WorkspaceModel {
    func exportImage(_ item: DropItem) {
        guard item.kind == .image, item.payloadPath != nil else { error = ui("ExportOnlyClipboardImages"); return }
        Task {
            do {
                let source = try await actions.contentURL(item)
                let panel = NSSavePanel(); panel.allowedContentTypes = [source.pathExtension.lowercased() == "jpg" ? .jpeg : .png]
                panel.nameFieldStringValue = "DropSpace-" + Date().formatted(.iso8601).replacingOccurrences(of: ":", with: "-")
                let response = await panel.begin()
                guard response == .OK, let destination = panel.url else { return }
                try await actions.exportOwnedImage(item, to: destination)
                status = ui("ImageExported")
            } catch { self.error = error.localizedDescription }
        }
    }
}
