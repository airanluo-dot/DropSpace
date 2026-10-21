import AppKit
import SwiftUI
import DropSpaceCore
import DropSpacePlatform

struct IslandView: View {
    let controller: IslandController
    let model: WorkspaceModel
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    @Environment(\.colorSchemeContrast) private var contrast
    var body: some View {
        DropContainer(onFiles: model.intake, onText: model.addText, onPromised: model.intakePromised, onFailure: { model.error = $0 },
                      onDragPresence: controller.dragPresence, onDropAccepted: controller.dropAccepted) {
            ZStack {
                if controller.motion.compact > 0.001 {
                    Button(action: controller.open) { compact }
                        .buttonStyle(.plain).frame(width: controller.compactGeometry.width, height: controller.compactGeometry.height)
                        .scaleEffect(controller.compactScale).opacity(controller.motion.compact)
                        .allowsHitTesting(controller.snapshot.state == .compact)
                        .accessibilityLabel(model.ui("OverlayTitle")).accessibilityValue(compactAccessibleValue)
                        .accessibilityIdentifier("island-compact")
                }
                if controller.motion.drag > 0.001 {
                    VStack(spacing: 4) {
                        Text(model.ui(controller.snapshot.state == .dragReady ? "OverlayDropTitle" : model.settings.fileDragWakeMode == .classicTopEdge ? "OverlayClassicDragTitle" : "OverlaySmartDragTitle")).font(.system(size: 17, weight: .semibold))
                        Text(model.ui(controller.snapshot.state == .dragReady ? "OverlayDropSubtitle" : "OverlayDragSubtitle")).font(.system(size: 12)).foregroundStyle(.secondary)
                    }.opacity(controller.motion.drag).allowsHitTesting(false)
                }
                if controller.motion.expanded > 0.001 {
                    expanded.frame(width: 560, height: 340).scaleEffect(controller.expandedScale).opacity(controller.motion.expanded).allowsHitTesting(controller.expanded)
                }
                if controller.experience.expandedDropActive {
                    VStack(spacing: 5) {
                        Image(systemName: "tray.and.arrow.down").font(.system(size: 24))
                        Text(model.ui("OverlayDropTargetTitle.Text")).font(.system(size: 17, weight: .semibold))
                        Text(model.ui("OverlayDropTargetSubtitle.Text")).font(.system(size: 12))
                    }.frame(maxWidth: .infinity, maxHeight: .infinity).background(.regularMaterial)
                        .clipShape(RoundedRectangle(cornerRadius: 22)).overlay(RoundedRectangle(cornerRadius: 22).stroke(.tint, lineWidth: 2))
                        .padding(8).allowsHitTesting(false)
                }
                if controller.editingPosition {
                    VStack(spacing: 6) {
                        Text(model.ui("OverlayPlacementEditHint.Text")).font(.system(size: 12))
                        HStack(spacing: 8) { Button(model.ui("Cancel"), action: controller.cancelPosition); Button(model.ui("ApplyIslandPlacementButton.Content"), action: controller.savePosition) }
                    }.frame(maxWidth: .infinity, maxHeight: .infinity).background(.regularMaterial)
                }
            }.frame(maxWidth: .infinity, maxHeight: .infinity)
                .background { if reduceTransparency { Color(nsColor: .windowBackgroundColor) } else { Rectangle().fill(.regularMaterial) } }
                .overlay { if controller.hovered && controller.snapshot.state == .compact { Color.primary.opacity(0.035).allowsHitTesting(false) } }
                .clipShape(RoundedRectangle(cornerRadius: controller.motion.radius))
                .overlay { if contrast == .increased { RoundedRectangle(cornerRadius: controller.motion.radius).stroke(.primary, lineWidth: 1) } }
                .onHover(perform: controller.hover)
        }.accessibilityIdentifier("island-surface").environment(\.locale, Locale(identifier: model.ui.language))
    }
    private var compactAccessibleValue: String {
        switch controller.snapshot.content {
        case .music:
            let media = model.media.snapshot
            guard model.settings.lyrics.enabled, model.settings.islandActivity.showLyricsInCompact,
                  let document = model.lyrics?.document, document.match?.trackIdentity == media.trackID else { return media.title }
            let display = LyricsDisplay.presentation(document, position: media.position, delay: model.settings.lyrics.delayMilliseconds)
            guard !display.interlude, let index = display.lineIndex else { return media.title }
            let line = document.lines[index]
            return media.title + "\n" + line.text + (LyricsDisplay.secondary(line, target: model.settings.lyricsTranslationTarget.rawValue,
                enabled: model.settings.lyrics.secondaryLyrics, showAI: model.settings.lyrics.showAiLyricsLabel).map { "\n" + $0 } ?? "")
        case .notification, .volume:
            return model.activities.current.map { $0.title + "\n" + $0.body } ?? ""
        default:
            return model.islandSpaceCount == 1 ? model.islandSpace.first?.title ?? "" :
                model.islandSpaceCount > 1 ? model.ui("OverlayItemCount", String(model.islandSpaceCount)) : ""
        }
    }
    @ViewBuilder private var compact: some View {
        switch controller.snapshot.content {
        case .notification, .volume:
            if let activity = model.activities.current {
                HStack(spacing: 12) {
                    Image(systemName: activity.volume == nil ? "bell.fill" : activity.volume!.muted ? "speaker.slash.fill" : "speaker.wave.2.fill").font(.system(size: 24))
                    VStack(alignment: .leading, spacing: 3) {
                        Text(activity.title).fontWeight(.semibold).lineLimit(1)
                        Text(activity.body).lineLimit(1).foregroundStyle(.secondary)
                        if let volume = activity.volume { ProgressView(value: Double(volume.percent), total: 100) }
                    }
                }.padding(.horizontal, 18)
            }
        case .music:
            HStack(spacing: 12) {
                if model.settings.islandActivity.showArtwork { IslandArtwork(model: model, side: 24, radius: 6) }
                CompactLyricsView(model: model)
                SpectrumView(model: model, active: controller.snapshot.state == .compact, height: 22)
            }.padding(.horizontal, 14)
        default:
            if controller.snapshot.variant == .singleFile || controller.snapshot.variant == .multipleFiles {
                HStack(spacing: 12) {
                    IslandLogo().frame(width: 24, height: 24)
                    Text(model.islandSpaceCount == 1 ? model.islandSpace.first?.title ?? "" : model.ui("OverlayItemCount", String(model.islandSpaceCount))).fontWeight(.semibold).lineLimit(1).frame(maxWidth: .infinity)
                    Image(systemName: "chevron.down").font(.system(size: 16)).foregroundStyle(.secondary)
                }.padding(.horizontal, 18)
            } else if controller.snapshot.variant == .idle, model.settings.islandAppearance.resident,
                      let contribution = model.modules?.contributions.first, let manifest = contribution.manifest, let content = contribution.island {
                HStack(spacing: 8) {
                    if model.settings.islandAppearance.showLogoWhenIdle { IslandLogo().frame(width: 24, height: 24) }
                    Text(ModuleContract.text(manifest, content.compact, language: model.ui.language)).lineLimit(1)
                }.padding(.horizontal, 14)
            } else if model.settings.islandAppearance.showLogoWhenIdle { IslandLogo().frame(width: 24, height: 24) }
        }
    }
    private var expanded: some View {
        ZStack {
            ForEach(IslandPage.allCases, id: \.rawValue) { page in
                if controller.pages[page.rawValue] > 0.001 {
                    pageView(page).opacity(controller.pages[page.rawValue]).allowsHitTesting(controller.page == page).accessibilityHidden(controller.page != page)
                }
            }
            if controller.page != .widgets { rail(previous: true).frame(maxWidth: .infinity, alignment: .leading) }
            if controller.page != .clipboard { rail(previous: false).frame(maxWidth: .infinity, alignment: .trailing) }
            if controller.page != .files {
                Button(action: controller.collapse) { Image(systemName: "chevron.up").frame(width: 34, height: 34) }.buttonStyle(.plain)
                    .accessibilityLabel("Collapse").accessibilityIdentifier("island-collapse")
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing).padding(.top, 4).padding(.trailing, 22)
            }
        }
    }
    @ViewBuilder private func pageView(_ page: IslandPage) -> some View {
        switch page {
        case .files: IslandItems(model: model, clipboard: false, collapse: controller.collapse)
        case .clipboard: IslandItems(model: model, clipboard: true, collapse: controller.collapse).padding(.horizontal, 18)
        case .music: IslandMusicView(model: model, active: controller.snapshot.state == .expanded && controller.page == .music).padding(.horizontal, 18)
        case .widgets:
            Group { if model.settings.widgets.enabled { WidgetGrid(model: model, runtime: controller.runtime) } else { Text(model.ui("WidgetsEmpty.Text")) } }
                .padding(.horizontal, 40).padding(.vertical, 28)
                .overlay(alignment: .bottom) { if let modules = model.modules, !modules.contributions.isEmpty { ModuleIslandStrip(model: model, modules: modules) } }
        }
    }
    private func rail(previous: Bool) -> some View {
        Button { if let page = IslandPage(rawValue: controller.page.rawValue + (previous ? -1 : 1)) { controller.choosePage(page) } }
        label: { Capsule().fill(.secondary).frame(width: 4, height: 38).frame(width: 22, height: 100) }
            .buttonStyle(.plain).accessibilityLabel(previous ? "Previous page" : "Next page")
            .accessibilityIdentifier(previous ? "island-previous-page" : "island-next-page")
    }
}
struct IslandLogo: View {
    private var image: NSImage { Bundle.main.url(forResource: "DropSpaceLogo", withExtension: "png").flatMap(NSImage.init(contentsOf:)) ?? NSApp.applicationIconImage }
    var body: some View { Image(nsImage: image).resizable().scaledToFit().accessibilityHidden(true) }
}
struct IslandArtwork: View {
    let model: WorkspaceModel
    let side: CGFloat, radius: CGFloat
    var body: some View {
        Group {
            if let bytes = model.media.snapshot.artwork, let image = NSImage(data: bytes) { Image(nsImage: image).resizable().scaledToFill() }
            else { Image(systemName: "music.note").resizable().scaledToFit().padding(side / 5) }
        }.frame(width: side, height: side).background(.quaternary).clipShape(RoundedRectangle(cornerRadius: radius)).accessibilityHidden(true)
    }
}
struct IslandItems: View {
    let model: WorkspaceModel
    let clipboard: Bool
    let collapse: () -> Void
    private var items: [DropItem] { clipboard ? model.islandClipboard : model.islandSpace }
    var body: some View {
        VStack(spacing: clipboard ? 8 : 12) {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(model.ui(clipboard ? "ClipboardIslandTitle.Text" : "OverlayExpandedTitle.Text")).font(.system(size: clipboard ? 20 : 18, weight: .semibold))
                    if !clipboard { Text(model.ui("OverlayExpandedSubtitle.Text").replacingOccurrences(of: "Explorer", with: "Finder")).font(.system(size: 12)).foregroundStyle(.secondary) }
                }
                Spacer()
                if clipboard { Button(model.ui(model.settings.clipboardPaused ? "ClipboardIslandResume" : "ClipboardIslandPause")) { model.update { $0.clipboardPaused.toggle() } }.padding(.trailing, 24) }
                else { Button(action: collapse) { Image(systemName: "chevron.up").frame(width: 34, height: 34) }.buttonStyle(.plain).accessibilityLabel("Collapse").accessibilityIdentifier("island-collapse") }
            }
            ScrollView { LazyVStack(alignment: .leading, spacing: 4) { ForEach(items) {
                IslandItemRow(item: $0, model: model, clipboard: clipboard)
                    .nativeDragRegion($0.id, space: clipboard ? "island-clipboard-drag" : "island-space-drag")
            } } }
                .nativeDragSource(space: clipboard ? "island-clipboard-drag" : "island-space-drag", prepare: { id in
                    try await model.prepareNativeDrag(id, rows: items, useSelection: false)
                }, failure: { model.error = $0 })
                .overlay { if clipboard && items.isEmpty { Text(model.ui("ClipboardIslandEmpty.Text")).foregroundStyle(.secondary) } }
            HStack(spacing: 8) {
                if !clipboard { Text(model.ui("OverlayRemoveHint.Text")).font(.system(size: 11)).foregroundStyle(.tertiary) }
                Spacer(minLength: 0)
                Button(model.ui(clipboard ? "ClipboardIslandOpenMain.Content" : "OverlayOpenMainButton.Content")) { model.section = clipboard ? "clipboard" : "space"; model.refresh(); model.openMainWindow?() }
            }
        }.padding(.horizontal, clipboard ? 24 : 18).padding(.top, clipboard ? 18 : 14).padding(.bottom, clipboard ? 18 : 16)
    }
}
struct IslandItemRow: View {
    let item: DropItem; let model: WorkspaceModel; let clipboard: Bool
    var body: some View {
        HStack(spacing: clipboard ? 8 : 10) {
            if !clipboard { ItemThumbnail(item: item, model: model, side: 36) }
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title).lineLimit(1)
                Text(item.text ?? item.file?.path ?? "").font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                if !clipboard { IslandQuickActions(item: item, model: model) }
            }.frame(maxWidth: .infinity, alignment: .leading)
            if clipboard { Button(model.ui("ClipboardIslandCopy.Content")) { model.copy([item]) } }
            else { Button { model.open(item) } label: { Image(systemName: "arrow.up.forward") }.help("Open") }
            Button { model.pin([item], !item.isPinned) } label: { Image(systemName: item.isPinned ? "pin.fill" : "pin") }.help(item.isPinned ? "Unpin" : "Pin")
            Button { model.remove([item]) } label: { Image(systemName: "xmark") }.help("Remove reference")
        }.buttonStyle(.borderless).padding(.horizontal, clipboard ? 0 : 8).padding(.vertical, 5).frame(minHeight: clipboard ? 44 : 48)
    }
}
struct IslandQuickActions: View {
    let item: DropItem; let model: WorkspaceModel
    var body: some View {
        let partition = QuickActionRules.partition(available: QuickActionRules.localCapabilities([item]), items: [item], preferences: model.settings.quickActionPreferences)
        HStack(spacing: 4) { ForEach(partition.primary, id: \.self) { action in Button(model.ui(action.labelResource)) { model.perform(action, [item]) }.font(.system(size: 11)) } }
    }
}
