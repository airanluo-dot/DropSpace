import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private actor LookupProbe: LyricsSource {
    nonisolated let kind: LyricsProvider
    let delay: Duration
    let body: Bool
    let ignoreCancellation: Bool
    var calls = 0, active = 0, peak = 0
    init(_ kind: LyricsProvider, delay: Duration = .zero, body: Bool = true, ignoreCancellation: Bool = false) {
        self.kind = kind; self.delay = delay; self.body = body; self.ignoreCancellation = ignoreCancellation
    }
    func query(_ query: LyricsQuery) async throws -> LyricsDocument {
        calls += 1; active += 1; peak = max(peak, active); defer { active -= 1 }
        if ignoreCancellation { await Task.detached { try? await Task.sleep(for: self.delay) }.value }
        else { try await Task.sleep(for: delay) }
        var document = LyricsDocument(lines: body ? [.init(start: 1, end: 10, text: "Original lyric", secondary: "Translated lyric")] : [], provider: kind)
        for index in document.lines.indices {
            document.lines[index].translationOrigin = .provider
            document.lines[index].translationLanguage = "en"; document.lines[index].translationLanguageIsExplicit = true
        }
        document.providerDataRevision = 3
        return document.binding(.init(title: query.title, artist: query.artist, album: query.album, duration: query.duration, score: 8, candidateID: "fixture"), to: query)
    }
}

@Test func lyricsFallbackPreservesBackupPriorityAndFractionalCacheIdentity() async throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let primary = LookupProbe(.netEase, body: false), backup = LookupProbe(.qqMusic, delay: .milliseconds(350))
    let fast = LookupProbe(.lrclib, delay: .milliseconds(5)), emptyKugou = LookupProbe(.kugou, body: false), emptyAMLL = LookupProbe(.amll, body: false)
    let lookup = LyricsLookup(sources: [.netEase: primary, .qqMusic: backup, .lrclib: fast, .kugou: emptyKugou, .amll: emptyAMLL], cache: LyricsCache(directory: directory))
    var settings = LyricsSettings(); settings.backupProvider = .qqMusic
    var query = LyricsQuery(title: "Song", artist: "Artist", duration: 180.1, trackIdentity: "recording")
    query.preferredTranslationLanguage = "en"
    let result = try await lookup.query(query, settings: settings)
    #expect(result.document.provider == .qqMusic)
    #expect(await fast.calls == 1) // The backup's 150 ms admission cannot monopolize the window.
    #expect(result.completedProviders > 0 && result.failures.isEmpty)
    _ = try await lookup.query(query, settings: settings)
    #expect(await backup.calls == 1)
    query.duration = 180.2
    _ = try await lookup.query(query, settings: settings)
    #expect(await backup.calls == 2)
    await lookup.stop()
}

@Test func lyricsCancellationRetainsInvocationSlotsAndRejectsLateSuccess() async throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let source = LookupProbe(.netEase, delay: .milliseconds(400), ignoreCancellation: true)
    let cache = LyricsCache(directory: directory), lookup = LyricsLookup(sources: [.netEase: source], cache: LyricsCache(directory: directory))
    var settings = LyricsSettings(); settings.searchRemainingProviders = false
    let configured = settings
    for index in 0..<4 {
        let task = Task { try await lookup.query(.init(title: "Song", artist: "Artist", trackIdentity: "track-\(index)"), settings: configured) }
        try await Task.sleep(for: .milliseconds(20)); task.cancel()
        do { _ = try await task.value; Issue.record("Cancelled lookup published a result") } catch is CancellationError { }
    }
    await lookup.stop()
    #expect(await source.peak <= 2)
    #expect(await source.active == 0)
    #expect(try await cache.usage() == 0)
}

@Test func completedEmptyLyricsSearchIsNotMisreportedAsTotalFailure() async throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let lookup = LyricsLookup(sources: [.qqMusic: LookupProbe(.qqMusic, body: false)], cache: LyricsCache(directory: directory))
    var settings = LyricsSettings(); settings.backupProvider = .qqMusic; settings.searchRemainingProviders = false
    let result = try await lookup.query(.init(title: "Song", artist: "Artist"), settings: settings)
    #expect(result.document.lines.isEmpty && result.completedProviders == 1 && result.failures.count == 1)
}

