import Foundation
import Network
import Security
import DropSpaceCore

public struct DropLinkHTTPHead: Sendable {
    public let method: String
    public let path: String
    public let headers: [String: String]
}
public struct DropLinkHTTPRequest: Sendable {
    public let head: DropLinkHTTPHead
    public let body: Data
    public let peerID: UUID?
    public let address: String
}
public struct DropLinkHTTPAdmission: Sendable {
    public let maximumBodyBytes: Int
    public let peerID: UUID?
    public let expectedBodyHash: String?
    public let rejection: DropLinkHTTPResponse?
    /// The authentication layer keeps its nonce only after a complete,
    /// matching body, including if the application subsequently rejects it.
    public let finish: @Sendable (Bool) async -> Void
    public init(maximumBodyBytes: Int, peerID: UUID? = nil, expectedBodyHash: String? = nil,
                rejection: DropLinkHTTPResponse? = nil, finish: @escaping @Sendable (Bool) async -> Void = { _ in }) {
        self.maximumBodyBytes = maximumBodyBytes; self.peerID = peerID; self.expectedBodyHash = expectedBodyHash
        self.rejection = rejection; self.finish = finish
    }
}

/// Native HTTP transport. Routing, persisted peer trust, HMAC authentication and
/// user approval remain application services supplied through the two handlers.
public actor DropLinkHTTPServer {
    public typealias Admission = @Sendable (DropLinkHTTPHead, String) async throws -> DropLinkHTTPAdmission
    public typealias Handler = @Sendable (DropLinkHTTPRequest) async throws -> DropLinkHTTPResponse
    private let identity: DropLinkIdentity
    private let admit: Admission
    private let handler: Handler
    private let queue = DispatchQueue(label: "DropSpace.DropLink.Listener")
    private var listener: NWListener?
    private var starting: CheckedContinuation<URL, any Error>?
    private var endpoint: URL?
    private var active: [UUID: (NWConnection, Task<Void, Never>)] = [:]
    private var processing = 0
    private var stopping = false
    private var generation = UUID()
    private var shutdown: Task<Void, Never>?
    public init(identity: DropLinkIdentity, admit: @escaping Admission, handler: @escaping Handler) {
        self.identity = identity; self.admit = admit; self.handler = handler
    }
    public func start(address: String, port: UInt16 = 0) async throws -> URL {
        if let endpoint { return endpoint }
        guard listener == nil, !stopping, !address.isEmpty else { throw DropSpaceError.unavailable("DropLink listener is changing state") }
        let tls = NWProtocolTLS.Options()
        guard let local = sec_identity_create(identity.identity) else { throw DropSpaceError.invalid("DropLink TLS identity") }
        sec_protocol_options_set_local_identity(tls.securityProtocolOptions, local)
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_add_tls_application_protocol(tls.securityProtocolOptions, "http/1.1")
        let parameters = NWParameters(tls: tls, tcp: NWProtocolTCP.Options())
        parameters.requiredLocalEndpoint = .hostPort(host: NWEndpoint.Host(address), port: NWEndpoint.Port(rawValue: port)!)
        let listener = try NWListener(using: parameters), generation = UUID()
        self.listener = listener; self.generation = generation
        listener.stateUpdateHandler = { [weak self] state in Task { await self?.stateChanged(state, listener: listener, address: address) } }
        listener.newConnectionHandler = { [weak self] connection in Task { if let self { await self.accept(connection, generation: generation) } else { connection.cancel() } } }
        do {
            return try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { continuation in starting = continuation; listener.start(queue: queue) }
            } onCancel: { listener.cancel() }
        } catch { listener.cancel(); if self.listener === listener { self.listener = nil }; throw error }
    }
    public func counts() -> (connections: Int, requests: Int) { (active.count, processing) }
    public func stop() async {
        if let shutdown { await shutdown.value; return }
        stopping = true; generation = UUID()
        let task = Task { await stopOwned() }; shutdown = task
        await task.value; shutdown = nil; stopping = false
    }
    private func stopOwned() async { endpoint = nil; listener?.cancel(); listener?.newConnectionHandler = nil
        starting?.resume(throwing: CancellationError()); starting = nil
        let tasks = Array(active.values)
        for (connection, task) in tasks { task.cancel(); connection.cancel() }
        for (_, task) in tasks { await task.value }
        listener?.stateUpdateHandler = nil; listener = nil; active.removeAll()
    }
    private func stateChanged(_ state: NWListener.State, listener current: NWListener, address: String) {
        guard listener === current else { return }
        switch state {
        case .ready:
            guard !stopping, let port = current.port else { return }
            var parts = URLComponents(); parts.scheme = "https"; parts.host = address; parts.port = Int(port.rawValue)
            guard let url = parts.url else { starting?.resume(throwing: DropSpaceError.invalid("DropLink bound address")); starting = nil; current.cancel(); return }
            endpoint = url; starting?.resume(returning: url); starting = nil
        case .failed(let error), .waiting(let error):
            starting?.resume(throwing: error); starting = nil; endpoint = nil; current.cancel()
        case .cancelled:
            starting?.resume(throwing: CancellationError()); starting = nil; endpoint = nil
        default: break
        }
    }
    private func accept(_ connection: NWConnection, generation: UUID) {
        guard !stopping, self.generation == generation, listener != nil, active.count < 32 else { connection.cancel(); return }
        let id = UUID(), address: String
        if case .hostPort(let host, _) = connection.endpoint { address = String(describing: host) } else { connection.cancel(); return }
        connection.start(queue: queue)
        let task = Task { [weak self] in
            guard let self else { connection.cancel(); return }
            await self.serve(connection, id: id, address: address)
        }
        active[id] = (connection, task)
    }
    private func serve(_ connection: NWConnection, id: UUID, address: String) async {
        defer { connection.cancel(); active[id] = nil }
        let reader = DropLinkHTTPReader(connection: connection)
        do {
            let head = try await deadline(seconds: 30, connection: connection) { try await reader.requestHead() }
            guard processing < 8 else { try await Self.write(.init(status: 429, body: Data()), to: connection); return }
            processing += 1; defer { processing -= 1 }
            let admission = try await admit(head, address), ticket = DropLinkAdmissionTicket()
            if let rejection = admission.rejection { await ticket.finish(admission.finish, accepted: false); try await Self.write(rejection, to: connection); return }
            var accepted = false
            do {
                guard (0...69_970_604).contains(admission.maximumBodyBytes) else { throw DropSpaceError.limit("DropLink body policy") }
                if let length = head.headers["content-length"], let count = Int64(length), count > admission.maximumBodyBytes { throw DropSpaceError.limit("DropLink request body") }
                if let expect = head.headers["expect"] {
                    guard expect.lowercased() == "100-continue" else { await ticket.finish(admission.finish, accepted: false); try await Self.write(.init(status: 417, body: Data()), to: connection); return }
                    try await Self.send(Data("HTTP/1.1 100 Continue\r\n\r\n".utf8), to: connection)
                }
                let body = try await deadline(seconds: 600, connection: connection) { try await reader.readBufferedBody(fields: head.headers, maximum: admission.maximumBodyBytes) }
                if let hash = admission.expectedBodyHash, body.sha256 != hash {
                    await ticket.finish(admission.finish, accepted: false); try await Self.write(.init(status: 401, body: Data()), to: connection); return
                }
                accepted = true
                let response = try await deadline(seconds: 600, connection: connection) { [handler] in
                    try await handler(.init(head: head, body: body.data, peerID: admission.peerID, address: address))
                }
                await ticket.finish(admission.finish, accepted: true); try await Self.write(response, to: connection)
            } catch { await ticket.finish(admission.finish, accepted: accepted); throw error }
        } catch is CancellationError { }
        catch let error as DropSpaceError {
            if case .limit = error { try? await Self.write(.init(status: 413, body: Data()), to: connection) }
            else { try? await Self.write(.init(status: 400, body: Data()), to: connection) }
        } catch { /* Connection failure owns no application retry or user file. */ }
    }
    private func deadline<T: Sendable>(seconds: Double, connection: NWConnection, operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { tasks in
            tasks.addTask { try await withTaskCancellationHandler { try await operation() } onCancel: { connection.cancel() } }
            tasks.addTask { try await Task.sleep(for: .seconds(seconds)); throw URLError(.timedOut) }
            defer { tasks.cancelAll() }; return try await tasks.next()!
        }
    }
    private static func write(_ response: DropLinkHTTPResponse, to connection: NWConnection) async throws {
        guard (200...599).contains(response.status), response.body.count <= 16_842_752 else { throw DropSpaceError.limit("DropLink response") }
        let header = "HTTP/1.1 \(response.status) Response\r\nContent-Type: application/json\r\nContent-Length: \(response.body.count)\r\nConnection: close\r\n\r\n"
        try await send(Data(header.utf8), to: connection)
        if !response.body.isEmpty { try await send(response.body, to: connection) }
    }
    private static func send(_ bytes: Data, to connection: NWConnection) async throws {
        try Task.checkCancellation()
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            connection.send(content: bytes, completion: .contentProcessed { error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            })
        }
    }
}

private actor DropLinkAdmissionTicket {
    private var finished = false
    func finish(_ operation: @Sendable (Bool) async -> Void, accepted: Bool) async {
        guard !finished else { return }; finished = true; await operation(accepted)
    }
}
