import Foundation
import DropSpaceCore

public struct LyricsLookupResult: Sendable {
    public var document: LyricsDocument
    public var completedProviders: Int
    public var failures: [String]
    public var cacheWarning: String?
}
struct LyricsProviderReply: Sendable {
    var document: LyricsDocument?
    var failure: String?
}

/// A presentation timeout retires the waiter, never the actual provider slot.
/// The coordinator retains this owner until the underlying invocation exits.
private actor LyricsProviderCall {
    private var worker: Task<Void, Never>?
    private var timer: Task<Void, Never>?
    private var resolved: LyricsProviderReply?
    private var waiter: CheckedContinuation<LyricsProviderReply, Never>?
    func start(source: any LyricsSource, identifier: (any LyricsLanguageIdentifying)?, query: LyricsQuery, gate: DownloadPermitPool, deadline: ContinuousClock.Instant,
               progress: @escaping @Sendable (LyricsDocument) async -> Void, retired: @escaping @Sendable () async -> Void) {
        worker = Task {
            let reply: LyricsProviderReply
            do {
                let permit = try await gate.acquire()
                do {
                    try Task.checkCancellation()
                    let result = try await source.query(query, progress: .init(identifier: identifier, report: progress))
                    try Task.checkCancellation()
                    reply = .init(document: LyricsSelection.validate(result, query: query))
                } catch { reply = .init(failure: error.localizedDescription) }
                await gate.release(permit)
            } catch { reply = .init(failure: error.localizedDescription) }
            finish(reply, cancelWorker: false)
            await retired()
        }
        timer = Task {
            do { try await Task.sleep(until: deadline, clock: .continuous); finish(.init(failure: "Lyrics provider deadline exceeded"), cancelWorker: true) }
            catch { }
        }
    }
    func value() async -> LyricsProviderReply {
        if let resolved { return resolved }
        return await withCheckedContinuation { waiter = $0 }
    }
    func cancel() { finish(.init(failure: "Lyrics lookup cancelled"), cancelWorker: true) }
    func drain() async { cancel(); await worker?.value }
    private func finish(_ reply: LyricsProviderReply, cancelWorker: Bool) {
        if cancelWorker { worker?.cancel() }
        guard resolved == nil else { return }
        resolved = reply; timer?.cancel(); timer = nil
        waiter?.resume(returning: reply); waiter = nil
    }
}

