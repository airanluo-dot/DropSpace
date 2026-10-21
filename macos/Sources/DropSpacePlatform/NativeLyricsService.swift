import Foundation
import Observation
import DropSpaceCore
import DropSpaceInfrastructure

@MainActor @Observable public final class NativeLyricsService {
    public enum Status: String { case disabled, loading, found, notFound, failed }
    public private(set) var document = LyricsDocument(lines: [], provider: .localLrc)
    public private(set) var status: Status = .disabled
    public private(set) var failures: [String] = []
    public private(set) var cacheWarning: String?
    public private(set) var cacheBytes: Int64 = 0
    public private(set) var searching = false
    public private(set) var translating = false
    public private(set) var translationFailure: String?
    public private(set) var translationBackend: String?
    public private(set) var translationFromCache = false
    public private(set) var usedCPUFallback = false
    public private(set) var translationPaused = false
    public private(set) var canRetryGPU = false
    public private(set) var executionStatus = PlainHyRuntimeStatus()
    public let qqSession: QQMusicSession
    private let lookup: LyricsLookup
    private let languageIdentifier: FastTextLanguageIdentifier
    private let translationWorker: PlainHyTranslationWorker
    private let translator: PlainHyLyricsTranslator
    private var translationWork: Task<Void, Never>?
    private var translationRevision = 0
    private var translationSource: LyricsDocument?
    private var playback = MediaSnapshot()
    private var lastQuery: LyricsQuery?
    private var lastSettings: LyricsSettings?
    private var work: Task<Void, Never>?
    private var cacheWork: Task<Void, Never>?
    private var cacheRevision = 0
    private var generation = 0
    private var suspended = false
    private var resetCircuitPending = false
    private var resetBackendPending = false
    public init(budget: DownloadBudget, dataDirectory: URL, models: AIModelStore) {
        let namespace = ProcessInfo.processInfo.environment["DROPSPACE_DATA_DIRECTORY"].map {
            ".validation." + Digests.sha256(Data($0.utf8)).prefix(16)
        } ?? ""
        qqSession = QQMusicSession(storage: QQMusicKeychainStorage(keychain:
            KeychainStore(service: "io.github.airanluo-dot.DropSpace.QQMusic" + namespace)))
        let http = LyricsHTTP(budget: budget)
        languageIdentifier = FastTextLanguageIdentifier(executable: Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/language-worker"),
            model: Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/LanguageModels/lid.176.bin"))
        let cache = LyricsCache(directory: dataDirectory.appendingPathComponent("LyricsCache", isDirectory: true))
        lookup = LyricsLookup(sources: [.netEase: NetEaseLyricsProvider(http: http), .qqMusic: QQMusicLyricsProvider(http: http, session: qqSession), .kugou: KugouLyricsProvider(http: http),
                  .lrclib: LRCLibLyricsProvider(http: http), .amll: AMLLLyricsProvider(http: http)], cache: cache, identifier: languageIdentifier)
        translationWorker = PlainHyTranslationWorker(executable: Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/plain-lyrics-worker"), models: models)
        translator = PlainHyLyricsTranslator(cache: cache, worker: translationWorker)
    }
    public func configure(snapshot: MediaSnapshot, settings: LyricsSettings, target: LyricsTarget, refresh: Bool = false, resetExecution: Bool = false) {
        guard !suspended else { return }
        if resetExecution || lastSettings.map({
            $0.enabled != settings.enabled || $0.aiTranslationEnabled != settings.aiTranslationEnabled ||
            $0.aiModelID != settings.aiModelID || $0.gpuAccelerationEnabled != settings.gpuAccelerationEnabled ||
            $0.gpuBackend != settings.gpuBackend
        }) == true {
            resetCircuitPending = true; resetBackendPending = true
            executionStatus = .init()
        }
        playback = snapshot
        var query = LyricsQuery(title: snapshot.title, artist: snapshot.artist, album: snapshot.album, duration: snapshot.duration, trackIdentity: snapshot.trackID, albumArtist: snapshot.albumArtist)
        query.preferredTranslationLanguage = target.rawValue
        let reload = refresh || resetCircuitPending || query != lastQuery || LyricsReloadPolicy.requiresReload(lastSettings, settings)
        if settings.cacheMaximumBytes != lastSettings?.cacheMaximumBytes {
            cacheRevision += 1; let revision = cacheRevision, cache = lookup.cache
            cacheWork?.cancel()
            cacheWork = Task { [weak self] in
                do {
                    try await cache.configure(maximumBytes: settings.cacheMaximumBytes)
                    let bytes = try await cache.usage()
                    guard let self, cacheRevision == revision, !Task.isCancelled else { return }
                    cacheBytes = bytes; cacheWarning = nil
                } catch is CancellationError { }
                catch { guard let self, cacheRevision == revision, !Task.isCancelled else { return }; cacheWarning = error.localizedDescription }
            }
        }
        lastQuery = query; lastSettings = settings
        guard reload else { return }
        generation += 1; let run = generation
        let old = work, oldTranslation = translationWork
        old?.cancel(); oldTranslation?.cancel(); translationWork = nil; translationRevision += 1; translationSource = nil
        translating = false; translationFailure = nil; translationBackend = nil; translationFromCache = false; usedCPUFallback = false
        executionStatus.current = nil
        work = nil; failures = []; document = LyricsDocument(lines: [], provider: .localLrc)
        status = settings.enabled && !query.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? .loading : .disabled
        searching = status == .loading
        let lookup = lookup, request = query, quotaWork = cacheWork
        work = Task { [weak self] in
            defer { if let self, generation == run { searching = false } }
            await old?.value; await oldTranslation?.value
            guard let self, generation == run, !Task.isCancelled else { return }
            let resetBackend = resetBackendPending
            await translationWorker.stop(clearFailures: resetBackend)
            guard generation == run, !Task.isCancelled else { return }
            if resetCircuitPending {
                await translator.resumeInference()
                guard generation == run, !Task.isCancelled else { return }
                resetCircuitPending = false; resetBackendPending = false
                translationPaused = false
                if resetBackend { canRetryGPU = false }
            }
            await quotaWork?.value
            guard generation == run, !Task.isCancelled else { return }
            cacheBytes = (try? await lookup.cache.usage()) ?? 0
            guard status != .disabled else { return }
            do {
                let result = try await lookup.query(request, settings: settings, refresh: refresh) { [weak self] document in
                    await self?.receivePreview(document, generation: run)
                }
                try Task.checkCancellation(); guard generation == run else { return }
                receiveFinal(result.document, generation: run); failures = result.failures; cacheWarning = result.cacheWarning
                status = LyricsSelection.usable(document) ? .found : result.completedProviders > 0 ? .notFound : .failed
                cacheBytes = (try? await lookup.cache.usage()) ?? 0
            } catch is CancellationError { return }
            catch { guard generation == run, !Task.isCancelled else { return }; failures.append(error.localizedDescription); status = .failed }
        }
    }
    private func receiveFinal(_ incoming: LyricsDocument, generation run: Int) {
        guard generation == run, let settings = lastSettings, let query = lastQuery else { return }
        let primary: LyricsProvider = settings.localOnly ? .localLrc : settings.provider
        let backup = primary != .localLrc && settings.backupProvider != primary && settings.backupProvider != .localLrc ? settings.backupProvider : nil
        // The completed snapshot may reach MainActor after a late native veto.
        // Rank against the original provider source, never generated AI rows.
        let value = translationSource.map { LyricsSelection.choose($0, incoming, target: query.preferredTranslationLanguage, primary: primary, backup: backup) } ?? incoming
        receivePreview(value, generation: run)
    }
    private func receivePreview(_ incoming: LyricsDocument, generation run: Int) {
        guard generation == run, !Task.isCancelled else { return }
        guard let settings = lastSettings, let query = lastQuery else { return }
        let target = query.preferredTranslationLanguage
        // A later provider translation revokes the earlier AI request. Identical
        // source revisions can retain progressively translated occurrences.
        if let source = translationSource,
           LyricsLanguagePolicy.sourceIdentity(source) == LyricsLanguagePolicy.sourceIdentity(incoming),
           LyricsLanguagePolicy.originalRevision(source) == LyricsLanguagePolicy.originalRevision(incoming),
           LyricsLanguagePolicy.translationRevision(source) == LyricsLanguagePolicy.translationRevision(incoming),
           source.translationAdmission?.decision == incoming.translationAdmission?.decision {
            document = PlainHyLyrics.reconcile(current: incoming, source: source, generated: document, target: target)
            status = .found; return
        }
        let previous = translationWork; previous?.cancel(); translationWork = nil
        translationRevision += 1; let revision = translationRevision
        translationSource = incoming; document = PlainHyLyrics.markStates(incoming, target: target); status = .found
        translating = settings.aiTranslationEnabled && !PlainHyLyrics.eligible(incoming, target: target).isEmpty
        translationFailure = nil; translationBackend = nil; translationFromCache = false; usedCPUFallback = false
        let translator = translator, worker = translationWorker
        translationWork = Task { [weak self] in
            await previous?.value
            guard let self, generation == run, translationRevision == revision, !Task.isCancelled else { return }
            // Drain old ownership even when the replacement has no eligible AI.
            await worker.stop()
            guard generation == run, translationRevision == revision, !Task.isCancelled, translating else { return }
            defer { if generation == run, translationRevision == revision { translating = false } }
            do {
                await worker.observeExecution { [weak self] value in
                    await self?.receiveExecution(value, run: run, revision: revision)
                }
                try Task.checkCancellation()
                guard translationIsCurrent(run, revision) else { return }
                let result = try await translator.translate(query: query, document: incoming, target: target, settings: settings,
                    position: { [weak self] in await self?.currentPosition() ?? 0 },
                    isCurrent: { [weak self] in await self?.translationIsCurrent(run, revision) ?? false },
                    progress: { [weak self] value in await self?.receiveTranslation(value, source: incoming, target: target, run: run, revision: revision) })
                try Task.checkCancellation(); guard translationIsCurrent(run, revision) else { return }
                let paused = await translator.inferencePaused
                let workerFailure = await worker.lastFailure
                let execution = result.fromCache ? nil : await worker.lastExecution
                let bytes = (try? await lookup.cache.usage()) ?? 0
                guard translationIsCurrent(run, revision), !Task.isCancelled else { return }
                receiveTranslation(result.document, source: incoming, target: target, run: run, revision: revision)
                translationFailure = result.reason; translationFromCache = result.fromCache
                translationPaused = paused; canRetryGPU = workerFailure != nil
                if let warning = result.cacheWarning { cacheWarning = warning }
                if let execution {
                    translationBackend = execution.backend; usedCPUFallback = execution.usedCPUFallback
                }
                cacheBytes = bytes
            } catch is CancellationError { }
            catch { if translationIsCurrent(run, revision) { translationFailure = error.localizedDescription } }
        }
    }
    private func currentPosition() -> Double { playback.interpolatedPosition() }
    private func translationIsCurrent(_ run: Int, _ revision: Int) -> Bool { generation == run && translationRevision == revision }
    private func receiveExecution(_ value: PlainHyRuntimeStatus, run: Int, revision: Int) {
        guard translationIsCurrent(run, revision), !Task.isCancelled else { return }
        executionStatus = value; canRetryGPU = value.failure != nil
    }
    private func receiveTranslation(_ value: LyricsDocument, source: LyricsDocument, target: String, run: Int, revision: Int) {
        guard translationIsCurrent(run, revision), !Task.isCancelled else { return }
        document = PlainHyLyrics.reconcile(current: document, source: source, generated: value, target: target)
    }
    /// Reuses the owned lookup/translation retirement chain. No view-owned task
    /// can outlive quit, reconnect or a later settings/track generation.
    public func resumeTranslation(retryGPU: Bool = false) {
        guard !suspended, let settings = lastSettings, let query = lastQuery,
              let target = LyricsTarget(rawValue: query.preferredTranslationLanguage) else { return }
        resetCircuitPending = true
        resetBackendPending = resetBackendPending || retryGPU
        configure(snapshot: playback, settings: settings, target: target, refresh: true)
    }
    public func clearCache() async {
        guard !suspended else { return }
        generation += 1; let run = generation; let previous = work, previousTranslation = translationWork
        work = nil; previous?.cancel(); previousTranslation?.cancel(); translationWork = nil; translationRevision += 1; translationSource = nil; translating = false
        executionStatus.current = nil; translationFromCache = false; translationBackend = nil
        await previous?.value; await previousTranslation?.value; await cacheWork?.value
        await lookup.stop(); await languageIdentifier.stop(clearPredictions: true); await translationWorker.stop(); await translator.clearMemo()
        guard generation == run else { return }
        document = .init(lines: [], provider: .localLrc); status = .loading; searching = true
        do {
            try await lookup.clearCache()
            guard generation == run else { return }
            cacheBytes = try await lookup.cache.usage(); cacheWarning = nil
            if let query = lastQuery, let settings = lastSettings {
                let lookup = lookup
                work = Task { [weak self] in
                    defer { if let self, generation == run { searching = false } }
                    do {
                        let value = try await lookup.query(query, settings: settings, refresh: true) { [weak self] value in await self?.receivePreview(value, generation: run) }
                        try Task.checkCancellation(); guard let self, generation == run else { return }
                        receiveFinal(value.document, generation: run); failures = value.failures; cacheWarning = value.cacheWarning
                        status = LyricsSelection.usable(document) ? .found : value.completedProviders > 0 ? .notFound : .failed
                        cacheBytes = (try? await lookup.cache.usage()) ?? 0
                    } catch is CancellationError { }
                    catch { guard let self, generation == run else { return }; cacheWarning = error.localizedDescription }
                }
            }
        } catch { if generation == run { cacheWarning = error.localizedDescription; searching = false } }
    }
    public func suspendForReconnect() async { suspended = true; await stop() }
    public func resumeAfterReconnect(snapshot: MediaSnapshot, settings: LyricsSettings, target: LyricsTarget) {
        suspended = false; configure(snapshot: snapshot, settings: settings, target: target)
    }
    public func stop() async {
        generation += 1; let run = generation
        let old = work, oldTranslation = translationWork; work = nil; old?.cancel(); oldTranslation?.cancel(); cacheWork?.cancel()
        translationWork = nil; translationRevision += 1; translationSource = nil; translating = false
        executionStatus.current = nil
        await old?.value; await oldTranslation?.value; await cacheWork?.value; cacheWork = nil
        await lookup.stop(); await languageIdentifier.stop(); await translationWorker.stop()
        await translationWorker.observeExecution(nil)
        guard generation == run else { return }
        lastQuery = nil; lastSettings = nil
        status = .disabled; searching = false; document = .init(lines: [], provider: .localLrc)
    }
}
