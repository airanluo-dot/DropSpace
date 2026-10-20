import Foundation
import DropSpaceCore

public protocol ModuleCatalogTransport: Sendable {
    func fetch() async throws -> Data
}
private final class ModuleCatalogRedirectPolicy: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) { completionHandler(nil) }
}
private actor NativeModuleCatalogTransport: ModuleCatalogTransport {
    let budget: DownloadBudget
    let session: URLSession
    init(budget: DownloadBudget) {
        self.budget = budget
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil; configuration.urlCredentialStorage = nil; configuration.urlCache = nil
        configuration.timeoutIntervalForRequest = 15; configuration.timeoutIntervalForResource = 15
        session = URLSession(configuration: configuration, delegate: ModuleCatalogRedirectPolicy(), delegateQueue: nil)
    }
    func fetch() async throws -> Data {
        let permit = try await budget.connections.acquire()
        do {
            try Task.checkCancellation()
            var request = URLRequest(url: ModuleCatalogRules.catalogURL)
            request.setValue("DropSpace-official-module/1 macOS", forHTTPHeaderField: "User-Agent")
            let (stream, response) = try await session.bytes(for: request)
            defer { stream.task.cancel() }
            guard let response = response as? HTTPURLResponse, response.statusCode == 200, response.url == ModuleCatalogRules.catalogURL,
                  response.expectedContentLength <= ModuleCatalogRules.maximumCatalogBytes else { throw DropSpaceError.unavailable("OfficialModuleCatalogUnavailable") }
            var bytes = Data(), charged = 0
            for try await byte in stream {
                bytes.append(byte)
                guard bytes.count <= ModuleCatalogRules.maximumCatalogBytes else { throw DropSpaceError.limit("OfficialModuleCatalogTooLarge") }
                if bytes.count - charged >= 16384 { try await budget.bandwidth.consume(bytes.count - charged); charged = bytes.count }
            }
            try await budget.bandwidth.consume(bytes.count - charged); try Task.checkCancellation()
            await budget.connections.release(permit); return bytes
        } catch { await budget.connections.release(permit); throw error }
    }
}

/// Explicit refresh only. Creating an empty catalog starts no requests, workers,
/// timers or directories; a failed refresh leaves previous descriptors intact.
public actor OfficialModuleCatalog {
    public private(set) var packages: [OfficialModulePackage]
    private let transport: any ModuleCatalogTransport
    private let refreshGate = DownloadPermitPool(limit: 1)
    private var active: Task<[OfficialModulePackage], any Error>?
    private var generation = 0
    private var stopped = false
    public init(budget: DownloadBudget, transport: (any ModuleCatalogTransport)? = nil) throws {
        self.packages = try ModuleCatalogRules.embedded()
        self.transport = transport ?? NativeModuleCatalogTransport(budget: budget)
    }
    public func refresh() async throws {
        guard !stopped else { throw CancellationError() }
        let deadline = ContinuousClock.now.advanced(by: .seconds(15))
        let run = generation, transport = transport
        let permit = try await refreshGate.acquire(deadline: deadline)
        do {
            try Task.checkCancellation(); guard !stopped, generation == run else { throw CancellationError() }
            let task = Task {
                try await withThrowingTaskGroup(of: [OfficialModulePackage].self) { group in
                    group.addTask { try ModuleCatalogRules.parse(await transport.fetch()) }
                    group.addTask { try await Task.sleep(until: deadline, clock: .continuous); throw DropSpaceError.unavailable("OfficialModuleCatalogDeadline") }
                    defer { group.cancelAll() }
                    return try await group.next()!
                }
            }
            active = task
            let candidate = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            try Task.checkCancellation(); guard generation == run else { throw CancellationError() }
            packages = candidate; active = nil
            await refreshGate.release(permit)
        } catch {
            if generation == run { active = nil }
            await refreshGate.release(permit); throw error
        }
    }
    public func stop() async {
        stopped = true; generation += 1; let owned = active; owned?.cancel()
        _ = try? await owned?.value; active = nil
    }
}
