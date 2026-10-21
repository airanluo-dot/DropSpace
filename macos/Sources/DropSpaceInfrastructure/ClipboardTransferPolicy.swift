import Foundation
import DropSpaceCore

public enum ClipboardEnvelopePolicy {
    public static let automaticTextBytes = 256 * 1024, hardTextBytes = 2 * 1024 * 1024
    public static let automaticImageBytes = 10 * 1024 * 1024, hardImageBytes = 50 * 1024 * 1024
    public static func text(origin: UUID, sequence: Int64, text: String, kind: ClipboardPayloadKind = .text, now: Date = Date()) throws -> ClipboardEnvelope {
        let bytes = Data(text.utf8)
        guard !blank(text), [.text, .url].contains(kind), bytes.count <= hardTextBytes else { throw DropSpaceError.invalid("Clipboard transfer text") }
        return .init(origin: origin, sequence: sequence, kind: kind, now: now, hash: Digests.sha256(bytes), bytes: Int64(bytes.count), mime: kind == .url ? "text/uri-list" : "text/plain", text: text)
    }
    public static func image(origin: UUID, sequence: Int64, bytes: Data, mime: String, now: Date = Date()) throws -> ClipboardEnvelope {
        guard !bytes.isEmpty, bytes.count <= hardImageBytes, !blank(mime) else { throw DropSpaceError.invalid("Clipboard transfer image") }
        return .init(origin: origin, sequence: sequence, kind: .image, now: now, hash: Digests.sha256(bytes), bytes: Int64(bytes.count), mime: mime, image: bytes)
    }
    public static func allowedAutomatically(_ value: ClipboardEnvelope, mode: ClipboardSyncMode) -> Bool {
        switch mode {
        case .textAndURL: value.isTextLike && value.byteLength <= automaticTextBytes
        case .textURLAndImage: value.isTextLike && value.byteLength <= automaticTextBytes || value.kind == 3 && value.byteLength <= automaticImageBytes
        default: false
        }
    }
    public static func validate(_ value: ClipboardEnvelope) throws {
        guard value.eventId != .zero, value.originDeviceId != .zero, value.originSequence >= 0, value.byteLength > 0,
              value.sha256.utf16.count == 64, !blank(value.mime), value.mime.utf16.count <= 128,
              DropLinkHandoffJSON.utcDate(value.createdAtUtc) != nil else { throw DropSpaceError.invalid("Clipboard envelope metadata") }
        let bytes: Data
        if value.isTextLike, let text = value.text { bytes = Data(text.utf8) }
        else if value.kind == 3, let image = value.imageBytes { bytes = image }
        else { throw DropSpaceError.invalid("Clipboard envelope payload") }
        guard bytes.count == value.byteLength, bytes.count <= (value.isTextLike ? hardTextBytes : hardImageBytes),
              Digests.sha256(bytes) == value.sha256.lowercased() else { throw DropSpaceError.invalid("Clipboard envelope integrity") }
    }
    private static func blank(_ text: String) -> Bool {
        text.unicodeScalars.allSatisfy { (9...13).contains($0.value) || $0.value == 0x85 || [.spaceSeparator, .lineSeparator, .paragraphSeparator].contains($0.properties.generalCategory) }
    }
}

/// Event identity includes origin, sequence, UUID, hash and bytes. Equal content
/// intentionally copied again is a new event, not an adjacent-capture duplicate.
public actor ClipboardLoopGuard {
    private struct Key: Hashable { let origin: UUID; let sequence: Int64; let event: UUID; let hash: String; let bytes: Int64 }
    private var entries: [Key: Date] = [:]
    private var order: [(Key, Date)] = []
    private let maximum: Int
    private let lifetime: TimeInterval
    public init(maximum: Int = 10000, lifetime: TimeInterval = 86400) throws {
        guard (1...100000).contains(maximum), lifetime > 0, lifetime <= 604800 else { throw DropSpaceError.invalid("Clipboard loop guard limits") }
        self.maximum = maximum; self.lifetime = lifetime
    }
    public func accept(_ envelope: ClipboardEnvelope, now: Date = Date()) -> Bool {
        prune(now); let key = key(envelope)
        if let date = entries[key], now.timeIntervalSince(date) <= lifetime { return false }
        entries[key] = now; order.append((key, now))
        while entries.count > maximum, !order.isEmpty { let (key, date) = order.removeFirst(); if entries[key] == date { entries[key] = nil } }
        return true
    }
    public func remove(_ envelope: ClipboardEnvelope, now: Date = Date()) { prune(now); let key = key(envelope); if entries.removeValue(forKey: key) != nil { order.removeAll { $0.0 == key } } }
    public func clear() { entries.removeAll(); order.removeAll() }
    public func counts() -> (entries: Int, order: Int) { (entries.count, order.count) }
    private func key(_ value: ClipboardEnvelope) -> Key { .init(origin: value.originDeviceId, sequence: value.originSequence, event: value.eventId, hash: value.sha256.lowercased(), bytes: value.byteLength) }
    private func prune(_ now: Date) {
        while let (key, date) = order.first, now.timeIntervalSince(date) > lifetime { order.removeFirst(); if entries[key] == date { entries[key] = nil } }
    }
}

public actor ClipboardPropagationQueue {
    public typealias Propagate = @Sendable (DropItem) async throws -> Void
    private let propagate: Propagate
    private let failure: @Sendable (any Error) async -> Void
    private var pending: [DropItem] = []
    private var worker: Task<Void, Never>?
    private var closed = false
    public init(propagate: @escaping Propagate, failure: @escaping @Sendable (any Error) async -> Void) { self.propagate = propagate; self.failure = failure }
    @discardableResult public func enqueue(_ item: DropItem) -> Bool {
        guard !closed else { return false }
        if pending.count == 16 { pending.removeFirst() }; pending.append(item)
        if worker == nil { worker = Task { await drain() } }; return true
    }
    public func stop() async {
        closed = true; pending.removeAll(); let owned = worker; owned?.cancel(); await owned?.value
    }
    private func drain() async {
        defer { worker = nil }
        while !closed, !pending.isEmpty, !Task.isCancelled {
            let item = pending.removeFirst()
            do { try Task.checkCancellation(); try await propagate(item) }
            catch is CancellationError { if closed || Task.isCancelled { return }; await failure(CancellationError()) }
            catch { await failure(error) }
        }
    }
}
