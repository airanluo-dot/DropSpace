import Foundation
import DropSpaceCore
import DropSpaceInfrastructure

public actor PromisedFileImporter {
    private let store: ItemStore
    private let payloads: PayloadStore
    private let references: FileReferenceService
    public init(store: ItemStore, payloads: PayloadStore, references: FileReferenceService) {
        self.store = store; self.payloads = payloads; self.references = references
    }
    public func begin() async throws -> (OwnedImportLease, URL) {
        let lease = OwnedImportLease()
        try await store.beginOwnedImport(lease)
        do {
            let url = try await payloads.createPromiseStaging(lease)
            try Task.checkCancellation()
            return (lease, url)
        } catch {
            if (error as? POSIXError)?.code == .EEXIST {
                // mkdir did not create this path, so the lease never owned it.
                try? await store.discardUncreatedOwnedImport(lease.id)
            } else { try? await abandon(lease) }
            throw error
        }
    }
    public func finish(_ lease: OwnedImportLease, urls: [URL], settings: AppSettings,
                       onAccepted: (@Sendable (Int) async -> Void)? = nil) async throws -> ItemImportResult {
        do {
            let entries = try await payloads.inspectPromises(urls, lease: lease)
            var items: [DropItem] = [], rejected = 0
            for entry in entries {
                try Task.checkCancellation()
                do {
                    let suffix = entry.url.pathExtension.filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
                    let name = UUID().uuidString + (suffix.isEmpty ? "" : "." + String(suffix.prefix(12)))
                    try await store.reserveOwnedImport(lease.id, name: name)
                    let output = try await payloads.adoptPromise(entry, lease: lease, name: name)
                    let result = try await references.prepare([output], source: .space, settings: settings)
                    guard var item = result.items.first, item.status == .available else {
                        throw PromisedFileError.invalidResult
                    }
                    item.title = entry.title; item.payloadPath = name
                    item.batchID = lease.id; item.batchIndex = items.count; item.batchCount = entries.count
                    item.batchAcquisition = "file-promise"
                    try Task.checkCancellation()
                    try await store.commitOwnedImportItem(lease.id, item: item)
                    items.append(item)
                    await onAccepted?(items.count)
                } catch is CancellationError { throw CancellationError() }
                catch {
                    // Mirrors StagedFileImportService: retain already committed
                    // files, reject this item and continue with the same batch.
                    rejected += 1
                }
            }
            // Only uncommitted reservations remain in the lease. Cleanup failure
            // cannot turn previously committed files into a false rollback.
            try? await abandon(lease)
            return .init(items: items, rejected: rejected)
        } catch {
            try? await abandon(lease)
            throw error
        }
    }
    public func abandon(_ lease: OwnedImportLease) async throws {
        try await store.abandonOwnedImport(lease.id)
        try await payloads.reconcile(store)
    }
}
