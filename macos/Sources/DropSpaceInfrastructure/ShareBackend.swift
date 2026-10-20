import Foundation
import Network
import Security
import Synchronization
import DropSpaceCore

public struct ShareBackendSession: Codable, Sendable {
    public let uploadBaseUrl: URL, downloadBaseUrl: URL, uploadAuthorization: String, revokeUrl: URL
    public init(upload: URL, download: URL, authorization: String, revoke: URL) {
        uploadBaseUrl = upload; downloadBaseUrl = download; uploadAuthorization = authorization; revokeUrl = revoke
    }
    public func validate(origin: URL? = nil) throws {
        for url in [uploadBaseUrl, downloadBaseUrl, revokeUrl] {
            guard Self.safe(url), url.absoluteString.utf16.count <= 2048 else { throw DropSpaceError.invalid("Unsafe secure share endpoint") }
            if let origin, !Self.sameOrigin(url, origin) { throw DropSpaceError.invalid("Secure share endpoint changed origin") }
        }
        guard uploadAuthorization.hasPrefix("Bearer "), uploadAuthorization.utf16.count > 7, uploadAuthorization.utf16.count <= 4096,
              !uploadAuthorization.contains("\r"), !uploadAuthorization.contains("\n") else { throw DropSpaceError.invalid("Unsafe secure share authorization") }
    }
    public static func safe(_ url: URL) -> Bool {
        url.scheme?.lowercased() == "https" && url.host?.isEmpty == false && url.user == nil && url.password == nil && url.query == nil && url.fragment == nil && (1...65535).contains(url.port ?? 443)
    }
    public static func sameOrigin(_ first: URL, _ second: URL) -> Bool {
        first.scheme?.lowercased() == second.scheme?.lowercased() && first.host?.lowercased() == second.host?.lowercased() && (first.port ?? 443) == (second.port ?? 443)
    }
}
public protocol ShareBackendClient: Sendable {
    func create(id: UUID, expires: Date, count: Int, bytes: Int64) async throws -> ShareBackendSession
    func upload(session: ShareBackendSession, name: String, ciphertext: Data) async throws
    func revoke(session: ShareBackendSession, id: UUID) async throws
}
public struct CloudflareShareBackend: ShareBackendClient {
    private let endpoint: URL
    private let http: ShareHTTPSClient
    public init(endpoint: URL, budget: DownloadBudget) throws {
        guard ShareBackendSession.safe(endpoint) else { throw DropSpaceError.invalid("Secure share requires an HTTPS backend URL") }
        self.endpoint = endpoint; http = ShareHTTPSClient(budget: budget)
    }
    public func create(id: UUID, expires: Date, count: Int, bytes: Int64) async throws -> ShareBackendSession {
        guard id != .zero else { throw DropSpaceError.invalid("Share identity") }
        let body: ModuleJSON = .object([.init("shareId", .string(id.dropLinkD)), .init("expiresAtUtc", .string(DropLinkHandoffJSON.timestamp(expires))), .init("itemCount", .number(String(count))), .init("totalBytes", .number(String(bytes)))])
        let response = try await http.send(url: endpointURL("/v1/shares"), method: "POST", body: body.encoded(), authorization: nil, contentType: "application/json")
        guard (200...299).contains(response.status) else { throw DropSpaceError.unavailable("Secure share create HTTP \(response.status)") }
        let session = try JSONDecoder().decode(ShareBackendSession.self, from: response.body)
        try session.validate(origin: endpoint); return session
    }
    public func upload(session: ShareBackendSession, name: String, ciphertext: Data) async throws {
        try session.validate(origin: endpoint)
        guard !name.isEmpty, name.utf16.count <= 180, name != ".", name != "..",
              name.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || [45, 46, 95].contains($0) }),
              ciphertext.count <= ShareLimits.maxObjectBytes, name != "manifest.bin" || ciphertext.count <= ShareLimits.maxManifestBytes,
              let url = URL(string: name, relativeTo: session.uploadBaseUrl)?.absoluteURL else { throw DropSpaceError.invalid("Secure share upload object") }
        let response = try await http.send(url: url, method: "PUT", body: ciphertext, authorization: session.uploadAuthorization, contentType: "application/octet-stream")
        guard (200...299).contains(response.status) else { throw DropSpaceError.unavailable("Secure share upload HTTP \(response.status)") }
    }
    public func revoke(session: ShareBackendSession, id: UUID) async throws {
        try session.validate(origin: endpoint); guard id != .zero else { throw DropSpaceError.invalid("Share identity") }
        let response = try await http.send(url: endpointURL("/v1/shares/" + id.dropLinkN), method: "DELETE", body: Data(), authorization: session.uploadAuthorization, contentType: "application/json", timeout: 120)
        guard (200...299).contains(response.status) || [404, 410].contains(response.status) else { throw DropSpaceError.unavailable("Secure share revoke HTTP \(response.status)") }
    }
    private func endpointURL(_ path: String) -> URL {
        var value = URLComponents(url: endpoint, resolvingAgainstBaseURL: true)!; value.path = path; return value.url!
    }
}

