import Foundation
import DropSpaceCore

// Cancellation must be visible synchronously to a late callback, even if the
// lookup's actor is suspended while a native view publishes the previous row.
final class LyricsSearchCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false
    var isCancelled: Bool { lock.withLock { value } }
    func cancel() { lock.withLock { value = true } }
}

struct LyricsSearchSnapshot: Sendable {
    var document: LyricsDocument
    var completed = Set<LyricsProvider>()
    var reported = Set<LyricsProvider>()
    var failedProviders = Set<LyricsProvider>()
    var failures: [String] = []
    var hasOriginal = false
    var preferredTarget = false
    var windowDeadline: ContinuousClock.Instant?
    var completedProviders: Int { completed.subtracting(failedProviders).count }
}

actor LyricsSearchSession {
    private let query: LyricsQuery
    private let primary: LyricsProvider
    private let backup: LyricsProvider?
    private let cancelled: LyricsSearchCancellation
    private let signal: AsyncStream<Void>.Continuation
    private let preview: @Sendable (LyricsDocument) async -> Void
    private var state: LyricsSearchSnapshot
    private var closed = false, publishing = false
    private var pendingPublication: LyricsDocument?

    init(query: LyricsQuery, primary: LyricsProvider, backup: LyricsProvider?, cancelled: LyricsSearchCancellation,
         signal: AsyncStream<Void>.Continuation, preview: @escaping @Sendable (LyricsDocument) async -> Void) {
        self.query = query; self.primary = primary; self.backup = backup; self.cancelled = cancelled
        self.signal = signal; self.preview = preview
        state = .init(document: .init(lines: [], provider: primary))
    }
    func snapshot() -> LyricsSearchSnapshot { state }
    func close() -> LyricsSearchSnapshot { closed = true; return state }
    func report(_ candidate: LyricsDocument) async {
        guard !cancelled.isCancelled else { return }
        let value = LyricsSelection.validate(candidate, query: query)
        guard LyricsSelection.usable(value) else { return }
        if closed {
            // A completed source window stays closed. Only a native translation
            // veto bound to this exact recording can retire in-flight local AI.
            guard candidate.match?.trackIdentity == query.trackIdentity,
                  value.translationAdmission?.decision == .providerTarget,
                  LyricsLanguagePolicy.current(value, target: query.preferredTranslationLanguage) else { return }
            pendingPublication = value
        } else {
            state.reported.insert(value.provider); state.hasOriginal = true
            if state.windowDeadline == nil, LyricsSelection.needsLookup(value, target: query.preferredTranslationLanguage) || value.bodyQuality == .confirmedInstrumental {
                state.windowDeadline = .now.advanced(by: .seconds(3))
            }
            let next = LyricsSelection.choose(state.document, value, target: query.preferredTranslationLanguage, primary: primary, backup: backup)
            if next != state.document { state.document = next; pendingPublication = next }
            if value.provider == primary, !query.preferredTranslationLanguage.isEmpty, LyricsSelection.completeTarget(value, target: query.preferredTranslationLanguage) {
                state.preferredTarget = true
            }
            signal.yield(())
        }
        // One publisher drains replacements in order. Actor reentrancy cannot
        // allow an older original callback to overwrite a native translation.
        guard !publishing else { return }
        publishing = true
        while let value = pendingPublication, !cancelled.isCancelled {
            pendingPublication = nil; await preview(value)
        }
        pendingPublication = nil; publishing = false
    }
    func finished(_ kind: LyricsProvider, reply: LyricsProviderReply) async {
        guard !closed, !cancelled.isCancelled else { return }
        if let value = reply.document { await report(value) }
        guard !closed, !cancelled.isCancelled else { return }
        state.completed.insert(kind)
        if let failure = reply.failure { state.failedProviders.insert(kind); state.failures.append("\(kind.rawValue): \(failure)") }
        signal.yield(())
    }
}
