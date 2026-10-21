import SwiftUI
import AppKit
import DropSpaceCore
import DropSpacePlatform

struct MusicApplicationsView: View {
    let model: WorkspaceModel
    private var sources: [String] {
        MediaSourcePolicy.displayed(available: model.media.availablePlayers, settings: model.settings.islandActivity)
    }
    var body: some View {
        SettingsGroup(model.ui("MusicApplications")) {
            Toggle(model.ui("MusicAllApplications"), isOn: Binding(
                get: { !MediaSourcePolicy.restricted(model.settings.islandActivity) },
                set: { all in
                    let available = model.media.availablePlayers
                    model.update { MediaSourcePolicy.setAll(all, available: available, settings: &$0.islandActivity) }
                })).accessibilityIdentifier("music-all-applications")
            Text(model.ui("Mac.MediaApplicationsHelp")).font(.caption).foregroundStyle(.secondary)
            if sources.isEmpty { Text(model.ui("MusicApplicationsEmpty")).foregroundStyle(.secondary) }
            ForEach(sources, id: \.self) { source in
                let displayed = sources
                Toggle(isOn: Binding(
                    get: { MediaSourcePolicy.allows(source, settings: model.settings.islandActivity) },
                    set: { selected in
                        model.update {
                            MediaSourcePolicy.setSelected(source, selected: selected, displayed: displayed, settings: &$0.islandActivity)
                        }
                    })) {
                        MediaApplicationLabel(source: source)
                    }.accessibilityIdentifier("music-source-" + source)
            }
        }.disabled(model.isQuitting)
    }
}

private struct MediaApplicationLabel: View {
    let source: String
    @State private var name = ""
    @State private var icon: NSImage?
    var body: some View {
        HStack(spacing: 12) {
            if let icon { Image(nsImage: icon).resizable().frame(width: 28, height: 28).accessibilityHidden(true) }
            VStack(alignment: .leading, spacing: 2) {
                Text(name.isEmpty ? source : name)
                Text(source).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }
        }.task(id: source) {
            // Row-local icons are released when the source disappears. No global
            // registry or filesystem scan is needed to resolve one application.
            let canonical = NativeMediaService.players.keys.first { $0.lowercased() == source.lowercased() } ?? source
            name = NativeMediaService.players[canonical] ?? source
            if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: canonical) {
                name = Bundle(url: url)?.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String ?? name
                icon = NSWorkspace.shared.icon(forFile: url.path)
            } else { icon = nil }
        }
    }
}
