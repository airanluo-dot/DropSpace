import Foundation
import Network
import DropSpaceCore

struct NearbyHTTPResponse: Sendable {
    var status: Int
    var headers: [String: String] = [:]
    var body = Data()
    var reader: ShareSourceReader?
    var offset: Int64 = 0, length: Int64 = 0
}
/// A separate plaintext listener for browser sharing. It never exposes DropLink
/// routes, trust credentials or the application's general data directory.
actor NearbyShareHTTPServer {
    typealias Handler = @Sendable (DropLinkHTTPHead, String) async throws -> NearbyHTTPResponse
    private let handler: Handler
    private let onInvalidated: @Sendable () async -> Void
    private let queue = DispatchQueue(label: "DropSpace.NearbyShare.Listener")
    private var listener: NWListener?
    private var starting: CheckedContinuation<URL, any Error>?
    private var endpoint: URL?
    private var active: [UUID: (NWConnection, Task<Void, Never>)] = [:]
    private var generation = UUID()
    private var shutdown: Task<Void, Never>?
    init(handler: @escaping Handler, onInvalidated: @escaping @Sendable () async -> Void = {}) { self.handler = handler; self.onInvalidated = onInvalidated }
    func start(address: String) async throws -> URL {
        guard shutdown == nil else { throw DropSpaceError.unavailable("Nearby listener is retiring") }
        if let endpoint { return endpoint }
        guard listener == nil else { throw DropSpaceError.unavailable("Nearby listener is starting") }
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: NWEndpoint.Host(address), port: .any)
        let listener = try NWListener(using: parameters), generation = UUID()
        self.listener = listener; self.generation = generation
        listener.stateUpdateHandler = { [weak self] state in Task { await self?.state(state, listener: listener, address: address) } }
        listener.newConnectionHandler = { [weak self] connection in Task { if let self { await self.accept(connection, generation: generation) } else { connection.cancel() } } }
        do {
            return try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { starting = $0; listener.start(queue: queue) }
            } onCancel: { listener.cancel() }
        } catch { listener.cancel(); if self.listener === listener { self.listener = nil }; throw error }
    }
    private func state(_ state: NWListener.State, listener current: NWListener, address: String) {
        guard listener === current else { return }
        switch state {
        case .ready:
            guard shutdown == nil, let port = current.port else { return }
            var parts = URLComponents(); parts.scheme = "http"; parts.host = address; parts.port = Int(port.rawValue); parts.path = "/"
            guard let url = parts.url else { current.cancel(); return }
            endpoint = url; starting?.resume(returning: url); starting = nil
        case .failed(let error), .waiting(let error):
            invalidatePublishedEndpoint(); starting?.resume(throwing: error); starting = nil; current.cancel()
        case .cancelled: invalidatePublishedEndpoint(); starting?.resume(throwing: CancellationError()); starting = nil
        default: break
        }
    }
    private func invalidatePublishedEndpoint() {
        let notify = endpoint != nil && shutdown == nil; endpoint = nil
        if notify { Task { await onInvalidated() } }
    }
    private func accept(_ connection: NWConnection, generation: UUID) {
        guard shutdown == nil, self.generation == generation, listener != nil, active.count < 32,
              case .hostPort(let host, _) = connection.endpoint else { connection.cancel(); return }
        let id = UUID(), address = String(describing: host); connection.start(queue: queue)
        active[id] = (connection, Task { await serve(connection, id: id, address: address) })
    }
    private func serve(_ connection: NWConnection, id: UUID, address: String) async {
        defer { connection.cancel(); active[id] = nil }
        do {
            let head = try await deadline(seconds: 30, connection: connection) { try await DropLinkHTTPReader(connection: connection).requestHead() }
            guard head.headers["transfer-encoding"] == nil, head.headers["content-length"].map({ $0 == "0" }) ?? true else {
                try await Self.write(.init(status: 400), to: connection, headOnly: false); return
            }
            try await deadline(seconds: 1800, connection: connection) { [handler] in
                let response = try await handler(head, address)
                do { try await Self.write(response, to: connection, headOnly: head.method == "HEAD"); await response.reader?.close() }
                catch { await response.reader?.close(); throw error }
            }
        } catch { /* A failed stream is aborted, never replaced with a success. */ }
    }
    private static func write(_ response: NearbyHTTPResponse, to connection: NWConnection, headOnly: Bool) async throws {
        let count = response.reader == nil ? Int64(response.body.count) : response.length
        var header = "HTTP/1.1 \(response.status) Response\r\nContent-Length: \(count)\r\nConnection: close\r\n"
        for (key, value) in response.headers.sorted(by: { $0.key < $1.key }) {
            guard !key.contains("\r"), !key.contains("\n"), !value.contains("\r"), !value.contains("\n") else { throw DropSpaceError.invalid("Nearby response header") }
            header += key + ": " + value + "\r\n"
        }
        try await send(Data((header + "\r\n").utf8), to: connection)
        guard !headOnly else { return }
        if let reader = response.reader {
            var offset = response.offset, remaining = response.length
            while remaining > 0 {
                try Task.checkCancellation()
                let bytes = try await reader.read(offset: offset, count: Int(min(81920, remaining)))
                try await send(bytes, to: connection); offset += Int64(bytes.count); remaining -= Int64(bytes.count)
            }
        } else if !response.body.isEmpty { try await send(response.body, to: connection) }
    }
    private static func send(_ bytes: Data, to connection: NWConnection) async throws {
        try Task.checkCancellation()
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            connection.send(content: bytes, completion: .contentProcessed { error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            })
        }
    }
    private func deadline<T: Sendable>(seconds: Double, connection: NWConnection, operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { tasks in
            tasks.addTask { try await withTaskCancellationHandler { try await operation() } onCancel: { connection.cancel() } }
            tasks.addTask { try await Task.sleep(for: .seconds(seconds)); throw URLError(.timedOut) }
            defer { tasks.cancelAll() }; return try await tasks.next()!
        }
    }
    func stop() async -> Bool {
        if shutdown == nil {
            generation = UUID(); endpoint = nil; listener?.cancel()
            starting?.resume(throwing: CancellationError()); starting = nil
            let tasks = Array(active.values)
            for (connection, task) in tasks { task.cancel(); connection.cancel() }
            shutdown = Task { await finishStop(tasks) }
        }
        return await NativeRetirementWait.wait(shutdown!, seconds: 10)
    }
    private func finishStop(_ tasks: [(NWConnection, Task<Void, Never>)]) async {
        for (_, task) in tasks { await task.value }
        listener?.newConnectionHandler = nil; listener?.stateUpdateHandler = nil; listener = nil
        active.removeAll(); shutdown = nil
    }
}

/// A deadline returns to the UI without discarding the task that owns native IO.
/// Its service retains that task and refuses another listener until it retires.
actor NativeRetirementWait {
    private var continuation: CheckedContinuation<Bool, Never>?
    private var timer: Task<Void, Never>?
    static func wait(_ owner: Task<Void, Never>, seconds: Double) async -> Bool {
        let ticket = NativeRetirementWait(); return await ticket.start(owner, seconds: seconds)
    }
    private func start(_ owner: Task<Void, Never>, seconds: Double) async -> Bool {
        await withCheckedContinuation { continuation in
            self.continuation = continuation
            Task { await owner.value; finish(true) }
            timer = Task { do { try await Task.sleep(for: .seconds(seconds)); finish(false) } catch {} }
        }
    }
    private func finish(_ retired: Bool) { guard let continuation else { return }; self.continuation = nil; timer?.cancel(); timer = nil; continuation.resume(returning: retired) }
}