public actor LyricsLookup {
    private let sources: [LyricsProvider: any LyricsSource]
    private let identifier: (any LyricsLanguageIdentifying)?
    public nonisolated let cache: LyricsCache
    private var gates: [LyricsProvider: DownloadPermitPool] = [:]
    private var invocations: [UUID: LyricsProviderCall] = [:]
    public init(sources: [LyricsProvider: any LyricsSource], cache: LyricsCache, identifier: (any LyricsLanguageIdentifying)? = nil) { self.sources = sources; self.cache = cache; self.identifier = identifier }

    public func query(_ requested: LyricsQuery, settings: LyricsSettings, refresh: Bool = false,
                      preview: @escaping @Sendable (LyricsDocument) async -> Void = { _ in }) async throws -> LyricsLookupResult {
        try Task.checkCancellation()
        var query = requested; query.bypassProviderResponseCache = refresh
        let primary: LyricsProvider = settings.localOnly ? .localLrc : settings.provider
        let backup = primary != .localLrc && settings.backupProvider != primary && settings.backupProvider != .localLrc ? settings.backupProvider : nil
        let empty = LyricsDocument(lines: [], provider: primary)
        guard settings.enabled, !query.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return .init(document: empty, completedProviders: 0, failures: []) }
        guard query.duration.isFinite, query.duration < Double(Int64.max / 1000) else { throw DropSpaceError.invalid("Invalid recording duration") }
        var warning: String?
        // Cache configuration belongs to the settings owner. A delayed lookup
        // must not restore an earlier quota after the user changes it.
        let generation = await cache.generation, key = try Self.key(query, primary: primary, backup: backup, remaining: settings.searchRemainingProviders)
        if primary != .localLrc, !refresh {
            do {
                if let stored = try await cache.readDocument(identity: key) {
                    var value = LyricsSelection.validate(stored, query: query)
                    if let identifier, !query.preferredTranslationLanguage.isEmpty, !value.lines.isEmpty {
                        value = try await identifier.prepare(value, target: query.preferredTranslationLanguage)
                    }
                    let revision = stored.provider == .netEase || stored.provider == .qqMusic ? 3 : stored.provider == .kugou ? 1 : 0
                    if value.providerDataRevision >= revision, LyricsSelection.usable(value), !LyricsSelection.needsLookup(value, target: query.preferredTranslationLanguage) {
                        return .init(document: value, completedProviders: 0, failures: [], cacheWarning: warning)
                    }
                }
            } catch { warning = error.localizedDescription }
        }
        let deadline = ContinuousClock.now.advanced(by: .seconds(24))
        let local = LocalLyricsProvider(directory: URL(fileURLWithPath: settings.localLrcDirectory, isDirectory: true))
        if primary == .localLrc && settings.localLrcDirectory.isEmpty { return .init(document: empty, completedProviders: 1, failures: [], cacheWarning: warning) }
        var allowed: [LyricsProvider] = []
        if primary != .localLrc {
            if let backup { allowed.append(backup) }
            if settings.searchRemainingProviders { allowed += LyricsSelection.online.filter { $0 != primary && !allowed.contains($0) } }
        }
        let result = try await collect(query, primary: primary, backup: backup, allowed: allowed,
                                       primarySource: primary == .localLrc ? local : sources[primary], deadline: deadline, preview: preview)
        let document = result.document, completed = result.completedProviders, failures = result.failures
        let primaryFailed = !result.completed.contains(primary) || result.failedProviders.contains(primary)
        try Task.checkCancellation()
        // A transient failure in a preferred source must not freeze a lower winner.
        let selectionComplete = document.provider == primary || !primaryFailed && (document.provider == backup || backup == nil || failures.isEmpty)
        if primary != .localLrc, LyricsSelection.usable(document), !LyricsSelection.needsLookup(document, target: query.preferredTranslationLanguage), query.preferredTranslationLanguage.isEmpty || selectionComplete {
            do { try await cache.writeDocument(identity: key, document: document, generation: generation) } catch { warning = error.localizedDescription }
        }
        return .init(document: document, completedProviders: completed, failures: failures, cacheWarning: warning)
    }
    private func collect(_ query: LyricsQuery, primary: LyricsProvider, backup: LyricsProvider?, allowed: [LyricsProvider],
                         primarySource: (any LyricsSource)?, deadline: ContinuousClock.Instant,
                         preview: @escaping @Sendable (LyricsDocument) async -> Void) async throws -> LyricsSearchSnapshot {
        let updates = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        let cancelled = LyricsSearchCancellation()
        let search = LyricsSearchSession(query: query, primary: primary, backup: backup, cancelled: cancelled,
                                         signal: updates.continuation, preview: preview)
        var work: [Task<Void, Never>] = [], timers: [Task<Void, Never>] = []
        var admitted = Set<LyricsProvider>(), remaining = allowed
        var supplemental = false, windowTimer = false, qualityDeadline: ContinuousClock.Instant?
        var hedgeDeadline: ContinuousClock.Instant?
        func wake(at instant: ContinuousClock.Instant) {
            timers.append(Task { do { try await Task.sleep(until: instant, clock: .continuous); updates.continuation.yield(()) } catch {} })
        }
        func admit(_ kind: LyricsProvider, source: (any LyricsSource)?) {
            admitted.insert(kind)
            work.append(Task {
                do {
                    let reply = try await self.invoke(kind, source: source, query: query, deadline: deadline) { await search.report($0) }
                    await search.finished(kind, reply: reply)
                } catch { await search.finished(kind, reply: .init(failure: error.localizedDescription)) }
            })
        }
        admit(primary, source: primarySource); wake(at: deadline)
        defer { work.forEach { $0.cancel() }; timers.forEach { $0.cancel() }; updates.continuation.finish() }
        return try await withTaskCancellationHandler {
            var iterator = updates.stream.makeAsyncIterator()
            while true {
                try Task.checkCancellation()
                let state = await search.snapshot()
                if let end = state.windowDeadline, !windowTimer { windowTimer = true; wake(at: min(end, deadline)) }
                if ContinuousClock.now >= deadline || state.windowDeadline.map({ ContinuousClock.now >= $0 }) == true || qualityDeadline.map({ ContinuousClock.now >= $0 }) == true {
                    return await search.close()
                }
                if !supplemental, state.hasOriginal || state.completed.contains(primary) {
                    supplemental = true
                    if LyricsSelection.needsLookup(state.document, target: query.preferredTranslationLanguage) {
                        if let backup, remaining.contains(backup) {
                            remaining.removeAll { $0 == backup }; admit(backup, source: sources[backup])
                            hedgeDeadline = .now.advanced(by: .milliseconds(150)); wake(at: hedgeDeadline!)
                        } else { for kind in remaining { admit(kind, source: sources[kind]) }; remaining = [] }
                    } else { remaining = [] }
                }
                if supplemental, !remaining.isEmpty,
                   backup.map({ state.reported.contains($0) || state.completed.contains($0) }) == true || hedgeDeadline.map({ ContinuousClock.now >= $0 }) == true {
                    for kind in remaining { admit(kind, source: sources[kind]) }; remaining = []
                }
                if state.preferredTarget || admitted.isSubset(of: state.completed) && remaining.isEmpty { return await search.close() }
                if qualityDeadline == nil, !state.document.lines.isEmpty,
                   query.preferredTranslationLanguage.isEmpty || LyricsSelection.completeTarget(state.document, target: query.preferredTranslationLanguage) {
                    let rank = LyricsSelection.sourceRank(state.document.provider, primary: primary, backup: backup)
                    if !(Array(admitted.subtracting(state.completed)) + remaining).contains(where: { LyricsSelection.sourceRank($0, primary: primary, backup: backup) < rank }) {
                        qualityDeadline = .now.advanced(by: .milliseconds(300)); wake(at: qualityDeadline!)
                    }
                }
                guard await iterator.next() != nil else { try Task.checkCancellation(); return await search.close() }
            }
        } onCancel: { cancelled.cancel(); updates.continuation.yield(()) }
    }
    private func invoke(_ kind: LyricsProvider, source: (any LyricsSource)?, query: LyricsQuery, deadline: ContinuousClock.Instant, progress: @escaping @Sendable (LyricsDocument) async -> Void) async throws -> LyricsProviderReply {
        try Task.checkCancellation()
        guard let source else { return .init(failure: "Lyrics source registration is unavailable") }
        let call = LyricsProviderCall(), id = UUID()
        let gate = gates[kind] ?? DownloadPermitPool(limit: 2); gates[kind] = gate; invocations[id] = call
        await call.start(source: source, identifier: identifier, query: query, gate: gate, deadline: min(deadline, .now.advanced(by: .seconds(8))), progress: progress) { await self.retired(id) }
        let reply = await withTaskCancellationHandler { await call.value() } onCancel: { Task { await call.cancel() } }
        try Task.checkCancellation(); return reply
    }
    private func retired(_ id: UUID) { invocations.removeValue(forKey: id) }
    public func clearCache() async throws {
        try await cache.clear()
        for source in sources.values { await source.clearResponseCache() }
    }
    public func stop() async {
        let owned = Array(invocations.values)
        for call in owned { await call.cancel() }
        for call in owned { await call.drain() }
    }
    private static func key(_ query: LyricsQuery, primary: LyricsProvider, backup: LyricsProvider?, remaining: Bool) throws -> String {
        struct Identity: Encodable { var version = "mac-source-v2-progressive-quality1"; var matcher = LyricsMatcher.version; var title: String; var artist: String; var albumArtist: String; var album: String; var duration: Double; var trackIdentity: String; var target: String; var primary: LyricsProvider; var backup: LyricsProvider?; var remaining: Bool }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        return String(decoding: try encoder.encode(Identity(title: query.title, artist: query.artist, albumArtist: query.albumArtist, album: query.album, duration: query.duration, trackIdentity: query.trackIdentity, target: LyricsSelection.target(query.preferredTranslationLanguage), primary: primary, backup: backup, remaining: remaining)), as: UTF8.self)
    }
}
