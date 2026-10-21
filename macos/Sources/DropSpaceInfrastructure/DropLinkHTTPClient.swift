import Foundation
import Network
import Security
import Synchronization
import DropSpaceCore

public struct DropLinkHTTPResponse: Sendable {
    public let status: Int
    public let body: Data
    public init(status: Int, body: Data = Data()) { self.status = status; self.body = body }
}

/// DropLink's pinned self-signed device certificates are not public Web PKI
/// certificates. Network's per-connection verify callback preserves that v1
/// trust model without changing system anchors or general HTTPS policy.
public final class DropLinkHTTPClient: Sendable {
    private let host: String
    private let port: UInt16
    private let fingerprint: String
    public init(endpoint: URL, fingerprint: String) throws {
        let normalized = fingerprint.replacingOccurrences(of: ":", with: "").lowercased()
        guard endpoint.scheme == "https", let host = endpoint.host, !host.isEmpty, endpoint.user == nil, endpoint.password == nil,
              endpoint.query == nil, endpoint.fragment == nil, normalized.utf8.count == 64,
              normalized.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }),
              let port = UInt16(exactly: endpoint.port ?? 443), port > 0 else { throw DropSpaceError.invalid("DropLink HTTPS origin or fingerprint") }
        self.host = host.hasPrefix("[") && host.hasSuffix("]") ? String(host.dropFirst().dropLast()) : host
        self.port = port; self.fingerprint = normalized
    }
    public func send(method: String, path: String, headers: [String: String] = [:], body: Data = Data(),
                     maximumResponseBytes: Int = 65536) async throws -> DropLinkHTTPResponse {
        guard ["GET", "POST", "PUT"].contains(method), path.hasPrefix("/v1/"), path.utf8.allSatisfy({ (33...126).contains($0) }),
              !path.contains("?"), !path.contains("#"), (1...16_842_752).contains(maximumResponseBytes),
              body.count <= (DropLinkRoute.isPairing(path) ? 65536 : DropLinkRoute.bodyLimit(path)) else { throw DropSpaceError.invalid("DropLink request") }
        let authority = host.contains(":") ? "[\(host)]:\(port)" : "\(host):\(port)"
        var lines = ["\(method) \(path) HTTP/1.1", "Host: \(authority)", "Connection: close", "Accept-Encoding: identity", "Content-Type: application/json", "Content-Length: \(body.count)"]
        var names = Set<String>()
        for (key, value) in headers {
            let name = key.lowercased()
            guard name.hasPrefix("x-droplink-"), key.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || $0 == 45 }),
                  value.utf8.allSatisfy({ (32...126).contains($0) }), names.insert(name).inserted else { throw DropSpaceError.invalid("DropLink header") }
            lines.append(key + ": " + value)
        }
        let header = Data((lines.joined(separator: "\r\n") + "\r\n\r\n").utf8)
        guard header.count <= 32768 else { throw DropSpaceError.limit("DropLink headers") }
        let tls = NWProtocolTLS.Options(), queue = DispatchQueue(label: "DropSpace.DropLink.TLS")
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_add_tls_application_protocol(tls.securityProtocolOptions, "http/1.1")
        let fingerprint = fingerprint
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, peer, complete in
            let trust = sec_trust_copy_ref(peer).takeRetainedValue()
            guard let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate], let leaf = chain.first else { complete(false); return }
            complete(Digests.sha256(SecCertificateCopyData(leaf) as Data) == fingerprint)
        }, queue)
        let connection = NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: port)!, using: NWParameters(tls: tls, tcp: NWProtocolTCP.Options()))
        defer { connection.cancel() }
        return try await withThrowingTaskGroup(of: DropLinkHTTPResponse.self) { tasks in
            tasks.addTask {
                try await withTaskCancellationHandler {
                    try Task.checkCancellation(); try await Self.ready(connection, queue: queue)
                    try await Self.write(header, connection: connection)
                    if !body.isEmpty { try await Self.write(body, connection: connection) }
                    let reader = DropLinkHTTPReader(connection: connection)
                    return try await reader.response(maximum: maximumResponseBytes)
                } onCancel: { connection.cancel() }
            }
            tasks.addTask { try await Task.sleep(for: .seconds(600)); throw URLError(.timedOut) }
            defer { tasks.cancelAll() }
            return try await tasks.next()!
        }
    }
    private static func ready(_ connection: NWConnection, queue: DispatchQueue) async throws {
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
                let waiting = pending.withLock { value in let copy = value; value = nil; return copy }
                waiting?.resume(with: result)
            }
            connection.start(queue: queue)
        }
    }
    private static func write(_ data: Data, connection: NWConnection) async throws {
        try Task.checkCancellation()
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            })
        }
    }
}

