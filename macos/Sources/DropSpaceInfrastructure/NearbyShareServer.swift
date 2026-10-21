import Foundation
import Security
import DropSpaceCore

public actor NearbyShareServer {
    private struct Share {
        let descriptor: ShareDescriptor, token: Data, sources: [ShareSource]
        var receivers = Set<String>()
    }
    private let limits: ShareLimits
    private let resolveAddress: @Sendable () throws -> String
    private let now: @Sendable () -> Date
    private let gate = DownloadPermitPool(limit: 1)
    private var shares: [UUID: Share] = [:]
    private var revision: UInt64 = 0, networkRevision: UInt64 = 0
    private var transport: NearbyShareHTTPServer?
    private var transportID = UUID()
    private var endpoint: URL?
    private var disposed = false
    private var expiry: Task<Void, Never>?
    private var observers: [UUID: AsyncStream<NearbyShareSnapshot>.Continuation] = [:]
    public init(limits: ShareLimits = .init(), resolveAddress: @escaping @Sendable () throws -> String, now: @escaping @Sendable () -> Date = { Date() }) throws {
        try limits.validate(); self.limits = limits; self.resolveAddress = resolveAddress; self.now = now
    }
    public func snapshots() -> AsyncStream<NearbyShareSnapshot> {
        let id = UUID()
        return AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            observers[id] = continuation; continuation.yield(snapshot())
            continuation.onTermination = { [weak self] _ in Task { await self?.removeObserver(id) } }
        }
    }
    private func removeObserver(_ id: UUID) { observers[id] = nil }
    public func snapshot() -> NearbyShareSnapshot { prune(); return currentSnapshot() }
    private func currentSnapshot() -> NearbyShareSnapshot {
        .init(revision: revision, items: shares.values.map { .init(descriptor: $0.descriptor, name: $0.sources[0].name) }.sorted {
            $0.expiresAt == $1.expiresAt ? $0.id.dropLinkD < $1.id.dropLinkD : $0.expiresAt < $1.expiresAt
        })
    }
    public func descriptor(_ id: UUID) -> ShareDescriptor? {
        prune(); guard !disposed, endpoint != nil else { return nil }; return shares[id]?.descriptor
    }
    public func create(_ sources: [ShareSource], lifetime: TimeInterval? = nil) async throws -> ShareDescriptor {
        guard !disposed else { throw CancellationError() }
        guard (1...limits.internetMaxItems).contains(sources.count), Set(sources.map(\.id)).count == sources.count else { throw DropSpaceError.limit("Share item count or duplicate identity") }
        var total: Int64 = 0
        for source in sources { try source.validate(); guard source.length <= limits.internetMaxBytes - total else { throw DropSpaceError.limit("Nearby share size limit") }; total += source.length }
        let duration = lifetime ?? Double(limits.nearbyTTLMinutes * 60)
        guard duration > 0, duration <= Double(limits.nearbyTTLMinutes * 60) else { throw DropSpaceError.invalid("Nearby share lifetime") }
        let permit = try await gate.acquire()
        do {
            let result = try await createOwned(sources, total: total, duration: duration); await gate.release(permit); return result
        } catch { await gate.release(permit); throw error }
    }
    private func createOwned(_ sources: [ShareSource], total: Int64, duration: TimeInterval) async throws -> ShareDescriptor {
        try Task.checkCancellation(); guard !disposed else { throw CancellationError() }
        let address: String
        do { address = try resolveAddress(); guard NearbySharePolicy.isPrivate(address) else { throw DropSpaceError.unavailable("No reachable private IPv4 network is available") } }
        catch { clear(); endpoint = nil; if let transport, await transport.stop() { self.transport = nil }; throw error }
        if let transport, endpoint?.host != address {
            clear(); endpoint = nil
            guard await transport.stop() else { throw DropSpaceError.unavailable("Nearby share is completing its network transition") }; self.transport = nil
        }
        let generation = networkRevision
        if transport == nil {
            let identity = UUID(); transportID = identity
            let transport = NearbyShareHTTPServer(handler: { [weak self] head, address in
                guard let self else { return .init(status: 404) }; return try await self.handle(head, address: address)
            }, onInvalidated: { [weak self] in await self?.transportFailed(identity) })
            self.transport = transport
            do {
                let url = try await dropLinkDeadline(seconds: 30) { try await transport.start(address: address) }
                guard !disposed, generation == networkRevision else { throw CancellationError() }; endpoint = url
            } catch {
                endpoint = nil; if await transport.stop() { self.transport = nil }; throw error
            }
        }
        try Task.checkCancellation()
        guard !disposed, generation == networkRevision, let endpoint else { throw DropSpaceError.unavailable("Nearby network changed before publication") }
        prune(); guard shares.count < limits.maxNearbyShares else { throw DropSpaceError.limit("Nearby share capacity is full") }
        var bytes = Data(count: limits.nearbyTokenBytes)
        guard bytes.withUnsafeMutableBytes({ SecRandomCopyBytes(kSecRandomDefault, $0.count, $0.baseAddress!) }) == errSecSuccess else { throw DropSpaceError.unavailable("Nearby token entropy") }
        let token = bytes.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        let id = UUID(), url = endpoint.appendingPathComponent("s/" + id.dropLinkN + "/" + token)
        let descriptor = ShareDescriptor(id: id, url: url, expiresAt: now().addingTimeInterval(duration), itemCount: sources.count, totalBytes: total, isEncrypted: false)
        shares[id] = .init(descriptor: descriptor, token: Data(token.utf8), sources: sources); revision &+= 1; scheduleExpiry(); publish(); return descriptor
    }
    private func transportFailed(_ identity: UUID) async {
        guard transportID == identity, !disposed else { return }
        networkRevision &+= 1; endpoint = nil; clear()
        let permit: UUID
        do { permit = try await gate.acquire() } catch { return }
        if transportID == identity, let transport, await transport.stop() { self.transport = nil }
        await gate.release(permit)
    }
    public func revoke(_ id: UUID) -> Bool {
        prune(); guard shares.removeValue(forKey: id) != nil else { return false }
        revision &+= 1; scheduleExpiry(); publish(); return true
    }
    public func networkChanged() async {
        networkRevision &+= 1; endpoint = nil; clear()
        let permit: UUID
        do { permit = try await gate.acquire() } catch { return }
        if let transport, await transport.stop() { self.transport = nil }; await gate.release(permit)
    }
    public func stop() async -> Bool {
        disposed = true; networkRevision &+= 1; endpoint = nil; clear()
        let permit: UUID
        do { permit = try await gate.acquire() } catch { return false }
        let retired = await transport?.stop() ?? true
        if retired { transport = nil }
        for continuation in observers.values { continuation.finish() }; observers.removeAll()
        await gate.release(permit); return retired
    }
    private func clear() {
        expiry?.cancel(); expiry = nil
        guard !shares.isEmpty else { return }; shares.removeAll(); revision &+= 1; publish()
    }
    private func prune() {
        let expired = shares.filter { $0.value.descriptor.expiresAt <= now() }.map(\.key)
        guard !expired.isEmpty else { return }
        for id in expired { shares[id] = nil }; revision &+= 1; scheduleExpiry(); publish()
    }
    private func publish() { let value = currentSnapshot(); for continuation in observers.values { continuation.yield(value) } }
    private func scheduleExpiry() {
        expiry?.cancel(); expiry = nil
        guard !disposed, let next = shares.values.map(\.descriptor.expiresAt).min() else { return }
        let delay = max(0.001, next.timeIntervalSince(now()))
        expiry = Task { [weak self] in do { try await Task.sleep(for: .seconds(delay)); await self?.prune() } catch {} }
    }
    func handle(_ head: DropLinkHTTPHead, address: String) async throws -> NearbyHTTPResponse {
        guard NearbySharePolicy.isPrivate(address) else { return .init(status: 403) }
        guard ["GET", "HEAD"].contains(head.method) else { return .init(status: 405, headers: ["Allow": "GET, HEAD"]) }
        let parts = head.path.split(separator: "/", omittingEmptySubsequences: true)
        guard [3, 5].contains(parts.count), parts[0].lowercased() == "s", let id = NearbySharePolicy.guid(String(parts[1])),
              !disposed, endpoint != nil, var share = shares[id] else { return .init(status: 404) }
        let token = Data(parts[2].utf8)
        guard token.count == share.token.count, zip(token, share.token).reduce(UInt8(0), { $0 | ($1.0 ^ $1.1) }) == 0 else { return .init(status: 404) }
        if share.descriptor.expiresAt <= now() { shares[id] = nil; revision &+= 1; scheduleExpiry(); publish(); return .init(status: 410) }
        if !share.receivers.contains(address) {
            guard share.receivers.count < limits.maxNearbyReceivers else { return .init(status: 429) }; share.receivers.insert(address); shares[id] = share
        }
        var headers = ["Cache-Control": "no-store", "Referrer-Policy": "no-referrer", "X-Content-Type-Options": "nosniff", "X-DropSpace-Transport": "unencrypted-private-http"]
        if parts.count == 3 {
            var html = "<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>DropSpace Nearby Share</title><h1>DropSpace Nearby Share</h1><ul>"
            for source in share.sources {
                let name = NearbySharePolicy.html(source.name)
                html += "<li><a download=\"" + name + "\" href=\"/s/" + id.dropLinkN + "/" + String(parts[2]) + "/file/" + source.id.dropLinkN + "\">" + name + "</a> <small>" + String(source.length) + " bytes</small></li>"
            }
            html += "</ul><p><strong>Security notice:</strong> this nearby link uses unencrypted HTTP on the private network. Anyone on that network who obtains the link can read these files. It expires automatically; use Internet Share for encrypted transport.</p>"
            headers["Content-Type"] = "text/html; charset=utf-8"; headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'"
            return .init(status: 200, headers: headers, body: Data(html.utf8))
        }
        guard parts[3].lowercased() == "file", let item = NearbySharePolicy.guid(String(parts[4])), let source = share.sources.first(where: { $0.id == item }) else { return .init(status: 404) }
        let reader: ShareSourceReader
        do { reader = try ShareSourceReader(source) } catch { return .init(status: 409) }
        var start: Int64 = 0, count = source.length, status = 200
        if let range = head.headers["range"], !range.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            guard range.lowercased().hasPrefix("bytes="), let parsed = NearbySharePolicy.range(String(range.dropFirst(6)), total: source.length) else {
                await reader.close(); return .init(status: 416, headers: ["Content-Range": "bytes */" + String(source.length)])
            }
            start = parsed.lowerBound; count = parsed.upperBound - start + 1; status = 206
            headers["Content-Range"] = "bytes \(start)-\(parsed.upperBound)/\(source.length)"
        }
        headers["Content-Type"] = source.mime; headers["Content-Disposition"] = NearbySharePolicy.disposition(source.name)
        return .init(status: status, headers: headers, reader: reader, offset: start, length: count)
    }
}
