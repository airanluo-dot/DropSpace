import AppKit
import Foundation
import Testing
import UniformTypeIdentifiers

/// Verifies advertisement only on a private pasteboard. Actual reception must
/// run inside a native drop callback: invoking it here raises an NSException.
/// The failed acceptance attempt is retained in the execution ledger.
@Test @MainActor func nativePromiseReceiverAdvertisesWithoutMaterializing() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let board = NSPasteboard(name: .init("DropSpace.Validation.Promise." + UUID().uuidString))
    defer { board.clearContents() }
    let first = PromiseSource(name: "first.txt", bytes: Data("native promise".utf8))
    let second = PromiseSource(name: "empty.txt", bytes: Data())
    let providers = [NSFilePromiseProvider(fileType: UTType.plainText.identifier, delegate: first),
                     NSFilePromiseProvider(fileType: UTType.plainText.identifier, delegate: second)]
    #expect(board.writeObjects(providers))
    let receivers = try #require(board.readObjects(forClasses: [NSFilePromiseReceiver.self]) as? [NSFilePromiseReceiver])
    #expect(receivers.count == 2)
    #expect(receivers.allSatisfy { $0.fileNames.isEmpty })
    #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
    withExtendedLifetime((providers, first, second, receivers)) {}
}

private final class PromiseSource: NSObject, NSFilePromiseProviderDelegate, @unchecked Sendable {
    let name: String, bytes: Data
    let queue: OperationQueue = { let q = OperationQueue(); q.maxConcurrentOperationCount = 1; return q }()
    init(name: String, bytes: Data) { self.name = name; self.bytes = bytes }
    func filePromiseProvider(_ filePromiseProvider: NSFilePromiseProvider, fileNameForType fileType: String) -> String { name }
    func operationQueue(for filePromiseProvider: NSFilePromiseProvider) -> OperationQueue { queue }
    nonisolated func filePromiseProvider(_ filePromiseProvider: NSFilePromiseProvider, writePromiseTo url: URL,
                                        completionHandler: @escaping ((any Error)?) -> Void) {
        do { try bytes.write(to: url, options: .withoutOverwriting); completionHandler(nil) }
        catch { completionHandler(error) }
    }
}
