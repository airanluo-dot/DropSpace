import AppKit
import Observation
import DropSpaceCore
import DropSpacePlatform

/// App lifetime owns accepted promises. Views only route an actual drop here.
/// NSFilePromiseReceiver has no public producer-cancellation method: a cancelled
/// batch keeps its lease until every advertised callback retires.
@MainActor @Observable final class NativePromisedFiles {
    private final class Reception {
        let id = UUID()
        let receivers: [NSFilePromiseReceiver]
        let settings: AppSettings
        var lease: OwnedImportLease?
        var expected: [Int] = []
        var received: [Int] = []
        var outputs: [[URL]]
        var error: (any Error)?
        var cancelled = false
        var hasUnknownCount = false
        var invalidCount = false
        var task: Task<Void, Never>?
        init(_ receivers: [NSFilePromiseReceiver], settings: AppSettings) {
            self.receivers = receivers; self.settings = settings
            outputs = Array(repeating: [], count: receivers.count)
        }
    }
    @ObservationIgnored private var current: Reception?
    private var prepared: (OwnedImportLease, URL)?
    @ObservationIgnored private var warming: Task<Void, Never>?
    @ObservationIgnored private let queue: OperationQueue = {
        let queue = OperationQueue()
        queue.name = "DropSpace promised-file callbacks"
        queue.maxConcurrentOperationCount = 1; queue.qualityOfService = .utility
        return queue
    }()
    private let importer: PromisedFileImporter
    var onChange: (() -> Void)?
    private(set) var issueKey: String?
    private(set) var issueDetail = ""
    private(set) var result: ItemImportResult?
    private(set) var active = false
    private(set) var received = 0
    private(set) var expected = 0
    private(set) var cancelling = false
    private var stopped = false
    init(importer: PromisedFileImporter) { self.importer = importer }
    var canRetry: Bool { !stopped && current == nil && prepared == nil && warming == nil }
    func activate() async { warm(); await warming?.value }
    func retry() { dismissIssue(); warm() }
    private func warm() {
        guard !stopped, current == nil, prepared == nil, warming == nil else { return }
        warming = Task {
            defer { warming = nil }
            do {
                let ready = try await importer.begin()
                if stopped || Task.isCancelled { await clean(ready.0) }
                else { prepared = ready }
            } catch is CancellationError {}
            catch { report(error) }
        }
    }
    func issueMessage(_ ui: Localizer) -> String? {
        if let issueKey { return ui(issueKey, issueDetail) }
        if let result { return ui("ItemsAddedWithRejected", String(result.items.count), String(result.rejected)) }
        return nil
    }
    func dismissIssue() { issueKey = nil; issueDetail = ""; result = nil }
    private func report(_ error: any Error) {
        issueKey = (error as? PromisedFileError)?.resourceKey ?? "Mac.Promise.failed"
        issueDetail = error is PromisedFileError ? "" : error.localizedDescription
    }

    func receive(_ receivers: [NSFilePromiseReceiver], settings: AppSettings) -> Bool {
        guard !stopped, current == nil, let (lease, directory) = prepared else {
            report(PromisedFileError.busy)
            return false
        }
        guard (1...PromisedFileRules.maximumItems).contains(receivers.count) else {
            report(PromisedFileError.countLimit); return false
        }
        let reception = Reception(receivers, settings: settings)
        reception.lease = lease; prepared = nil
        current = reception; active = true; received = 0; expected = 0; cancelling = false; dismissIssue()
        // This must execute synchronously inside performDragOperation. AppKit
        // throws an NSException if deferred to a Task, even with a retained
        // receiver. Only an empty, durable destination is prepared beforehand.
        let receptionID = reception.id
        for (index, receiver) in receivers.enumerated() {
            receiver.receivePromisedFiles(atDestination: directory, options: [:], operationQueue: queue) { [weak self] url, error in
                Task { @MainActor in
                    self?.completed(receptionID: receptionID, index: index, url: error == nil ? url : nil, error: error)
                }
            }
            let count = receiver.fileNames.count
            reception.expected.append(max(1, count)); reception.received.append(0)
            if count == 0 { reception.hasUnknownCount = true }
        }
        expected = reception.expected.reduce(0, +)
        if expected > PromisedFileRules.maximumItems {
            reception.error = PromisedFileError.countLimit; reception.cancelled = true; cancelling = true
        }
        if reception.hasUnknownCount {
            reception.error = PromisedFileError.providerUnknownCount
            reception.cancelled = true; cancelling = true
        }
        if let error = reception.error { report(error) }
        return true
    }
    private func completed(receptionID: UUID, index: Int, url: URL?, error: (any Error)?) {
        guard let reception = current, reception.id == receptionID, reception.received.indices.contains(index) else { return }
        // Some providers populate names later. This cannot revoke a cancellation,
        // but it can establish that the cancelled producer has finally retired.
        for i in reception.receivers.indices {
            let count = reception.receivers[i].fileNames.count
            if count > 0 { reception.expected[i] = count }
        }
        reception.hasUnknownCount = reception.invalidCount || reception.receivers.contains { $0.fileNames.isEmpty }
        expected = reception.expected.reduce(0, +)
        if expected > PromisedFileRules.maximumItems {
            reception.error = PromisedFileError.countLimit; reception.cancelled = true; cancelling = true
        }
        reception.received[index] += 1; received += 1
        if let error { reception.error = error; reception.cancelled = true; cancelling = true }
        if let url { reception.outputs[index].append(url) }
        if reception.received[index] > reception.expected[index] {
            reception.error = PromisedFileError.extraFiles
            reception.cancelled = true; reception.hasUnknownCount = true; reception.invalidCount = true; cancelling = true
            reception.task?.cancel(); report(PromisedFileError.extraFiles)
        }
        // Unknown legacy counts cannot establish that all producer writes ended.
        // Keep the durable lease; shutdown/recovery exposes cleanup honestly.
        guard !reception.hasUnknownCount,
              zip(reception.received, reception.expected).allSatisfy({ $0 >= $1 }),
              let lease = reception.lease, reception.task == nil else { return }
        reception.task = Task {
            if reception.cancelled || stopped {
                await clean(lease)
                if let error = reception.error { report(error) }
                retire(reception); return
            }
            defer { onChange?() }
            do {
                let imported = try await importer.finish(lease, urls: reception.outputs.flatMap { $0 }, settings: reception.settings)
                if imported.rejected > 0 { result = imported }
            } catch is CancellationError {}
            catch { report(error) }
            retire(reception)
        }
    }
    func cancel() {
        guard let reception = current else { return }
        reception.cancelled = true; cancelling = true
        reception.task?.cancel()
    }
    func shutdown() async -> Bool {
        stopped = true; cancel()
        warming?.cancel()
        if let ready = prepared {
            prepared = nil
            warming = Task { await clean(ready.0); warming = nil }
        }
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while current != nil || warming != nil, ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(25))
        }
        return current == nil && warming == nil
    }
    private func clean(_ lease: OwnedImportLease) async {
        do { try await importer.abandon(lease) }
        catch { issueKey = "Mac.Promise.cleanupPending"; issueDetail = error.localizedDescription }
    }
    private func retire(_ reception: Reception) {
        guard current === reception else { return }
        reception.task = nil; current = nil; active = false; cancelling = false
        warm()
    }
}