/// Incremental HTTP/1 framing. Both Windows chunked JSON and explicit-length
/// bodies are bounded before materializing a response. Redirects never escape
/// the authenticated device connection.
actor DropLinkHTTPReader {
    let connection: NWConnection
    private var buffer = Data()
    private var ended = false
    init(connection: NWConnection) { self.connection = connection }
    private func receive() async throws {
        try Task.checkCancellation(); guard !ended else { return }
        let (bytes, complete): (Data?, Bool) = try await withCheckedThrowingContinuation { continuation in
            connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, complete, error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume(returning: (data, complete)) }
            }
        }
        if let bytes { buffer.append(bytes) }; ended = complete
        if bytes?.isEmpty != false && !complete { throw URLError(.networkConnectionLost) }
    }
    private func line(maximum: Int = 8192) async throws -> Data {
        while true {
            if let delimiter = buffer.range(of: Data([13, 10])) {
                let length = buffer.distance(from: buffer.startIndex, to: delimiter.lowerBound)
                guard length <= maximum else { throw DropSpaceError.limit("DropLink HTTP line") }
                let value = Data(buffer[..<delimiter.lowerBound]); buffer.removeSubrange(..<delimiter.upperBound); return value
            }
            guard buffer.count <= maximum else { throw DropSpaceError.limit("DropLink HTTP line") }
            guard !ended else { throw URLError(.networkConnectionLost) }; try await receive()
        }
    }
    private func take(_ count: Int) async throws -> Data {
        while buffer.count < count { guard !ended else { throw URLError(.networkConnectionLost) }; try await receive() }
        let value = Data(buffer.prefix(count)); buffer.removeFirst(count); return value
    }
    private func headers() async throws -> [String: String] {
        var result = [String: String](), size = 0
        for _ in 0..<128 {
            let bytes = try await line(); size += bytes.count + 2
            guard size <= 32768 else { throw DropSpaceError.limit("DropLink HTTP headers") }
            if bytes.isEmpty { return result }
            guard let text = String(data: bytes, encoding: .ascii), let colon = text.firstIndex(of: ":"), colon != text.startIndex else { throw DropSpaceError.invalid("DropLink HTTP header") }
            let key = String(text[..<colon]).lowercased(), value = text[text.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            guard key.utf8.allSatisfy({ (97...122).contains($0) || (48...57).contains($0) || $0 == 45 }), result[key] == nil,
                  value.utf8.allSatisfy({ (32...126).contains($0) || $0 == 9 }) else { throw DropSpaceError.invalid("Ambiguous DropLink HTTP header") }
            result[key] = value
        }
        throw DropSpaceError.limit("DropLink HTTP headers")
    }
    func response(maximum: Int) async throws -> DropLinkHTTPResponse {
        for _ in 0..<5 {
            let statusLine = try await line(), parts = String(decoding: statusLine, as: UTF8.self).split(separator: " ", maxSplits: 2)
            guard parts.count >= 2, ["HTTP/1.1", "HTTP/1.0"].contains(String(parts[0])), parts[1].count == 3,
                  let status = Int(parts[1]), (100...599).contains(status) else { throw DropSpaceError.invalid("DropLink HTTP status") }
            let fields = try await headers()
            if (100...199).contains(status) { guard status != 101 else { throw DropSpaceError.invalid("DropLink HTTP upgrade") }; continue }
            guard !(300...399).contains(status) else { throw DropSpaceError.invalid("DropLink redirect") }
            let body = status == 204 ? Data() : try await readBody(fields: fields, maximum: maximum, untilEOF: true)
            return DropLinkHTTPResponse(status: status, body: body)
        }
        throw DropSpaceError.limit("DropLink informational responses")
    }
    func readBody(fields: [String: String], maximum: Int, untilEOF: Bool = false) async throws -> Data {
        try await readBufferedBody(fields: fields, maximum: maximum, untilEOF: untilEOF, threshold: Int.max).data
    }
    func readBufferedBody(fields: [String: String], maximum: Int, untilEOF: Bool = false, threshold: Int = 65536) async throws -> DropLinkBufferedBody {
        let body = DropLinkBodyBuffer(maximum: maximum, threshold: threshold)
        do {
            if let encoding = fields["content-encoding"], encoding.lowercased() != "identity" { throw DropSpaceError.invalid("Encoded DropLink response") }
            guard fields["transfer-encoding"] == nil || fields["content-length"] == nil else { throw DropSpaceError.invalid("Ambiguous DropLink framing") }
            var total = 0
            if let transfer = fields["transfer-encoding"] {
                guard transfer.lowercased() == "chunked" else { throw DropSpaceError.invalid("DropLink transfer encoding") }
                while true {
                    let line = try await line(maximum: 1024)
                    guard let sizeText = String(data: line, encoding: .ascii)?.split(separator: ";", omittingEmptySubsequences: false).first,
                          !sizeText.isEmpty, sizeText.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }),
                          let count = Int(sizeText, radix: 16) else { throw DropSpaceError.invalid("DropLink chunk size") }
                    guard count <= maximum - total else { throw DropSpaceError.limit("DropLink chunk size") }
                    if count == 0 { _ = try await headers(); break }
                    var remaining = count
                    while remaining > 0 {
                        let length = min(remaining, 65536); try await body.append(take(length)); remaining -= length
                    }
                    total += count
                    guard try await take(2) == Data([13, 10]) else { throw DropSpaceError.invalid("DropLink chunk delimiter") }
                }
            } else if let text = fields["content-length"] {
                guard !text.isEmpty, text.utf8.allSatisfy({ (48...57).contains($0) }), let count = Int(text) else { throw DropSpaceError.invalid("DropLink content length") }
                guard count <= maximum else { throw DropSpaceError.limit("DropLink response size") }
                var remaining = count
                while remaining > 0 {
                    let length = min(remaining, 65536); try await body.append(take(length)); remaining -= length
                }
            } else if untilEOF {
                repeat {
                    guard buffer.count <= maximum - total else { throw DropSpaceError.limit("DropLink response size") }
                    total += buffer.count; try await body.append(buffer); buffer.removeAll(keepingCapacity: true)
                    if ended { break }; try await receive()
                } while true
            }
            return try await body.finish()
        } catch {
            await body.close()
            if threshold != Int.max, error is POSIXError || (error as NSError).domain == NSCocoaErrorDomain {
                throw DropSpaceError.limit("DropLink request buffering failed: " + error.localizedDescription)
            }
            throw error
        }
    }
    func requestHead() async throws -> DropLinkHTTPHead {
        let bytes = try await line(), words = String(decoding: bytes, as: UTF8.self).split(separator: " ", omittingEmptySubsequences: false)
        guard words.count == 3, ["HTTP/1.1", "HTTP/1.0"].contains(String(words[2])),
              !words[0].isEmpty, words[0].utf8.allSatisfy({ (65...90).contains($0) }),
              words[1].hasPrefix("/"), !words[1].hasPrefix("//"), !words[1].contains("#"),
              words[1].utf8.allSatisfy({ (33...126).contains($0) }) else { throw DropSpaceError.invalid("DropLink request line") }
        let fields = try await headers()
        guard words[2] == "HTTP/1.0" || fields["host"]?.isEmpty == false else { throw DropSpaceError.invalid("DropLink HTTP host") }
        let target = String(words[1]), rawPath = String(target.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)[0])
        let escaped = Array(rawPath.utf8); var decoded = [UInt8](), index = 0
        while index < escaped.count {
            if escaped[index] == 37 {
                guard index + 2 < escaped.count, let value = UInt8(String(decoding: escaped[(index + 1)...(index + 2)], as: UTF8.self), radix: 16) else { throw DropSpaceError.invalid("DropLink path encoding") }
                if value == 47 { decoded.append(contentsOf: escaped[index...(index + 2)]) } else { decoded.append(value) }
                index += 3
            } else { decoded.append(escaped[index]); index += 1 }
        }
        guard let path = String(bytes: decoded, encoding: .utf8), !path.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }) else { throw DropSpaceError.invalid("DropLink path encoding") }
        return DropLinkHTTPHead(method: String(words[0]), path: path, headers: fields)
    }

}
