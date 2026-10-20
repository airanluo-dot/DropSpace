import Testing
import Foundation
import DropSpaceCore
@testable import DropSpaceInfrastructure

@Test func rangePlanAndValidatorMatchPinnedBoundaries() throws {
    let ranges = try HTTPByteRange.plan(length:10_485_763,connections:64)
    #expect(ranges.count == 5)
    #expect(ranges.first?.from == 0)
    #expect(ranges.last?.to == 10_485_762)
    #expect(ranges.map(\.length).reduce(0,+) == 10_485_763)
    for index in 1..<ranges.count { #expect(ranges[index].from == ranges[index-1].to + 1) }
    let resume = HTTPResumeMetadata(identity:"fixed",etag:"\"v1\"",length:1000)
    #expect(resume.accepts(offset:200,etag:"\"v1\"",contentRange:"bytes 200-999/1000",contentLength:800))
    #expect(!resume.accepts(offset:200,etag:"W/\"v1\"",contentRange:"bytes 200-999/1000",contentLength:800))
    #expect(!resume.accepts(offset:200,etag:"\"v2\"",contentRange:"bytes 200-999/1000",contentLength:800))
    #expect(!resume.accepts(offset:200,etag:"\"v1\"",contentRange:"bytes 201-999/1000",contentLength:799))
}
@Test func sharedPermitCancellationDoesNotLeakOrSteal() async throws {
    let pool = DownloadPermitPool(limit:1)
    let first = try await pool.acquire()
    let waiting = Task { try await pool.acquire() }
    await Task.yield(); waiting.cancel()
    do { _ = try await waiting.value; Issue.record("Cancelled admission succeeded") } catch is CancellationError { }
    #expect(await pool.activeCount == 1)
    await pool.release(first)
    let next = try await pool.acquire(); #expect(await pool.activeCount == 1)
    try await pool.setLimit(2); let another = try await pool.acquire()
    try await pool.setLimit(1); #expect(await pool.activeCount == 2)
    await pool.release(next); await pool.release(another)
    #expect(await pool.activeCount == 0)
}
@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_HTTP"] != nil)) func localDownloadResumeAndHashIntegration() async throws {
    guard let address = ProcessInfo.processInfo.environment["DROPSPACE_TEST_HTTP"] else { return }
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
    defer { try? FileManager.default.removeItem(at:root) }
    let source = URL(string:address + "/bytes")!,file = root.appendingPathComponent("body.part")
    let data = Data((0..<(512*1024)).map { UInt8($0 % 251) })
    try data.prefix(65536).write(to:file)
    let meta = HTTPResumeMetadata(identity:Digests.sha256(Data(source.absoluteString.utf8)),etag:"\"fixture-v1\"",length:Int64(data.count))
    try JSONEncoder().encode(meta).write(to:file.appendingPathExtension("resume.json"))
    let engine = HTTPDownloader(budget:DownloadBudget())
    try await engine.download(source:source,staging:file,expectedBytes:Int64(data.count),sha256:Digests.sha256(data),maximumBytes:Int64(data.count))
    #expect(try Data(contentsOf:file) == data)
    // A stale validator is discarded by a fresh full response.
    try Data([0,1,2]).write(to:file)
    let stale = HTTPResumeMetadata(identity:meta.identity,etag:"\"stale\"",length:Int64(data.count))
    try JSONEncoder().encode(stale).write(to:file.appendingPathExtension("resume.json"))
    try await engine.download(source:source,staging:file,expectedBytes:Int64(data.count),sha256:Digests.sha256(data))
    #expect(try Data(contentsOf:file) == data)
    await #expect(throws:(any Error).self) { try await engine.download(source:URL(string:address+"/bad-range")!,staging:root.appendingPathComponent("bad")) }
    let preserved = root.appendingPathComponent("source.txt"), linked = root.appendingPathComponent("unsafe.part")
    try Data("preserve user bytes".utf8).write(to: preserved)
    try FileManager.default.linkItem(at: preserved, to: linked)
    await #expect(throws: (any Error).self) { try await engine.download(source: source, staging: linked) }
    #expect(try String(contentsOf: preserved, encoding: .utf8) == "preserve user bytes")
    let modulePolicy = HTTPDownloader(budget: DownloadBudget(), requestTimeout: 120, userAgent: "DropSpace-official-module/1 macOS")
    let moduleFile = root.appendingPathComponent("module.part")
    try await modulePolicy.download(source: URL(string: address + "/module-bytes")!, staging: moduleFile,
                                    expectedBytes: Int64(data.count), sha256: Digests.sha256(data), maximumBytes: Int64(data.count))
    #expect(try Data(contentsOf: moduleFile) == data)
}
