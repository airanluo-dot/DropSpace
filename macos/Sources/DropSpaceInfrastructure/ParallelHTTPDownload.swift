import Foundation
import DropSpaceCore

private struct RangeIdentity: Codable, Equatable, Sendable {
    let source: String
    let etag: String
    let length: Int64
    let parts: Int
}
private struct RangeRejected: Error {}
private struct ContentRange {
    let from: Int64, to: Int64, length: Int64
    init?(_ text: String?) {
        guard let text, text.hasPrefix("bytes ") else { return nil }
        let pieces = text.dropFirst(6).split(separator: "/", omittingEmptySubsequences: false)
        guard pieces.count == 2 else { return nil }
        let bounds = pieces[0].split(separator: "-", omittingEmptySubsequences: false)
        guard bounds.count == 2,
              [bounds[0], bounds[1], pieces[1]].allSatisfy({ !$0.isEmpty && $0.utf8.allSatisfy { (48...57).contains($0) } }),
              let from = Int64(bounds[0]), let to = Int64(bounds[1]), let length = Int64(pieces[1]),
              from >= 0, to >= from, to < length else { return nil }
        self.from = from; self.to = to; self.length = length
    }
}

private actor RangeProgress {
    private var completed: [Int64]
    private var next = 0
    private(set) var rejected = false
    private var reportTime = ContinuousClock.now
    private let total: Int64
    private let report: @Sendable (Int64, Int64?) async -> Void
    init(completed: [Int64], total: Int64, report: @escaping @Sendable (Int64, Int64?) async -> Void) {
        self.completed = completed; self.total = total; self.report = report
    }
    func claim() -> Int? { guard next < completed.count else { return nil }; defer { next += 1 }; return next }
    func snapshot() -> (bytes: Int64, remaining: Bool) { (completed.reduce(0, +), next < completed.count) }
    func reject() { rejected = true }
    func reportNow() async { await report(completed.reduce(0, +), total) }
    func update(_ index: Int, bytes: Int64, force: Bool = false) async {
        completed[index] = bytes
        if force || reportTime.duration(to: .now) >= .milliseconds(100) {
            reportTime = .now; await report(completed.reduce(0, +), total)
        }
    }
}

