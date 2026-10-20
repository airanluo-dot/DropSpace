import Foundation
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

private actor CatalogFixture: ModuleCatalogTransport {
    var payload: Data
    var delay: Duration = .zero
    var requests = 0
    init(_ payload: Data) { self.payload = payload }
    func configure(_ payload: Data, delay: Duration = .zero) { self.payload = payload; self.delay = delay }
    func fetch() async throws -> Data { requests += 1; try await Task.sleep(for: delay); return payload }
}
private func catalogBytes(_ id: String = "dropspace.example", windows: Bool = false) throws -> Data {
    try ModuleJSON.object([.init("schemaVersion", .number("1")), .init("packages", .array([
        .object([.init("id", .string(id)), .init("version", .string("1.2.3")),
                 .init("url", .string(windows ? "https://github.com/airanluo-dot/DropSpace/releases/download/dlc-\(id)-1.2.3/\(id)-1.2.3-win-x64.zip" : ModuleCatalogRules.packageURL(id: id, version: "1.2.3"))),
                 .init("bytes", .number("123")), .init("sha256", .string(String(repeating: "a", count: 64)))])]))]).encoded()
}

@Test func officialMacModuleCatalogIsIsolatedLazyAndRetainsLastValidRefresh() async throws {
    let bytes = try catalogBytes(), transport = CatalogFixture(bytes)
    let catalog = try OfficialModuleCatalog(budget: DownloadBudget(), transport: transport)
    #expect(await catalog.packages.isEmpty)
    #expect(await transport.requests == 0)
    try await catalog.refresh()
    #expect(await catalog.packages.first?.id == "dropspace.example")
    await transport.configure(Data(#"{"schemaVersion":1,"packages":[],"unexpected":true}"#.utf8))
    await #expect(throws: (any Error).self) { try await catalog.refresh() }
    #expect(await catalog.packages.first?.id == "dropspace.example")
    await transport.configure(try catalogBytes(windows: true))
    await #expect(throws: (any Error).self) { try await catalog.refresh() }
    #expect(await catalog.packages.first?.id == "dropspace.example")
    #expect(try ModuleCatalogRules.parse(catalogBytes("dropspace.sample")).isEmpty)
    await catalog.stop()
}

@Test func officialMacModuleCatalogStopRetiresActiveAndQueuedRefresh() async throws {
    let bytes = try catalogBytes(), transport = CatalogFixture(bytes)
    await transport.configure(bytes, delay: .seconds(60))
    let catalog = try OfficialModuleCatalog(budget: DownloadBudget(), transport: transport)
    let active = Task { try await catalog.refresh() }
    let deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while await transport.requests == 0, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(5)) }
    #expect(await transport.requests == 1)
    let queued = Task { try await catalog.refresh() }
    try await Task.sleep(for: .milliseconds(20)); await catalog.stop()
    await #expect(throws: CancellationError.self) { try await active.value }
    await #expect(throws: CancellationError.self) { try await queued.value }
    await #expect(throws: CancellationError.self) { try await catalog.refresh() }
    #expect(await transport.requests == 1)
    #expect(await catalog.packages.isEmpty)
}

@Test func downloadPermitDeadlineCannotTakeAnotherOwnersSlot() async throws {
    let pool = DownloadPermitPool(limit: 1), owner = try await pool.acquire()
    let start = ContinuousClock.now
    await #expect(throws: URLError.self) { _ = try await pool.acquire(deadline: .now.advanced(by: .milliseconds(100))) }
    #expect(start.duration(to: .now) >= .milliseconds(100))
    #expect(await pool.activeCount == 1)
    await pool.release(owner)
    let next = try await pool.acquire(deadline: .now.advanced(by: .milliseconds(100)))
    #expect(await pool.activeCount == 1); await pool.release(next)
    #expect(await pool.activeCount == 0)
}
