import SwiftUI
import AppKit
import DropSpaceCore
import DropSpacePlatform
import DropSpaceInfrastructure

/// The same app-owned settings transactions back the inline Music controls.
struct MusicPreferences: View {
    let model: WorkspaceModel
    @State private var confirmsOnlineLyrics = false
    @State private var confirmsCacheClear = false
    private func binding<T>(_ key: WritableKeyPath<AppSettings,T>) -> Binding<T> {
        Binding(get: { model.settings[keyPath:key] }, set: { value in model.update { $0[keyPath:key] = value } })
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            SettingsCard { aiSettings }
            playbackSettings
            lyricSettings
            MusicApplicationsView(model: model)
        }
            .confirmationDialog(model.ui("LyricsEnabled"), isPresented: $confirmsOnlineLyrics) {
                Button(model.ui("LyricsEnabled")) { setAIEnabled(true) }
                Button(model.ui("CommonCancel"), role: .cancel) {}
            } message: { Text(model.ui("LyricsOnlinePrivacyHelp")) }
            .confirmationDialog(model.ui("AiLyricsClearCache"), isPresented: $confirmsCacheClear) {
                Button(model.ui("AiLyricsClearCache"), role: .destructive) { Task { await model.lyrics?.clearCache() } }
                Button(model.ui("CommonCancel"), role: .cancel) {}
            } message: { Text(model.ui("AiLyricsClearCacheConfirm")) }
            .onChange(of: model.isQuitting) { _, quitting in if quitting { confirmsOnlineLyrics = false; confirmsCacheClear = false } }
    }
    private func setAIEnabled(_ enabled: Bool) {
        if enabled, !installed(model.settings.lyrics.aiModelID) { model.openComponentSettings(); return }
        model.update {
            $0.lyrics.aiTranslationEnabled = enabled
            $0.lyrics.enabled = enabled || $0.lyrics.enabled
            $0.lyrics.secondaryLyrics = enabled || $0.lyrics.secondaryLyrics
            $0.lyrics.glowMode = LyricsGlowPolicy.aiEnabledChanged(enabled)
        }
    }
    private func providerName(_ provider: LyricsProvider) -> String {
        let key: String = switch provider { case .netEase: "NetEase"; case .qqMusic: "QqMusic"; case .kugou: "Kugou"; case .lrclib: "Lrclib"; case .amll: "Amll"; case .localLrc: "LocalLrc" }
        return model.ui("LyricsProvider" + key)
    }
    private func installed(_ id: String) -> Bool {
        model.aiModels?.inventory[id]?.installed == true && model.aiModels?.inventory[id]?.enabled == true &&
            model.aiModels?.running.contains(id) == false
    }
    private var aiSettings: some View {
    SettingsGroup(model.ui("AiLyricsTitle")) {
        Picker(model.ui("LyricsTranslationTargetTitle"),selection: binding(\.lyricsTranslationTarget)) { Text("English").tag(LyricsTarget.en); Text("简体中文").tag(LyricsTarget.zh) }
        Toggle(model.ui("AiLyricsEnabled"),isOn: Binding(get: { model.settings.lyrics.aiTranslationEnabled }, set: { enabled in
            if enabled, !model.settings.lyrics.enabled, !model.settings.lyrics.localOnly { confirmsOnlineLyrics = true }
            else { setAIEnabled(enabled) }
        })).accessibilityIdentifier("music-ai-enabled")
        Picker(model.ui("AiLyricsModel"),selection: Binding(get: { model.settings.lyrics.aiModelID }, set: { id in
            if model.settings.lyrics.aiTranslationEnabled, !installed(id) { model.openComponentSettings(); return }
            model.update { $0.lyrics.aiModelID = id }
        })) {
            Text("Hy-MT2 1.8B Q8_0").tag("hy-mt2-18-q8-plain-beta")
            Text("Hy-MT2 7B Q8_0").tag("hy-mt2-7b-q8-plain-beta")
        }
        Toggle(model.ui("AiLyricsGpuAcceleration"),isOn: binding(\.lyrics.gpuAccelerationEnabled))
        Picker(model.ui("AiLyricsGpuBackend"),selection: binding(\.lyrics.gpuBackend)) { Text(model.ui("AiLyricsGpuAutomatic")).tag("automatic"); Text("Metal").tag("metal"); Text("CPU").tag("cpu") }
        aiStatus
        Button(model.ui("DlcManage"), action: model.openComponentSettings).accessibilityIdentifier("music-manage-components")
        if let service = model.lyrics {
            if service.translationPaused {
                Text(model.ui("AiLyricsTranslationPaused")).foregroundStyle(.secondary)
                Button(model.ui("AiLyricsResumeTranslation")) { service.resumeTranslation() }
                    .accessibilityIdentifier("music-resume-translation").disabled(model.musicRestart.isRetiring)
            }
            if model.settings.lyrics.aiTranslationEnabled && model.settings.lyrics.gpuAccelerationEnabled && service.canRetryGPU {
                Button(model.ui("AiLyricsRetryGpu")) { service.resumeTranslation(retryGPU: true) }
                    .accessibilityIdentifier("music-retry-gpu").disabled(model.musicRestart.isRetiring)
            }
        }
        Button(model.ui("AiLyricsClearCache")) { confirmsCacheClear = true }.disabled(model.musicRestart.isRetiring)
            Picker(model.ui("LyricsGlowTitle"),selection: binding(\.lyrics.glowMode)) { Text(model.ui("LyricsGlowOff")).tag("off"); Text(model.ui("LyricsGlowAi")).tag("aiLyrics"); Text(model.ui("LyricsGlowMusic")).tag("music") }
        Toggle(model.ui("LyricsSimplifiedGlow"),isOn: binding(\.lyrics.simplifiedGlow))
    }
    }
    private var playbackSettings: some View {
    SettingsGroup(model.ui("MusicPlaybackSection")) {
        Toggle(model.ui("MusicEnabled"),isOn: binding(\.islandActivity.enableMediaActivity))
        Toggle(model.ui("MusicArtwork"),isOn: binding(\.islandActivity.showArtwork))
        Toggle(model.ui("MusicSpectrum"),isOn: binding(\.islandActivity.showSpectrum))
        Toggle(model.ui("MusicDynamicWidth"),isOn: binding(\.islandActivity.compactDynamicWidth))
        Toggle(model.ui("MusicProgress"),isOn: binding(\.islandActivity.showProgress))
    }
    }
    private var lyricSettings: some View {
    SettingsGroup(model.ui("MusicLyricsSection")) {
        Toggle(model.ui("LyricsEnabled"),isOn: binding(\.lyrics.enabled))
        Toggle(model.ui("LyricsCompact"),isOn: binding(\.islandActivity.showLyricsInCompact))
        Picker(model.ui("LyricsMode"), selection: binding(\.lyrics.localOnly)) { Text(model.ui("LyricsOnline")).tag(false); Text(model.ui("LyricsLocal")).tag(true) }
        Picker(model.ui("LyricsProvider"),selection: binding(\.lyrics.provider)) { ForEach(LyricsProvider.allCases.filter { $0 != .localLrc },id: \.self) { Text(providerName($0)).tag($0) } }
        Picker(model.ui("LyricsBackupProvider"),selection: Binding(get: { model.settings.lyrics.backupProvider?.rawValue ?? "" },set: { value in model.update { $0.lyrics.backupProvider = LyricsProvider(rawValue:value) } })) { Text(model.ui("LyricsBackupProviderNone")).tag(""); ForEach(LyricsProvider.allCases.filter { $0 != .localLrc },id: \.self) { Text(providerName($0)).tag($0.rawValue) } }
        Toggle(model.ui("LyricsSearchRemainingProviders"),isOn: binding(\.lyrics.searchRemainingProviders))
        Toggle(model.ui("LyricsShowAiLabel"),isOn: binding(\.lyrics.showAiLyricsLabel))
        Toggle(model.ui("LyricsSecondary"),isOn: binding(\.lyrics.secondaryLyrics))
        SettingsValueSlider(title: model.ui("LyricsFontSize"), model: model, key: \.lyrics.fontSize, range: 12...28, step: 0.1,
                            format: { number($0, fractional: 1) })
        LabeledContent(model.ui("LyricsFontSizeNumber")) {
            TextField(model.ui("LyricsFontSizeNumber"), value: binding(\.lyrics.fontSize), format: .number)
                .frame(width: 72).accessibilityIdentifier("lyrics-font-size")
        }.help(model.ui("LyricsFontSizeHelp"))
        Toggle(model.ui("LyricsWords"),isOn: binding(\.lyrics.wordSyncedHighlighting))
        SettingsValueSlider(title: model.ui("LyricsDelay"), model: model,
            read: { Double($0.lyrics.delayMilliseconds) }, write: { $0.lyrics.delayMilliseconds = Int($1) },
            range: -30_000...30_000, step: 100,
            format: { ($0 > 0 ? "+" : "") + number($0) + " ms" })
        Toggle(model.ui("LyricsScrolling"),isOn: binding(\.lyrics.scrolling))
        SettingsValueSlider(title: model.ui("LyricsWidth"), model: model,
            read: { $0.lyrics.unlimitedScrollingWidth ? 610 : Double($0.lyrics.scrollingMaxWidth) },
            write: {
                $0.lyrics.unlimitedScrollingWidth = $1 == 610
                if $1 != 610 { $0.lyrics.scrollingMaxWidth = Int($1) }
            }, range: 80...610, step: 10,
            format: { $0 == 610 ? model.ui("LyricsWidthUnlimited") : number($0) })
        Picker(model.ui("Mac.LyricsTranslationScrollMode"), selection: binding(\.lyrics.translationScrollMode)) {
            Text(model.ui("Mac.LyricsScrollAdaptive")).tag(LyricsTranslationScrollMode.adaptive)
            Text(model.ui("Mac.LyricsScrollBaseline")).tag(LyricsTranslationScrollMode.baseline)
        }.accessibilityIdentifier("lyrics-translation-scroll-mode")
        Text(model.ui("Mac.LyricsAdaptiveScrollHelp")).font(.caption).foregroundStyle(.secondary)
        SettingsValueSlider(title: model.ui("LyricsCacheSize"), model: model,
            read: { Double($0.lyrics.cacheMaximumBytes) / 1_000_000_000 },
            write: { $0.lyrics.cacheMaximumBytes = Int64($1) * 1_000_000_000 },
            range: 0...10, step: 1,
            format: { $0 == 0 ? model.ui("LyricsCacheDisabled") : number($0) + " GB" })
        SettingTextField(title: model.ui("LyricsFolder"), model: model, key: \.lyrics.localLrcDirectory)
        Button(model.ui("ChooseFolder")) {
            let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = false
            panel.begin { result in if result == .OK, let url = panel.url { model.update { $0.lyrics.localLrcDirectory = url.path } } }
        }
        if let lyrics = model.lyrics {
            LabeledContent(model.ui("Mac.LyricsCacheUsed"), value: byteCount(lyrics.cacheBytes))
            Button(model.ui("ClearLyricsCache")) { confirmsCacheClear = true }.disabled(model.musicRestart.isRetiring)
            if let warning = lyrics.cacheWarning { Text(warning).foregroundStyle(.red) }
        }
    }
    }
    private func number(_ value: Double, fractional: Int = 0) -> String {
        value.formatted(.number.precision(.fractionLength(0...fractional)).locale(Locale(identifier: model.ui.language)))
    }
    @ViewBuilder private var aiStatus: some View {
        if let models = model.aiModels {
            let id = model.settings.lyrics.aiModelID
            if let component = models.catalog.first(where: { $0.id == id }) {
                Text(model.ui("AiLyricsModelDetails",
                    model.ui(id == "hy-mt2-7b-q8-plain-beta" ? "AiLyricsLargePlainBetaModel" : "AiLyricsPlainBetaModel"),
                    byteCount(component.fullsize),
                    "github.com/airanluo-dot/DropSpace")).font(.caption).foregroundStyle(.secondary)
            }
            Text(model.ui(models.running.contains(id) ? "AiLyricsDownloading" : installed(id) ? "AiLyricsReady" : models.inventory[id]?.installed == true ? "Mac.ModelDisabled" : "AiLyricsNeedsDownload"))
                .accessibilityIdentifier("music-model-status")
            if let failure = models.failures[id] { Text(failure).foregroundStyle(.red).textSelection(.enabled) }
        }
        if let service = model.lyrics {
            if service.translating { Text(model.ui("AiLyricsTranslating")) }
            let selected = model.settings.lyrics.gpuAccelerationEnabled ? model.settings.lyrics.gpuBackend : "cpu"
            Text(model.ui("AiLyricsSelectedBackend", selected == "automatic" ? model.ui("AiLyricsGpuAutomatic") : selected.uppercased()))
            if let current = service.executionStatus.current {
                Text(current.phase == .starting ? model.ui("AiLyricsBackendStarting") : model.ui("AiLyricsBackendExecuting", current.backend.uppercased()))
            } else {
                Text(model.ui(service.translationFromCache ? "AiLyricsBackendCached" : "AiLyricsBackendPending"))
            }
            if let last = service.executionStatus.last {
                Text(model.ui("AiLyricsLastBackend", last.backend.uppercased(), timestamp(last.at)))
            }
            if let failure = service.executionStatus.failure {
                Text(model.ui("AiLyricsRuntimeFailure", failure.requestedBackend.uppercased(), failure.attemptedBackend.uppercased(),
                    timestamp(failure.at), model.ui(failureKey(failure.reason)),
                    failure.exit.map { $0.wasSignal ? "SIG\($0.status)" : String($0.status) } ?? model.ui("AiLyricsExitNotObserved")))
                    .font(.caption).textSelection(.enabled)
                Text(model.ui(failure.exit?.terminatedByHost == true ? "AiLyricsWorkerStopped" : "AiLyricsWorkerExited") + " " +
                     model.ui(failure.cpuFallbackAttempted ? "AiLyricsCpuFallbackAttempted" : "AiLyricsInferenceFailed"))
                    .font(.caption).foregroundStyle(.secondary)
            }
            if let failure = service.translationFailure, !service.translationPaused, service.executionStatus.failure == nil {
                Text(failure).foregroundStyle(.red).textSelection(.enabled)
            }
        }
    }
    private func byteCount(_ bytes: Int64) -> String {
        ByteCountFormatStyle(style: .file, spellsOutZero: false, locale: Locale(identifier: model.ui.language)).format(bytes)
    }
    private func timestamp(_ date: Date) -> String {
        date.formatted(Date.FormatStyle(date: .abbreviated, time: .standard).locale(Locale(identifier: model.ui.language)))
    }
    private func failureKey(_ failure: PlainHyWorkerFailure?) -> String {
        switch failure {
        case .queueDeadline, .requestDeadline: "AiLyricsFailureTimeout"
        case .unavailableMemory, .memoryPressure, .residentLimit: "AiLyricsFailureMemory"
        case .runtimeLoad: "AiLyricsFailureRuntime"
        case .modelLoad, .modelNotReady: "AiLyricsFailureModel"
        case .invalidRuntime, .invalidFrame: "AiLyricsFailureValidation"
        case .decodeFailure: "AiLyricsFailureCompute"
        case .pipeFailure, nil: "AiLyricsFailureUnknown"
        }
    }
}
