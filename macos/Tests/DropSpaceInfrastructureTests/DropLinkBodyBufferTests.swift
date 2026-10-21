import Foundation
import Testing
@testable import DropSpaceInfrastructure

@Test func dropLinkRequestBodySpillsAfter64KiBAndUnlinksEveryOutcome() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropLinkSpool-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let chunk = Data(repeating: 0x76, count: 65536)
    let body = DropLinkBodyBuffer(maximum: 131072, directory: root)
    try await body.append(chunk)
    let memory = await body.storage; #expect(memory.inMemory == 65536 && !memory.spooled)
    try await body.append(Data([0xff]))
    let disk = await body.storage; #expect(disk.bytes == 65537 && disk.inMemory == 0 && disk.spooled)
    #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
    let finished = try await body.finish()
    #expect(finished.wasSpooled && finished.data == chunk + Data([0xff]))
    #expect(finished.sha256 == Digests.sha256(finished.data))
    #expect(!(await body.storage.spooled))
    // The immutable mapped data outlives the closed descriptor and unlinked file.
    #expect(finished.data.last == 0xff)
    let oversize = DropLinkBodyBuffer(maximum: 65537, directory: root)
    try await oversize.append(chunk); try await oversize.append(Data([1]))
    await #expect(throws: (any Error).self) { try await oversize.append(Data([2])) }
    await oversize.close(); #expect(!(await oversize.storage.spooled))
    let cancelled = DropLinkBodyBuffer(maximum: 131072, directory: root)
    try await cancelled.append(chunk); try await cancelled.append(Data([1]))
    let task = Task {
        withUnsafeCurrentTask { $0?.cancel() }
        return try await cancelled.finish()
    }
    await #expect(throws: CancellationError.self) { try await task.value }
    await cancelled.close()
    let inaccessible = DropLinkBodyBuffer(maximum: 131072, directory: root.appendingPathComponent("missing"))
    try await inaccessible.append(chunk)
    await #expect(throws: (any Error).self) { try await inaccessible.append(Data([1])) }
    await inaccessible.close()
    #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
}
