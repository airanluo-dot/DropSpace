import Foundation
import DropSpaceCore

/// Public response bytes only: callers always repeat recording matching and
/// bind their own playback identity. No credentials or matched documents live here.
actor NetEaseResponseCache {
    private enum Outcome: Sendable { case payload(Data), failure(any Error), retry }
    // Entry references cross cancellation closures; every field is accessed only
    // by this cache actor, including transport completion and waiter removal.
    private final class Entry: @unchecked Sendable {
        let id = UUID()
        var payload: Data?
        var savedAt: ContinuousClock.Instant = .now
        var lifetime: Duration = .zero
        var target = ""
        var order: UInt64 = 0
        var owner: UUID?
        var ownerCancellation: LyricsSearchCancellation?
        var request: Task<Void, Never>?
        var waiters: [UUID: CheckedContinuation<Outcome, Never>] = [:]
    }
    private let http: any LyricsTransport
    private let now: @Sendable () -> ContinuousClock.Instant
    private let maximumEntries: Int
    private let maximumBytes: Int
    private var entries: [URL: Entry] = [:]
    private var bytes = 0
    private var order: UInt64 = 0
    init(http: any LyricsTransport, maximumEntries: Int = 128, maximumBytes: Int = 16 * 1024 * 1024,
         now: @escaping @Sendable () -> ContinuousClock.Instant = { .now }) {
        self.http = http; self.maximumEntries = maximumEntries; self.maximumBytes = maximumBytes; self.now = now
    }
    func clear() { entries.removeAll(); bytes = 0 }
    func get(_ url: URL, refresh: Bool, target: String) async throws -> LyricsJSON {
        if refresh, let previous = entries.removeValue(forKey: url) { bytes -= previous.payload?.count ?? 0 }
        let normalized = Self.normalizedTarget(target)
        while true {
            try Task.checkCancellation()
            let entry = acquire(url)
            let lifetime = !url.path.contains("search") && entry.target != normalized ? .seconds(1) : entry.lifetime
            if let payload = entry.payload, entry.savedAt.duration(to: now()) < lifetime {
                return try JSONDecoder().decode(LyricsJSON.self, from: payload)
            }
            let waiter = UUID(), cancellation = LyricsSearchCancellation()
            let outcome: Outcome = await withTaskCancellationHandler {
                await withCheckedContinuation { continuation in
                    guard !cancellation.isCancelled else { continuation.resume(returning: .failure(CancellationError())); return }
                    entry.waiters[waiter] = continuation
                    if entry.owner == nil {
                        entry.owner = waiter; entry.ownerCancellation = cancellation
                        entry.request = Task {
                            let result: Outcome
                            do {
                                let response = try await self.http.get(url)
                                try Task.checkCancellation()
                                if cancellation.isCancelled { throw CancellationError() }
                                let payload = try JSONEncoder().encode(response)
                                self.store(response, payload: payload, url: url, entry: entry, target: normalized)
                                result = .payload(payload)
                            } catch { result = .failure(error) }
                            self.finish(entry, url: url, result: result)
                        }
                    }
                }
            } onCancel: {
                cancellation.cancel()
                Task { await self.cancel(url: url, entry: entry, waiter: waiter) }
            }
            try Task.checkCancellation()
            switch outcome {
            case .payload(let payload): return try JSONDecoder().decode(LyricsJSON.self, from: payload)
            case .failure(let error): throw error
            case .retry: continue
            }
        }
    }
    private func acquire(_ url: URL) -> Entry {
        order &+= 1
        if let entry = entries[url] { entry.order = order; return entry }
        let entry = Entry(); entry.order = order
        while entries.count >= maximumEntries {
            guard evictOldest() else { return entry } // Active owners are never evicted to make space.
        }
        entries[url] = entry; return entry
    }
    private func evictOldest(withBytes: Bool = false) -> Bool {
        guard let oldest = entries.filter({ $0.value.owner == nil && (!withBytes || $0.value.payload != nil) }).min(by: { $0.value.order < $1.value.order }) else { return false }
        entries.removeValue(forKey: oldest.key); bytes -= oldest.value.payload?.count ?? 0; return true
    }
    private func store(_ root: LyricsJSON, payload: Data, url: URL, entry: Entry, target: String) {
        guard entries[url] === entry, entry.ownerCancellation?.isCancelled != true, Self.reusable(root, url: url), payload.count <= maximumBytes else { return }
        while bytes - (entry.payload?.count ?? 0) + payload.count > maximumBytes {
            guard evictOldest(withBytes: true) else { return }
        }
        bytes += payload.count - (entry.payload?.count ?? 0)
        entry.payload = payload; entry.savedAt = now(); entry.target = target
        if url.path.contains("search") { entry.lifetime = root["result"]["songs"].array.isEmpty ? .seconds(1) : .seconds(600) }
        else {
            let document = try? NetEaseLyricsProvider.parse(root)
            let complete = document.map { !$0.lines.isEmpty && !LyricsSelection.needsLookup($0, target: target) } ?? false
            entry.lifetime = complete ? .seconds(600) : .seconds(1)
        }
    }
    private func finish(_ entry: Entry, url: URL, result: Outcome) {
        if entry.ownerCancellation?.isCancelled == true {
            if entries[url] === entry { entries.removeValue(forKey: url); bytes -= entry.payload?.count ?? 0 }
            let owned = entry.owner, waiters = entry.waiters
            entry.waiters = [:]; entry.owner = nil; entry.ownerCancellation = nil; entry.request = nil
            for (id, waiter) in waiters { waiter.resume(returning: id == owned ? .failure(CancellationError()) : .retry) }
            return
        }
        let waiters = entry.waiters.values
        entry.waiters = [:]; entry.owner = nil; entry.ownerCancellation = nil; entry.request = nil
        for waiter in waiters { waiter.resume(returning: result) }
    }
    private func cancel(url: URL, entry: Entry, waiter: UUID) {
        guard let continuation = entry.waiters.removeValue(forKey: waiter) else { return }
        continuation.resume(returning: .failure(CancellationError()))
        if entry.owner == waiter {
            if entries[url] === entry { entries.removeValue(forKey: url); bytes -= entry.payload?.count ?? 0 }
            entry.request?.cancel()
            // Surviving callers take ownership of a fresh entry immediately;
            // a cancellation-ignoring old transport cannot repopulate that entry.
            let waiting = entry.waiters.values; entry.waiters = [:]
            for continuation in waiting { continuation.resume(returning: .retry) }
        }
    }
    private static func normalizedTarget(_ target: String) -> String {
        let value = target.trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "_", with: "-").lowercased()
        switch value {
        case "zh", "zh-cn", "zh-sg", "zh-hans", "zh-hans-cn": return "zh-Hans"
        case "zh-tw", "zh-hk", "zh-mo", "zh-hant", "zh-hant-tw": return "zh-Hant"
        default: return value.split(separator: "-").first.map(String.init) ?? ""
        }
    }
    private static func reusable(_ root: LyricsJSON, url: URL) -> Bool {
        guard root.isObject, root["code"].number == 200 else { return false }
        if url.path.contains("search") {
            guard root["result"].isObject, let songs = root["result"]["songs"].allArray else { return false }
            return songs.allSatisfy { song in
                guard song.isObject, case .string = song["name"] else { return false }
                switch song["id"] { case .string, .number: return true; default: return false }
            }
        }
        func hasLyric(_ name: String) -> Bool { if case .string = root[name]["lyric"] { return root[name].isObject }; return false }
        guard hasLyric("lrc") || hasLyric("yrc") else { return false }
        return ["lrc", "yrc", "tlyric", "ytlrc"].allSatisfy { name in
            if root[name].isNull { return true }
            guard root[name].isObject else { return false }
            switch root[name]["lyric"] { case .string, .null: return true; default: return false }
        }
    }
}
