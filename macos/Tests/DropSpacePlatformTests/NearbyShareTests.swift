import Foundation
import Testing
import DropSpaceCore
import DropSpacePlatform
@testable import DropSpaceInfrastructure

@Test func nearbyNativeHTTPRangesRevocationExpiryAndNetworkLifetime() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-Nearby-" + UUID().dropLinkN)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
    let original = root.appendingPathComponent("中文 & file.bin"), bytes = Data((0..<300000).map { UInt8($0 % 251) })
    try bytes.write(to: original)
    let sources = try await ShareSourcePreparation().prepare([.file(original), .text("https://example.com/path#fragment", name: "URL", isURL: true), .text("<script>text & quote\"", name: "<&\"", isURL: false)])
    let address = try DropLinkNetworkInterface.preferred().address
    let server = try NearbyShareServer(resolveAddress: { address })
    #expect(await server.snapshot().items.isEmpty)
    let configuration = URLSessionConfiguration.ephemeral; configuration.timeoutIntervalForRequest = 5
    let http = URLSession(configuration: configuration); defer { http.invalidateAndCancel() }
    func fetch(_ url: URL, range: String? = nil, method: String = "GET") async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: url); request.httpMethod = method; request.setValue(range, forHTTPHeaderField: "Range")
        let (bytes, response) = try await http.data(for: request); return (bytes, try #require(response as? HTTPURLResponse))
    }
    do {
        let share = try await server.create(sources)
        #expect(share.url.scheme == "http" && share.itemCount == 3 && !share.isEncrypted)
        let (html, response) = try await fetch(share.url)
        #expect(response.statusCode == 200); #expect(response.value(forHTTPHeaderField: "Cache-Control") == "no-store")
        #expect(response.value(forHTTPHeaderField: "X-DropSpace-Transport") == "unencrypted-private-http")
        #expect(String(decoding: html, as: UTF8.self).contains("&lt;&amp;&quot;"))
        let file = share.url.appendingPathComponent("file/" + sources[0].id.dropLinkN)
        let (all, wholeResponse) = try await fetch(file)
        #expect(wholeResponse.statusCode == 200 && all == bytes)
        #expect(wholeResponse.value(forHTTPHeaderField: "Content-Disposition")?.contains("filename*=UTF-8''%E4%B8%AD%E6%96%87") == true)
        let (range, partial) = try await fetch(file, range: "bytes=7-18")
        #expect(partial.statusCode == 206 && range == bytes.subdata(in: 7..<19))
        #expect(partial.value(forHTTPHeaderField: "Content-Range") == "bytes 7-18/300000")
        let (suffix, _) = try await fetch(file, range: "bytes=-19"); #expect(suffix == bytes.suffix(19))
        let (_, invalid) = try await fetch(file, range: "bytes=0-1,4-5"); #expect(invalid.statusCode == 416)
        let (head, headResponse) = try await fetch(file, method: "HEAD"); #expect(head.isEmpty && headResponse.statusCode == 200)
        let path = share.url.path
        #expect(try await server.handle(.init(method: "GET", path: path, headers: [:]), address: "203.0.113.10").status == 403)
        #expect(try await server.handle(.init(method: "GET", path: path, headers: [:]), address: "10.20.30.40").status == 200)
        #expect(try await server.handle(.init(method: "GET", path: path, headers: [:]), address: "10.20.30.41").status == 429)
        // Already admitted streams survive revocation; later requests fail.
        let admitted = try await server.handle(.init(method: "GET", path: file.path, headers: [:]), address: address)
        #expect(await server.revoke(share.id)); #expect(await server.descriptor(share.id) == nil)
        #expect(try await admitted.reader?.read(offset: 0, count: 13) == bytes.prefix(13)); await admitted.reader?.close()
        let (_, revoked) = try await fetch(file); #expect(revoked.statusCode == 404)
        let expiring = try await server.create(sources, lifetime: 0.08)
        try await Task.sleep(for: .milliseconds(150))
        #expect(await server.snapshot().items.isEmpty); #expect(await server.descriptor(expiring.id) == nil)
        let (_, expired) = try await fetch(expiring.url); #expect([404, 410].contains(expired.statusCode))
        let changed = try await server.create(sources)
        await server.networkChanged(); #expect(await server.snapshot().items.isEmpty)
        #expect(await server.descriptor(changed.id) == nil)
        let rebound = try await server.create(sources)
        let (_, reboundResponse) = try await fetch(rebound.url); #expect(reboundResponse.statusCode == 200)
        try Data("changed".utf8).write(to: original)
        let (_, changedSource) = try await fetch(rebound.url.appendingPathComponent("file/" + sources[0].id.dropLinkN)); #expect(changedSource.statusCode == 409)
        #expect(await server.stop()); #expect(await server.snapshot().items.isEmpty)
        await #expect(throws: (any Error).self) { try await server.create(sources) }
        #expect(try Data(contentsOf: original) == Data("changed".utf8))
    } catch { _ = await server.stop(); throw error }
}

@Test func nearbyShareSourceTraversalAndCapacity() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-Share-Sources-" + UUID().dropLinkN)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: root) }
    let folder = root.appendingPathComponent("folder"), nested = folder.appendingPathComponent("nested"), empty = folder.appendingPathComponent("empty")
    try FileManager.default.createDirectory(at: nested, withIntermediateDirectories: true); try FileManager.default.createDirectory(at: empty, withIntermediateDirectories: true)
    try Data().write(to: nested.appendingPathComponent("zero.txt")); try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("skip"), withDestinationURL: root)
    let preparation = ShareSourcePreparation(), sources = try await preparation.prepare([.folder(folder)])
    #expect(sources.count == 1 && sources[0].length == 0 && sources[0].name == "folder - nested_zero.txt")
    await #expect(throws: (any Error).self) { try await preparation.prepare([.folder(empty)]) }
    await #expect(throws: (any Error).self) { try await preparation.prepare([.folder(folder.appendingPathComponent("skip"))]) }
    var limits = ShareLimits(); limits.maxNearbyShares = 1
    let address = try DropLinkNetworkInterface.preferred().address, server = try NearbyShareServer(limits: limits, resolveAddress: { address })
    do {
        let share = try await server.create(sources)
        await #expect(throws: (any Error).self) { try await server.create(sources) }
        let file = share.url.appendingPathComponent("file/" + sources[0].id.dropLinkN)
        let response = try await server.handle(.init(method: "GET", path: file.path, headers: [:]), address: address)
        #expect(response.status == 200 && response.length == 0); await response.reader?.close()
        let range = try await server.handle(.init(method: "GET", path: file.path, headers: ["range": "bytes=0-"]), address: address)
        #expect(range.status == 416)
        #expect(await server.stop())
    } catch { _ = await server.stop(); throw error }
}