/// A transfer-local range plan shares the same connection and bandwidth leases
/// as sequential downloads, model parts, official modules and app updates.
struct ParallelHTTPDownload: Sendable {
    let engine: HTTPDownloader
    let budget: DownloadBudget
    let observation: DownloadObservation
    private enum Event: Sendable { case worker, tick }
    func download(source: URL, files: DownloadFiles, expected: Int64?, maximum: Int64?,
                  trusted: @escaping @Sendable (URL) -> Bool,
                  progress: @escaping @Sendable (Int64, Int64?) async -> Void) async throws -> Bool {
        guard let probe = try await DownloadRetry.run(retrying: { attempt in await observation.stage(.retryWaiting, attempt: attempt) }, { try await self.probe(source, trusted: trusted) }) else { return false }
        if let expected, expected != probe.length { throw DropSpaceError.invalid("Unexpected download size") }
        if let maximum, probe.length > maximum { throw DropSpaceError.limit("Download size limit") }
        let sourceIdentity = Digests.sha256(Data(source.absoluteString.utf8))
        let old: RangeIdentity?
        if let size = try files.size(files.identity), size < 16384, let bytes = try files.read(files.identity) {
            old = try? JSONDecoder().decode(RangeIdentity.self, from: bytes)
        } else { old = nil }
        let same = old.map { (1...256).contains($0.parts) && $0.source == sourceIdentity && $0.etag == probe.etag && $0.length == probe.length } ?? false
        let maximumConnections = await budget.connections.configuredLimit
        let plan = try HTTPByteRange.plan(length: probe.length, connections: same ? old!.parts : maximumConnections)
        guard plan.count > 1 else { return false }
        try files.prepareRanges()
        let identity = RangeIdentity(source: sourceIdentity, etag: probe.etag, length: probe.length, parts: plan.count)
        if !same {
            for index in 0..<256 { try files.remove(files.part(index)) }
            try files.write(identity, to: files.identity)
        }
        var completed = [Int64](repeating: 0, count: plan.count)
        for index in plan.indices {
            if let size = try files.size(files.part(index)) {
                if size > plan[index].length { try files.remove(files.part(index)) }
                else { completed[index] = size }
            }
        }
        let resumed = completed.reduce(0, +)
        try files.remove(files.staging)
        let (needed, overflow) = identity.length.addingReportingOverflow(identity.length - resumed)
        guard !overflow else { throw DropSpaceError.limit("Download storage requirement") }
        try files.requireSpace(needed)
        let state = RangeProgress(completed: completed, total: identity.length, report: { bytes, total in
            await observation.bytes(bytes, total: total); await progress(bytes, total)
        })
        await observation.begin(bytes: resumed, total: identity.length)
        await progress(resumed, identity.length)
        do { try await schedule(plan, maximum: maximumConnections, source: source, files: files, identity: identity, state: state, trusted: trusted) }
        catch {
            await state.reportNow()
            try Task.checkCancellation()
            if await state.rejected {
                // The task group has joined every worker before discarding the
                // validator and switching to a fresh sequential representation.
                try files.remove(files.identity)
                try files.remove(files.staging.appendingPathExtension("resume.json"))
                return false
            }
            throw error
        }
        try Task.checkCancellation()
        try files.remove(files.staging.appendingPathExtension("resume.json"))
        await observation.stage(.merging)
        try await assemble(plan, files: files, length: identity.length)
        await progress(identity.length, identity.length)
        return true
    }
    private func probe(_ source: URL, trusted: @escaping @Sendable (URL) -> Bool) async throws -> (etag: String, length: Int64)? {
        await observation.stage(.waitingConnection)
        let lease = try await budget.connections.acquire()
        await observation.connected(1); await observation.stage(.probing)
        do {
            let (body, response) = try await engine.request(source: source, offset: 0, etag: nil, end: 0, trusted: trusted)
            defer { body.task.cancel() }
            guard (200...299).contains(response.statusCode) || response.statusCode == 416 else { throw DownloadHTTPFailure(response) }
            guard response.statusCode == 206, !encoded(response),
                  let range = ContentRange(response.value(forHTTPHeaderField: "Content-Range")), range.from == 0, range.to == 0,
                  let tag = response.value(forHTTPHeaderField: "ETag"), strong(tag),
                  response.value(forHTTPHeaderField: "Content-Length").map({ $0 == "1" }) ?? true else {
                await observation.connected(-1); await budget.connections.release(lease); return nil
            }
            var count = 0
            for try await _ in body { count += 1; if count > 1 { break } }
            guard count == 1 else { await observation.connected(-1); await budget.connections.release(lease); return nil }
            try await budget.bandwidth.consume(1)
            await observation.connected(-1); await budget.connections.release(lease); return (tag, range.length)
        } catch { await observation.connected(-1); await budget.connections.release(lease); throw error }
    }
    private func schedule(_ plan: [HTTPByteRange], maximum: Int, source: URL, files: DownloadFiles,
                          identity: RangeIdentity, state: RangeProgress, trusted: @escaping @Sendable (URL) -> Bool) async throws {
        let ceiling = min(plan.count, max(1, maximum))
        try await withThrowingTaskGroup(of: Event.self) { tasks in
            var created = min(4, ceiling), live = created
            let worker: @Sendable () async throws -> Event = {
                while let index = await state.claim() {
                    try Task.checkCancellation()
                    do { try await DownloadRetry.run(retrying: { attempt in await observation.stage(.retryWaiting, attempt: attempt) }) {
                        try await self.part(plan[index], index: index, source: source, files: files, identity: identity, state: state, trusted: trusted)
                    } }
                    catch is RangeRejected { await state.reject(); throw RangeRejected() }
                }
                return .worker
            }
            for _ in 0..<created { tasks.addTask(operation: worker) }
            var previous = await state.snapshot().bytes
            if created < ceiling { tasks.addTask { try await Task.sleep(for: .milliseconds(150)); return .tick } }
            defer { tasks.cancelAll() }
            while live > 0, let event = try await tasks.next() {
                switch event {
                case .worker: live -= 1
                case .tick:
                    let current = await state.snapshot()
                    if current.bytes > previous && current.remaining {
                        let target = min(ceiling, created * 2)
                        for _ in created..<target { tasks.addTask(operation: worker); live += 1 }
                        created = target
                    }
                    previous = current.bytes
                    if created < ceiling && current.remaining { tasks.addTask { try await Task.sleep(for: .milliseconds(150)); return .tick } }
                }
            }
        }
    }
    private func part(_ range: HTTPByteRange, index: Int, source: URL, files: DownloadFiles, identity: RangeIdentity,
                      state: RangeProgress, trusted: @escaping @Sendable (URL) -> Bool) async throws {
        let file = files.part(index), existing = try files.size(file) ?? 0
        if existing == range.length { return }
        guard existing < range.length else { throw RangeRejected() }
        let lease = try await budget.connections.acquire()
        await observation.connected(1)
        do {
            let (body, response) = try await engine.request(source: source, offset: range.from + existing, etag: identity.etag, end: range.to, trusted: trusted)
            defer { body.task.cancel() }
            if response.statusCode == 200 || response.statusCode == 416 { throw RangeRejected() }
            guard (200...299).contains(response.statusCode) else { throw DownloadHTTPFailure(response) }
            guard response.statusCode == 206, !encoded(response),
                  let receivedRange = ContentRange(response.value(forHTTPHeaderField: "Content-Range")),
                  receivedRange.from == range.from + existing, receivedRange.to == range.to, receivedRange.length == identity.length,
                  response.value(forHTTPHeaderField: "ETag") == identity.etag,
                  response.value(forHTTPHeaderField: "Content-Length").map({ Int64($0) == range.length - existing }) ?? true else { throw RangeRejected() }
            let output = try files.open(file, append: true); defer { try? output.close() }
            guard try output.seekToEnd() == existing else { throw DropSpaceError.invalid("Download fragment changed") }
            var buffer = Data(); buffer.reserveCapacity(131072)
            var written = existing
            for try await byte in body {
                buffer.append(byte)
                guard Int64(buffer.count) <= range.length - written else { throw RangeRejected() }
                if buffer.count == 131072 {
                    try Task.checkCancellation(); try await budget.bandwidth.consume(buffer.count)
                    try output.write(contentsOf: buffer); written += Int64(buffer.count); buffer.removeAll(keepingCapacity: true)
                    await state.update(index, bytes: written)
                }
            }
            if !buffer.isEmpty {
                try Task.checkCancellation(); try await budget.bandwidth.consume(buffer.count)
                try output.write(contentsOf: buffer); written += Int64(buffer.count)
            }
            try output.synchronize(); await state.update(index, bytes: written, force: true)
            guard written == range.length else { throw URLError(.networkConnectionLost) }
            await observation.connected(-1); await budget.connections.release(lease)
        } catch { await observation.connected(-1); await budget.connections.release(lease); throw error }
    }
    private func assemble(_ plan: [HTTPByteRange], files: DownloadFiles, length: Int64) async throws {
        let task = Task.detached(priority: .utility) {
            let output = try files.open(files.staging); defer { try? output.close() }
            var written: Int64 = 0
            for index in plan.indices {
                try Task.checkCancellation()
                let input = try files.open(files.part(index), reading: true); defer { try? input.close() }
                guard try input.seekToEnd() == plan[index].length else { throw DropSpaceError.invalid("Incomplete download fragment") }
                try input.seek(toOffset: 0)
                var copied: Int64 = 0
                while let bytes = try input.read(upToCount: 131072), !bytes.isEmpty {
                    try Task.checkCancellation(); copied += Int64(bytes.count)
                    guard copied <= plan[index].length else { throw DropSpaceError.invalid("Download fragment changed") }
                    try output.write(contentsOf: bytes); written += Int64(bytes.count)
                }
                guard copied == plan[index].length else { throw DropSpaceError.invalid("Incomplete download fragment") }
            }
            guard written == length else { throw DropSpaceError.invalid("Download assembly length") }
            try output.synchronize()
        }
        try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    private func encoded(_ response: HTTPURLResponse) -> Bool {
        response.value(forHTTPHeaderField: "Content-Encoding").map { $0.lowercased() != "identity" } ?? false
    }
    private func strong(_ tag: String) -> Bool {
        let bytes = Array(tag.utf8)
        return bytes.count >= 2 && bytes.first == 34 && bytes.last == 34 && bytes.dropFirst().dropLast().allSatisfy { $0 >= 33 && $0 != 34 && $0 != 127 }
    }
}
