import Foundation
import Testing
import DropSpaceCore

@Test func macUpdateVersionChannelAndArtifactIsolation() throws {
    #expect(try MacVersion("macos-v0.1.0-beta.9") < MacVersion("macos-v0.1.0"))
    #expect(try MacVersion("macos-v0.2.0-beta.1") > MacVersion("macos-v0.1.0"))
    for tag in ["v0.3.2-beta.7", "macos-v00.1.0", "macos-v0.1.0-preview.1", "macos-v0.1.0-beta.0", "macos-v0.1.0/../v2"] {
        #expect(throws: (any Error).self) { try MacVersion(tag) }
    }
    #expect(MacUpdateOrigin.feed(.beta).path == "/DropSpace/api/macos/v1/beta.json")
    #expect(!MacUpdateOrigin.allowsAssetRedirect(URL(string: "http://github.com/airanluo-dot/DropSpace/releases/download/macos-v1.0.0/a")!))
    #expect(!MacUpdateOrigin.allowsAssetRedirect(URL(string: "https://github.com/other/repo/releases/download/macos-v1.0.0/a")!))
    #expect(!MacUpdateOrigin.allowsAssetRedirect(URL(string: "https://release-assets.githubusercontent.com.evil.test/file")!))
    #expect(MacUpdateOrigin.allowsAssetRedirect(URL(string: "https://release-assets.githubusercontent.com/file?token=value")!))
    let tag = "macos-v0.1.0-beta.2"
    let base = "https://github.com/airanluo-dot/DropSpace/releases/"
    func asset(_ name: String) -> [String: Any] { ["name": name, "size": 12, "sha256": String(repeating: "a", count: 64), "url": base + "download/" + tag + "/" + name] }
    var json: [String: Any] = ["schemaVersion": 1, "platform": "macos", "architecture": "arm64", "tag": tag, "channel": "beta", "bundleID": Baseline.bundleID, "bundleVersion": "2", "minimumOS": "26.0", "windowsBaseline": Baseline.windowsVersion, "signing": "ad-hoc", "notarized": false, "archive": asset("DropSpace-macOS-arm64.zip"), "installer": asset("DropSpace-macOS-arm64.dmg"), "releaseNotes": "Changes", "releaseURL": base + "tag/" + tag]
    func manifest() throws -> MacUpdateManifest { try JSONDecoder().decode(MacUpdateManifest.self, from: JSONSerialization.data(withJSONObject: json)) }
    let currentOS = OperatingSystemVersion(majorVersion: 26, minorVersion: 0, patchVersion: 0)
    try manifest().validate(channel: .beta, os: currentOS)
    #expect(throws: (any Error).self) { try manifest().validate(channel: .stable, os: currentOS) }
    #expect(throws: (any Error).self) { try manifest().validate(channel: .beta, os: .init(majorVersion: 25, minorVersion: 9, patchVersion: 0)) }
    json["notarized"] = true
    #expect(throws: (any Error).self) { try manifest().validate(channel: .beta, os: currentOS) }
    json["notarized"] = false; json["platform"] = "windows"
    #expect(throws: (any Error).self) { try manifest().validate(channel: .beta, os: currentOS) }
}
