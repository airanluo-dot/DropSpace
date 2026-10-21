import SwiftUI
import DropSpaceCore
import DropSpacePlatform

struct MusicView: View {
    let model: WorkspaceModel
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                SettingsCard {
                    VStack(alignment: .leading, spacing: 0) {
                        HStack {
                            if model.musicRestart.state != .idle {
                                Text(model.ui(model.musicRestart.state == .reconnecting ? "MusicReconnecting" :
                                    model.musicRestart.state == .succeeded ? "MusicReconnected" : "MusicReconnectFailed"))
                                    .font(.caption).foregroundStyle(model.musicRestart.state == .failed ? .red : .secondary)
                                    .accessibilityIdentifier("music-reconnect-status")
                            }
                            Spacer()
                            Button { model.reconnectMusic() } label: { Label(model.ui("MusicRefresh"), systemImage: "arrow.clockwise") }
                                .help(model.ui("MusicRefreshHelp")).disabled(model.musicRestart.isRetiring)
                                .accessibilityIdentifier("music-reconnect")
                        }.padding(12)
                        IslandMusicView(model: model, isIsland: false)
                            .frame(height: model.media.snapshot.title.isEmpty ? 100 : 380)
                        if model.media.snapshot.title.isEmpty, model.settings.islandActivity.enableMediaActivity {
                            ForEach(model.media.availablePlayers.filter {
                                MediaSourcePolicy.allows($0, settings: model.settings.islandActivity)
                            }, id: \.self) { id in
                                Button(model.ui("Mac.MediaConnectPlayer", NativeMediaService.players[id] ?? id)) { Task { await model.media.connect(id) } }
                                    .disabled(model.musicRestart.isRetiring)
                            }.padding(.horizontal, 28).padding(.bottom, 16)
                        }
                        SpectrumStatus(model: model).padding(.horizontal, 28)
                    }
                }
                NeteaseControlsView(model: model)
                if model.settings.lyrics.enabled { SettingsCard { LyricsView(model: model).padding(2) } }
                MusicPreferences(model: model)
            }.frame(maxWidth: 780, alignment: .leading).padding(24).frame(maxWidth: .infinity, alignment: .leading)
        }.accessibilityIdentifier("music-page-scroll")
    }
}