private actor SearchEvents {
    var values: [String] = []
    func add(_ value: String) { values.append(value) }
}
private struct ProgressiveProbe: LyricsSource {
    let kind: LyricsProvider
    let action: @Sendable (LyricsQuery, LyricsSourceProgress) async throws -> LyricsDocument
    func query(_ query: LyricsQuery) async throws -> LyricsDocument { try await action(query, .init()) }
    func query(_ query: LyricsQuery, progress: LyricsSourceProgress) async throws -> LyricsDocument { try await action(query, progress) }
}
private func progressiveDocument(_ query: LyricsQuery, _ provider: LyricsProvider, translated: Bool) -> LyricsDocument {
    var row = LyricLine(start: 1, end: 10, text: "这是用于来源验证的原文", secondary: translated ? "This is the provider translation" : nil)
    if translated { row.translationOrigin = .provider; row.translationLanguage = "en"; row.translationLanguageIsExplicit = true }
    var value = LyricsDocument(lines: [row], provider: provider)
    value.providerDataRevision = 3
    value = value.binding(.init(title: query.title, artist: query.artist, album: query.album, duration: query.duration, score: 8, candidateID: "recording"), to: query)
    return LyricsLanguagePolicy.prepare(value, target: "en", original: .init(label: "zh", score: 1, runnerUpScore: 0))
}

@Test func progressiveLyricsStartsFallbackBeforePrimaryCompletesAndKeepsBetterNative() async throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let events = SearchEvents()
    let primary = ProgressiveProbe(kind: .netEase) { query, progress in
        _ = try await progress.prepare(progressiveDocument(query, .netEase, translated: false), query: query)
        try await Task.sleep(for: .milliseconds(300))
        await events.add("primary-complete")
        return try await progress.prepare(progressiveDocument(query, .netEase, translated: true), query: query)
    }
    let backup = ProgressiveProbe(kind: .qqMusic) { query, progress in
        await events.add("backup-start")
        return try await progress.prepare(progressiveDocument(query, .qqMusic, translated: true), query: query)
    }
    let lookup = LyricsLookup(sources: [.netEase: primary, .qqMusic: backup], cache: LyricsCache(directory: directory))
    var settings = LyricsSettings(); settings.backupProvider = .qqMusic; settings.searchRemainingProviders = false
    var query = LyricsQuery(title: "Song", artist: "Artist", trackIdentity: "current"); query.preferredTranslationLanguage = "en"
    let result = try await lookup.query(query, settings: settings) { value in await events.add("preview-\(value.provider)-\(value.lines.first?.secondary != nil)") }
    #expect(result.document.provider == .netEase && result.document.lines.first?.secondary != nil)
    let recorded = await events.values
    #expect(try #require(recorded.firstIndex(of: "backup-start")) < #require(recorded.firstIndex(of: "primary-complete")))
    #expect(recorded.contains("preview-qqMusic-true"))
    await lookup.stop()
}

@Test func progressiveLyricsUsesFirstOriginalThreeSecondWindow() async throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: directory) }
    let primary = ProgressiveProbe(kind: .netEase) { query, progress in
        let original = try await progress.prepare(progressiveDocument(query, .netEase, translated: false), query: query)
        try await Task.sleep(for: .seconds(10)); return original
    }
    let lookup = LyricsLookup(sources: [.netEase: primary], cache: LyricsCache(directory: directory))
    var settings = LyricsSettings(); settings.searchRemainingProviders = false
    var query = LyricsQuery(title: "Song", artist: "Artist", trackIdentity: "current"); query.preferredTranslationLanguage = "en"
    let start = ContinuousClock.now
    let result = try await lookup.query(query, settings: settings)
    #expect(start.duration(to: .now) >= .seconds(3) && start.duration(to: .now) < .seconds(5))
    #expect(result.document.lines.count == 1 && result.document.lines.first?.secondary == nil)
    #expect(result.completedProviders == 0)
    await lookup.stop()
}

@Test func closedLyricsSearchOnlyAcceptsCurrentNativeVetoAndCallerCancellationWins() async throws {
    var query = LyricsQuery(title: "Song", artist: "Artist", trackIdentity: "current"); query.preferredTranslationLanguage = "en"
    let cancelled = LyricsSearchCancellation(), signal = AsyncStream<Void>.makeStream(), events = SearchEvents()
    let session = LyricsSearchSession(query: query, primary: .netEase, backup: nil, cancelled: cancelled, signal: signal.continuation) { value in await events.add(value.lines.first?.secondary ?? "original") }
    await session.report(progressiveDocument(query, .netEase, translated: false)); _ = await session.close()
    await session.report(progressiveDocument(query, .qqMusic, translated: false))
    var other = query; other.trackIdentity = "previous"
    await session.report(progressiveDocument(other, .qqMusic, translated: true))
    #expect(await events.values.count == 1)
    await session.report(progressiveDocument(query, .qqMusic, translated: true))
    #expect(await events.values.count == 2)
    cancelled.cancel()
    await session.report(progressiveDocument(query, .netEase, translated: true))
    #expect(await events.values.count == 2)
    signal.continuation.finish()
}