/// Web PKI HTTPS, deliberately separate from DropLink's peer-certificate pins.
/// One native send at a time consumes the shared bandwidth pool in64KiB pieces;
/// body data is a single bounded encrypted object, never an entire source file.
struct ShareHTTPSClient: Sendable {
    let budget: DownloadBudget
    func send(url: URL, method: String, body: Data, authorization: String?, contentType: String, timeout: Double = 1800) async throws -> DropLinkHTTPResponse {
        guard ShareBackendSession.safe(url), ["POST", "PUT", "DELETE", "GET"].contains(method), body.count <= ShareLimits.maxObjectBytes,
              let parts = URLComponents(url: url, resolvingAgainstBaseURL: true), let rawHost = url.host else { throw DropSpaceError.invalid("Secure share HTTPS request") }
        let path = parts.percentEncodedPath.isEmpty ? "/" : parts.percentEncodedPath
        guard path.utf8.allSatisfy({ (33...126).contains($0) }), contentType.utf8.allSatisfy({ (32...126).contains($0) }),
              authorization?.utf8.allSatisfy({ (32...126).contains($0) }) ?? true else { throw DropSpaceError.invalid("Secure share HTTP headers") }
        let host = rawHost.hasPrefix("[") ? String(rawHost.dropFirst().dropLast()) : rawHost
        let port = UInt16(url.port ?? 443), authority = (host.contains(":") ? "[\(host)]" : host) + ":\(port)"
        var headers = "\(method) \(path) HTTP/1.1\r\nHost: \(authority)\r\nConnection: close\r\nAccept-Encoding: identity\r\nContent-Type: \(contentType)\r\nContent-Length: \(body.count)\r\n"
        if let authorization { headers += "Authorization: " + authorization + "\r\n" }
        let header = Data((headers + "\r\n").utf8)
        guard header.count <= 32768 else { throw DropSpaceError.limit("Secure share request headers") }
        let permit = try await budget.connections.acquire(deadline: .now.advanced(by: .seconds(timeout)))
        do {
            let result = try await perform(host: host, port: port, header: header, body: body, timeout: timeout)
            await budget.connections.release(permit); return result
        } catch { await budget.connections.release(permit); throw error }
    }
    private func perform(host: String, port: UInt16, header: Data, body: Data, timeout: Double) async throws -> DropLinkHTTPResponse {
        let tls = NWProtocolTLS.Options(), queue = DispatchQueue(label: "DropSpace.InternetShare.HTTPS")
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_add_tls_application_protocol(tls.securityProtocolOptions, "http/1.1")
        host.withCString { sec_protocol_options_set_tls_server_name(tls.securityProtocolOptions, $0) }
        // Preserve system trust anchors and hostname validation. Never reuse
        // the paired-device trust exception for a website or sharing backend.
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, peer, complete in
            let trust = sec_trust_copy_ref(peer).takeRetainedValue()
            guard SecTrustSetPolicies(trust, SecPolicyCreateSSL(true, host as CFString)) == errSecSuccess else { complete(false); return }
            complete(SecTrustEvaluateWithError(trust, nil))
        }, queue)
        let connection = NWConnection(host: .init(host), port: .init(rawValue: port)!, using: .init(tls: tls, tcp: .init()))
        defer { connection.cancel() }
        return try await withThrowingTaskGroup(of: DropLinkHTTPResponse.self) { tasks in
            tasks.addTask {
                try await withTaskCancellationHandler {
                    try Task.checkCancellation(); try await ready(connection, queue: queue); try await write(header, connection: connection)
                    var offset = 0
                    while offset < body.count {
                        let count = min(65536, body.count - offset)
                        try await budget.bandwidth.consume(count)
                        try await write(body.subdata(in: offset..<(offset + count)), connection: connection); offset += count
                    }
                    let response = try await DropLinkHTTPReader(connection: connection).response(maximum: 65536)
                    try await budget.bandwidth.consume(response.body.count)
                    return response
                } onCancel: { connection.cancel() }
            }
            tasks.addTask { try await Task.sleep(for: .seconds(timeout)); throw URLError(.timedOut) }
            defer { tasks.cancelAll() }; return try await tasks.next()!
        }
    }
    private func ready(_ connection: NWConnection, queue: DispatchQueue) async throws {
        let pending = Mutex<CheckedContinuation<Void, any Error>?>(nil)
        try await withCheckedThrowingContinuation { continuation in
            pending.withLock { $0 = continuation }
            connection.stateUpdateHandler = { state in
                let result: Result<Void, any Error>
                switch state {
                case .ready: result = .success(())
                case .failed(let error), .waiting(let error): result = .failure(error)
                case .cancelled: result = .failure(CancellationError())
                default: return
                }
                pending.withLock { value in let old = value; value = nil; return old }?.resume(with: result)
            }
            connection.start(queue: queue)
        }
    }
    private func write(_ bytes: Data, connection: NWConnection) async throws {
        try Task.checkCancellation()
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            connection.send(content: bytes, completion: .contentProcessed { error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            })
        }
    }
}
