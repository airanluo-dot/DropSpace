import AppKit
import Foundation
import DropSpaceCore
import DropSpaceInfrastructure

@MainActor public final class ClipboardCapture {
    private let store: ItemStore
    private let payloads: PayloadStore
    private let board: NSPasteboard
    private let references: FileReferenceService
    private let ownsReferences: Bool
    private let commitGate = DownloadPermitPool(limit: 1)
    private let imageReads = ClipboardImageReadLifetime()
    private var disposed = false
    private var timer: Timer?
    private var task: Task<Void,Never>?
    private var generation = 0
    private var observed = -1
    private var persistedFingerprint: String?
    private var lastObservedFingerprint: String?
    private var selfAuthoredCount: Int?
    private var settings = AppSettings()
    public var onChange: (() -> Void)?
    public var onCaptured: ((DropItem) -> Void)?
    public var isPaused: Bool { settings.clipboardPaused || !settings.privacyChoicesCompleted || disposed }
    public var onError: ((String) -> Void)?
    public private(set) var missedObservations = 0
    public init(store: ItemStore, payloads: PayloadStore, pasteboard: NSPasteboard = .general, references: FileReferenceService? = nil) {
        self.store = store; self.payloads = payloads; board = pasteboard
        self.references = references ?? FileReferenceService(); ownsReferences = references == nil
    }
    public func configure(_ value: AppSettings) async throws {
        let permit = try await commitGate.acquire()
        guard !disposed else { await commitGate.release(permit); throw CancellationError() }
        settings = value
        await commitGate.release(permit)
        if value.clipboardPaused || !value.privacyChoicesCompleted { stop(); return }
        guard timer == nil else { return }
        observed = board.changeCount
        timer = Timer.scheduledTimer(withTimeInterval: 0.2,repeats: true) { [weak self] _ in Task { @MainActor in self?.poll() } }
    }
    public func stop() {
        generation += 1; timer?.invalidate(); timer = nil; task?.cancel()
        persistedFingerprint = nil; lastObservedFingerprint = nil
    }
    public func stopAndDrain() async {
        disposed = true; let pending = task; stop()
        if ownsReferences, !(await references.shutdown()) { onError?("File metadata native IO is still retiring") }
        await pending?.value
        if let permit = try? await commitGate.acquire() { await commitGate.release(permit) }
        await imageReads.drain()
    }
    private func poll() {
        let pasteboard = board
        let count = pasteboard.changeCount
        guard count != observed, task == nil else { return }
        if observed >= 0 && count > observed + 1 { missedObservations += count - observed - 1 }
        observed = count
        if count == selfAuthoredCount { selfAuthoredCount = nil; lastObservedFingerprint = nil; return }
        guard pasteboard.accessBehavior != .alwaysDeny else { onError?("Clipboard access is denied in System Settings"); stop(); return }
        let settings = settings; let run = generation
        task = Task { [weak self] in
            guard let self else { return }
            defer { task = nil }
            var createdPayload: String?
            do {
                let name = board.name
                let snapshot = try await Task.detached(priority: .utility) { try Self.read(name: name, count: count,settings: settings) }.value
                try Task.checkCancellation()
                guard generation == run else { return }
                guard let snapshot else { lastObservedFingerprint = nil; return }
                let duplicate = lastObservedFingerprint == snapshot.fingerprint && persistedFingerprint == snapshot.fingerprint
                lastObservedFingerprint = snapshot.fingerprint
                if duplicate { return }
                persistedFingerprint = nil
                let items: [DropItem]
                switch snapshot.content {
                case .text(let text): items = [try FileIntake.text(text,source: .clipboard,settings: settings,fingerprint: snapshot.fingerprint)]
                case .files(let urls):
                    let result = try await references.prepare(urls,source: .clipboard,settings: settings)
                    if !result.failures.isEmpty { onError?(result.failures.joined(separator: "\n")) }
                    items = result.items
                case .image(let data, let suffix):
                    let path = try await payloads.write(data,extension: suffix); createdPayload = path
                    items = [DropItem(source: .clipboard,kind: .image,title: "Image",payloadPath: path,fingerprint: snapshot.fingerprint)]
                }
                try Task.checkCancellation()
                guard generation == run else { throw CancellationError() }
                if !items.isEmpty {
                    let permit = try await commitGate.acquire()
                    do {
                        try Task.checkCancellation()
                        guard generation == run, !isPaused else { throw CancellationError() }
                        try await store.insert(items); createdPayload = nil
                        persistedFingerprint = snapshot.fingerprint; onChange?()
                        for item in items { onCaptured?(item) }
                        await commitGate.release(permit)
                    } catch { await commitGate.release(permit); throw error }
                    try await store.applyRetention(days: settings.retentionDays,count: settings.retentionItemCount)
                    try await payloads.reconcile(store)
                }
            } catch is CancellationError { }
            catch { if generation == run { onError?(error.localizedDescription) } }
            if let createdPayload { try? await payloads.remove(createdPayload) }
        }
    }
    private struct Snapshot: Sendable {
        enum Content: Sendable { case text(String), files([URL]), image(Data,String) }
        let fingerprint: String; let content: Content
    }
    nonisolated private static func read(name: NSPasteboard.Name, count: Int, settings: AppSettings) throws -> Snapshot? {
        let board = NSPasteboard(name: name)
        guard board.changeCount == count else { return nil }
        let result: Snapshot?
        if settings.captureFiles, let urls = board.readObjects(forClasses: [NSURL.self],options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
            guard urls.count <= settings.maxClipboardFileItems else { throw DropSpaceError.limit("Clipboard file count exceeds limit") }
            result = Snapshot(fingerprint: Digests.sha256(Data(("files\0" + urls.map(\.path).joined(separator: "\0")).utf8)),content: .files(urls))
        } else if settings.captureImages, let type = board.availableType(from: [.png,.tiff]), let bytes = board.data(forType: type) {
            try FileIntake.validateImage(bytes,settings: settings)
            result = Snapshot(fingerprint: Digests.sha256(bytes),content: .image(bytes,type == .png ? "png" : "tiff"))
        } else if let text = board.string(forType: .string), !text.isEmpty {
            guard text.utf16.count <= settings.maxTextCharacters else { throw DropSpaceError.limit("Clipboard text exceeds limit") }
            let normalized = try ContentRules.normalizedBody(text)
            result = Snapshot(fingerprint: Digests.sha256(Data(normalized.utf8)),content: .text(normalized))
        } else { result = nil }
        return board.changeCount == count ? result : nil
    }
    /// Imported events update history/UI but never become locally originated events.
    /// Pause and commit share a gate through the best-effort OS clipboard mutation.
    @discardableResult public func importRemote(_ envelope: ClipboardEnvelope) async throws -> DropItem {
        try ClipboardEnvelopePolicy.validate(envelope)
        let permit = try await commitGate.acquire()
        do {
            try Task.checkCancellation()
            guard !isPaused else { throw ClipboardReceiveError.paused }
            let item: DropItem
            var image: (Data, ClipboardImageInfo)?
            if envelope.isTextLike, let text = envelope.text {
                // The baseline applies the wire 2 MiB limit here, not the local capture character limit.
                let normalized = try ContentRules.normalizedBody(text)
                item = DropItem(source: .clipboard, kind: ItemPolicy.classify(normalized), title: ContentRules.title(normalized), text: normalized, fingerprint: Digests.sha256(Data(normalized.utf8)))
                try await store.insert([item])
            } else if let bytes = envelope.imageBytes {
                let config = settings
                guard bytes.count <= config.maxImageBytes else { throw DropSpaceError.limit("Image encoded byte budget exceeded") }
                let info = try await imageReads.run { try FileIntake.imageInfo(bytes, settings: config) }
                try Task.checkCancellation()
                guard !disposed else { throw CancellationError() }
                let name = try await payloads.write(bytes, extension: info.suffix)
                item = DropItem(source: .clipboard, kind: .image, title: "Image", payloadPath: name, fingerprint: envelope.sha256.lowercased())
                do { try Task.checkCancellation(); try await store.insert([item]) }
                catch { try? await payloads.remove(name); throw error }
                image = (bytes, info)
            } else { throw DropSpaceError.invalid("Clipboard payload") }
            // No throws after durable commit: a clipboard failure must not turn the
            // same successfully stored envelope into a retryable duplicate import.
            onChange?()
            if !disposed {
                board.clearContents()
                let written: Bool
                if let (bytes, info) = image { written = board.setData(bytes, forType: .init(info.typeIdentifier)) }
                else { written = board.setString(item.text ?? "", forType: .string) }
                selfAuthoredCount = board.changeCount
                if !written { onError?("Remote clipboard content was saved, but the system clipboard could not be updated") }
            }
            await commitGate.release(permit); return item
        } catch { await commitGate.release(permit); throw error }
    }
    public func copy(_ items: [DropItem], resolvedFileURLs: [URL]? = nil) async throws {
        let writers: [NSPasteboardWriting]
        if items.allSatisfy({ $0.file != nil }) {
            if let resolvedFileURLs {
                guard resolvedFileURLs.count == items.count else { throw DropSpaceError.invalid("File selection changed") }
                writers = resolvedFileURLs.map { $0 as NSURL }
            } else {
                var urls: [NSURL] = []
                for item in items {
                    let availability = try await references.check(item.file!, kind: item.kind)
                    guard availability.status == .available, let url = availability.resolvedURL else { throw availability.reason ?? .storageUnavailable }
                    urls.append(url as NSURL)
                }
                writers = urls
            }
        }
        else if items.count == 1, let path = items.first?.payloadPath {
            let url = try await payloads.url(path)
            guard let image = NSImage(contentsOf: url) else { throw DropSpaceError.unavailable("Image payload is unavailable") }
            writers = [image]
        } else { writers = [items.compactMap(\.text).joined(separator: "\n") as NSString] }
        guard !items.isEmpty else { return }
        board.clearContents()
        guard board.writeObjects(writers) else { throw DropSpaceError.unavailable("Clipboard write failed") }
        selfAuthoredCount = board.changeCount
    }
}
