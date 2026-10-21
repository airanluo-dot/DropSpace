import SwiftUI
import DropSpaceCore
import DropSpacePlatform

struct LyricsView: View {
    let model: WorkspaceModel
    var compact = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    var body: some View {
        if let service = model.lyrics, model.settings.lyrics.enabled {
            TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !model.media.snapshot.playing)) { context in
                let snapshot = model.media.snapshot
                let position = snapshot.interpolatedPosition()
                let document = service.document.match?.trackIdentity == snapshot.trackID ? service.document : .init(lines: [], provider: .localLrc)
                let frame = LyricsTimeline.frame(document, position: position, delayMilliseconds: model.settings.lyrics.delayMilliseconds)
                if compact {
                    if let index = frame.lineIndex {
                        lyric(document.lines[index], active: true).lineLimit(1)
                    } else { Text(snapshot.title).lineLimit(1) }
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        HStack {
                            Text(model.ui("MusicLyricsSection")).font(.headline)
                            if service.searching && service.status == .found { ProgressView().controlSize(.small).accessibilityLabel(model.ui("Mac.LyricsCheckingSources")) }
                            Spacer()
                            Button(model.ui("Mac.LyricsRefresh")) { service.configure(snapshot: snapshot, settings: model.settings.lyrics, target: model.settings.lyricsTranslationTarget, refresh: true) }
                                .disabled(model.musicRestart.isRetiring)
                        }
                        switch service.status {
                        case .loading: ProgressView(model.ui("LyricsLoading")).controlSize(.small)
                        case .disabled: EmptyView()
                        case .notFound: Text(model.ui("LyricsNotFound")).foregroundStyle(.secondary)
                        case .failed: Text(model.ui("LyricsFailed")).foregroundStyle(.secondary)
                        case .found:
                            if service.document.bodyQuality == .confirmedInstrumental { Text(model.ui("Mac.LyricsInstrumental")).foregroundStyle(.secondary) }
                            else {
                                ScrollViewReader { proxy in
                                    ScrollView {
                                        LazyVStack(alignment: .leading, spacing: 8) {
                                            // MusicPage limits presentation to 2,000 rows.
                                            // Source and translation storage remain intact.
                                            ForEach(document.lines.indices.prefix(2000), id: \.self) { index in
                                                let line = document.lines[index]
                                                lyric(line, active: index == frame.lineIndex)
                                                    .opacity(index == frame.lineIndex ? 1 : 0.58).id(index)
                                                    .accessibilityElement(children: .ignore)
                                                    .accessibilityAddTraits(.isStaticText)
                                                    .accessibilityLabel(line.text + (LyricsDisplay.secondary(line,
                                                        target: model.settings.lyricsTranslationTarget.rawValue,
                                                        enabled: model.settings.lyrics.secondaryLyrics, showAI: model.settings.lyrics.showAiLyricsLabel).map { "\n" + $0 } ?? ""))
                                            }
                                        }.padding(.horizontal, 4).padding(.top, 2).padding(.bottom, 8)
                                    }.frame(minHeight: 120, idealHeight: 200, maxHeight: 360)
                                        .accessibilityIdentifier("music-lyrics-scroll")
                                        .onChange(of: "\(snapshot.trackID)|\(frame.lineIndex ?? -1)", initial: true) { _, _ in
                                            // The baseline main page centers the active line
                                            // independently of the compact scrolling switch.
                                            guard let index = frame.lineIndex, index < 2000 else { return }
                                            withAnimation(reduceMotion || model.settings.overlayMotion == .reduced ? nil : .easeInOut(duration: 0.25)) { proxy.scrollTo(index, anchor: .center) }
                                        }
                                }
                                Text("\(service.document.provider.rawValue) · \(service.document.match?.title ?? "")").font(.caption).foregroundStyle(.secondary)
                                if model.settings.lyrics.translationScrollMode == .adaptive,
                                   let index = LyricsDisplay.presentation(service.document, position: position,
                                       delay: model.settings.lyrics.delayMilliseconds).lineIndex,
                                   service.document.lines[index].timeBasis != .timed {
                                    Text(model.ui("Mac.LyricsScrollTimingUnavailable")).font(.caption).foregroundStyle(.secondary)
                                }
                            }
                        }
                        if !service.failures.isEmpty {
                            DisclosureGroup(model.ui("Mac.LyricsSourceDetails")) { ForEach(Array(service.failures.enumerated()), id: \.offset) { Text($0.element).font(.caption).textSelection(.enabled) } }
                        }
                        if service.translating { ProgressView(model.ui("AiLyricsTranslating")).controlSize(.small) }
                        if let backend = service.translationBackend {
                            Text(model.ui("AiLyricsActualBackend", backend.uppercased())).font(.caption).foregroundStyle(.secondary)
                        }
                        if service.translationPaused {
                            Text(model.ui("AiLyricsTranslationPaused")).font(.caption).foregroundStyle(.secondary)
                        } else if let failure = service.translationFailure {
                            Text(failure).font(.caption).foregroundStyle(.red).textSelection(.enabled)
                        }
                    }
                }
            }
        }
    }
    private func lyric(_ line: LyricLine, active: Bool) -> some View {
        VStack(alignment: .leading, spacing: compact ? 0 : 2) {
            // MusicPage.SetLyricActive emphasizes the whole original line.
            // Source word-mask timing belongs to the compact Island renderer.
            Text(compact ? LyricsDisplay.compact(line.text) : line.text)
                .fontWeight(active ? .semibold : .regular)
            if let secondary = LyricsDisplay.secondary(line, target: model.settings.lyricsTranslationTarget.rawValue,
                enabled: model.settings.lyrics.secondaryLyrics, showAI: model.settings.lyrics.showAiLyricsLabel) {
                Text(compact ? LyricsDisplay.compact(secondary) : secondary).font(.system(size: model.settings.lyrics.fontSize * 0.875)).opacity(0.72)
                    .background {
                        if compact { GlowTranslationPresence(model: model, line: line, compact: true, active: active) }
                    }
            }
        }.font(.system(size: model.settings.lyrics.fontSize))
            .frame(maxWidth: compact && !model.settings.lyrics.unlimitedScrollingWidth ? CGFloat(model.settings.lyrics.scrollingMaxWidth) : .infinity, alignment: .leading)
    }
}
