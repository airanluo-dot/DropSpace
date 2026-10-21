import Foundation
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private actor ShareHTTPRequests {
    var count = 0
    func hit() { count += 1 }
}
@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_TEST_SHARE_WEBPKI"] == "1"))
func shareWebPKIAcceptsPublicTLSAndRefusesDeviceSelfSignedIdentity() async throws {
    let budget = DownloadBudget(), client = ShareHTTPSClient(budget: budget)
    let publicResponse = try await client.send(url: URL(string: "https://www.apple.com/library/test/success.html")!, method: "GET", body: Data(), authorization: nil, contentType: "application/octet-stream", timeout: 20)
    #expect(publicResponse.status == 200)
    #expect(String(decoding: publicResponse.body, as: UTF8.self).contains("Success"))
    let identity = try DropLinkIdentity.create(name: "Owned WebPKI rejection test"), hits = ShareHTTPRequests()
    let server = DropLinkHTTPServer(identity: identity, admit: { _, _ in .init(maximumBodyBytes: 1024) }, handler: { _ in await hits.hit(); return .init(status: 200, body: Data("must not reach handler".utf8)) })
    do {
        let endpoint = try await server.start(address: "127.0.0.1")
        await #expect(throws: (any Error).self) {
            try await client.send(url: endpoint, method: "POST", body: Data("owned test bytes".utf8), authorization: "Bearer owned-test-token", contentType: "application/octet-stream", timeout: 10)
        }
        #expect(await hits.count == 0); #expect(await budget.connections.activeCount == 0)
        await server.stop()
    } catch { await server.stop(); throw error }
}
