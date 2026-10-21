import SwiftUI
import DropSpaceCore
import DropSpacePlatform

struct IslandMusicView: View {
    let model: WorkspaceModel
    var active = true
    var isIsland = true
    @State private var editing = false
    @State private var seek = 0.0
    @State private var capturedIdentity: String?
    private var media: MediaSnapshot { model.media.snapshot }
    var body: some View {
        VStack(spacing: 12) {
            HStack(spacing: 18) {
                if model.settings.islandActivity.showArtwork, !media.title.isEmpty { IslandArtwork(model: model, side: 100, radius: 16) }
                VStack(alignment: .leading, spacing: 6) {
                    Text(media.title).font(.system(size: 22, weight: .semibold)).lineLimit(1)
                    Text([media.artist, media.album].filter { !$0.isEmpty }.joined(separator: " · ")).foregroundStyle(.secondary).lineLimit(1)
                    Text(media.sourceName).font(.system(size: 12)).foregroundStyle(.tertiary).lineLimit(1)
                    if media.title.isEmpty { Text(model.ui("MediaNoSession.Text")) }
                }.frame(maxWidth: .infinity, alignment: .leading)
                SpectrumView(model: model, active: active)
            }.frame(height: 100)
            ZStack(alignment: .topLeading) { Color.clear; IslandCurrentLyrics(model: model, showsNextLine: isIsland, contributesGlow: isIsland) }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            VStack(spacing: 12) {
                if model.settings.islandActivity.showProgress && media.canSeek {
                    HStack(spacing: 12) {
                        Text(time(editing ? seek : media.position)).monospacedDigit().frame(minWidth: 48)
                        NativeSeekSlider(value: media.position, duration: media.duration, editing: { value, position in
                            if value { capturedIdentity = media.trackID }; editing = value; seek = position
                        }, preview: { seek = $0 }, commit: { value in
                            let identity = capturedIdentity ?? media.trackID; capturedIdentity = nil; command(.seek(value), identity: identity)
                        })
                        Text("−" + time(max(0, media.duration - (editing ? seek : media.position)))).monospacedDigit().frame(minWidth: 48)
                    }.font(.system(size: 12))
                }
                else if model.settings.islandActivity.showProgress, !media.title.isEmpty {
                    Text(model.ui("Mac.MediaTimelineUnavailable")).font(.caption).foregroundStyle(.secondary)
                }
                if !media.title.isEmpty { HStack(spacing: 28) {
                    Button { command(.previous) } label: { Image(systemName: "backward.end.fill").frame(width: 48, height: 44) }.disabled(!media.canSkip).accessibilityLabel(model.ui("MediaPrevious.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"))
                    Button { command(.playPause) } label: { Image(systemName: media.playing ? "pause.fill" : "play.fill").font(.system(size: 28)).frame(width: 56, height: 44) }.disabled(!media.canPlayPause).accessibilityLabel(model.ui(media.playing ? "MediaPauseLabel" : "MediaPlayLabel")).accessibilityIdentifier("media-playpause")
                    Button { command(.next) } label: { Image(systemName: "forward.end.fill").frame(width: 48, height: 44) }.disabled(!media.canSkip).accessibilityLabel(model.ui("MediaNext.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"))
                }.buttonStyle(.plain) }
                if let error = model.media.error { Text(error).font(.system(size: 11)).foregroundStyle(.red) }
            }
        }.padding(.horizontal, 28).padding(.top, 24).padding(.bottom, 16)
    }
    private func command(_ command: MediaCommand, identity: String? = nil) {
        let expected = identity ?? media.trackID
        Task { do { try await model.media.command(command, expectedTrackIdentity: expected) } catch { model.error = error.localizedDescription } }
    }
    private func time(_ value: Double) -> String { let seconds = value.isFinite ? max(0, Int(value)) : 0; return String(format: "%d:%02d", seconds / 60, seconds % 60) }
}
struct IslandCurrentLyrics: View {
    let model: WorkspaceModel
    var showsNextLine = true
    var contributesGlow = true
    var body: some View {
        if let lyrics = model.lyrics, model.settings.lyrics.enabled {
            TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !model.media.snapshot.playing)) { context in
                let snapshot = model.media.snapshot
                let document = lyrics.document.match?.trackIdentity == snapshot.trackID ? lyrics.document : .init(lines: [], provider: .localLrc)
                let position = snapshot.interpolatedPosition()
                let presentation = LyricsDisplay.presentation(document, position: position, delay: model.settings.lyrics.delayMilliseconds)
                VStack(alignment: .leading, spacing: 8) {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 2) {
                            if let index = presentation.lineIndex {
                                let line = document.lines[index]
                                Text(line.text).font(.system(size: model.settings.lyrics.fontSize, weight: .semibold))
                                if let secondary = LyricsDisplay.secondary(line, target: model.settings.lyricsTranslationTarget.rawValue,
                                    enabled: model.settings.lyrics.secondaryLyrics, showAI: model.settings.lyrics.showAiLyricsLabel) {
                                    Text(secondary).font(.system(size: model.settings.lyrics.fontSize * 0.875)).opacity(0.72)
                                        .background { if contributesGlow { GlowTranslationPresence(model: model, line: line, compact: false, active: true) } }
                                }
                            } else {
                                switch lyrics.status {
                                case .loading: Text(model.ui("LyricsLoading")).foregroundStyle(.secondary)
                                case .notFound: Text(model.ui("LyricsNotFound")).foregroundStyle(.secondary)
                                case .failed: Text(model.ui("LyricsFailed")).foregroundStyle(.secondary)
                                case .found, .disabled: EmptyView()
                                }
                            }
                            if lyrics.searching { ProgressView().controlSize(.mini) }
                            if lyrics.translationPaused {
                                Text(model.ui("AiLyricsTranslationPaused")).font(.system(size: 11)).foregroundStyle(.secondary)
                            } else if let failure = lyrics.translationFailure {
                                Text(failure).font(.system(size: 11)).foregroundStyle(.secondary)
                            }
                        }.frame(maxWidth: .infinity, alignment: .leading)
                    }.frame(maxHeight: showsNextLine ? .infinity : 112).id("\(snapshot.trackID)|\(presentation.lineIndex ?? -1)")
                    if showsNextLine, let index = LyricsDisplay.nextLine(document, position: position, delay: model.settings.lyrics.delayMilliseconds) {
                        Divider()
                        HStack(spacing: 8) { Image(systemName: "chevron.down").font(.system(size: 10)); Text(document.lines[index].text).font(.system(size: model.settings.lyrics.fontSize * 13 / 16)).lineLimit(1) }.foregroundStyle(.secondary)
                    }
                }
            }
        }
    }
}
