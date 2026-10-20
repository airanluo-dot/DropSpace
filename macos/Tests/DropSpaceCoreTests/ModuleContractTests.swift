import Foundation
import Testing
import DropSpaceCore

private let moduleSystem = OperatingSystemVersion(majorVersion: 26, minorVersion: 0, patchVersion: 0)
private func nativeModuleFixture() throws -> ModuleManifest {
    let bytes = try Data(contentsOf: Bundle.module.resourceURL!.appendingPathComponent("Fixtures/windows-module-v1.json"))
    var manifest = try ModuleCodec.manifest(bytes)
    manifest.system = "macos"; manifest.architecture = "arm64"; manifest.minimumMacOSVersion = "26.0"; manifest.entryPoint = "bin/module-worker"
    manifest.files = [.init(path: manifest.entryPoint, bytes: 1, sha256: String(repeating: "a", count: 64))]
    return manifest
}

@Test func moduleManifestRetainsWindowsMeaningAndRequiresExplicitNativeCompatibility() throws {
    let bytes = try Data(contentsOf: Bundle.module.resourceURL!.appendingPathComponent("Fixtures/windows-module-v1.json"))
    let windows = try ModuleCodec.manifest(bytes)
    #expect(windows.system == "windows" && windows.architecture == "x64" && windows.minimumMacOSVersion == nil)
    #expect(throws: (any Error).self) { try ModuleContract.validate(windows, installed: [:], systemVersion: moduleSystem) }
    var native = try nativeModuleFixture()
    try ModuleContract.validate(native, installed: [:], systemVersion: moduleSystem)
    #expect(ModuleContract.text(native, .init(key: "name"), language: "zh-CN") == "模块示例")
    #expect(ModuleContract.text(native, .init(key: "name"), language: "unknown") == "Module sample")
    native.minimumMacOSVersion = "26.1"
    #expect(throws: (any Error).self) { try ModuleContract.validate(native, installed: [:], systemVersion: moduleSystem) }
    native.minimumMacOSVersion = nil
    #expect(throws: (any Error).self) { try ModuleContract.validate(native, installed: [:], systemVersion: moduleSystem) }
    native.minimumMacOSVersion = "26.0"; native.requiredCapabilities.append(.init(id: "future", version: 1))
    #expect(throws: (any Error).self) { try ModuleContract.validate(native, installed: [:], systemVersion: moduleSystem) }
    native.requiredCapabilities.removeLast(); native.optionalCapabilities.append(.init(id: "future", version: 99, fallback: "omit"))
    try ModuleContract.validate(native, installed: [:], systemVersion: moduleSystem)
    native.optionalCapabilities[native.optionalCapabilities.count - 1].fallback = nil
    #expect(throws: (any Error).self) { try ModuleContract.validate(native, installed: [:], systemVersion: moduleSystem) }
}

@Test func moduleManifestCaseDefaultsDependenciesAndUTF16TranslationBounds() throws {
    var native = try nativeModuleFixture()
    var raw = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(native)) as? [String: Any])
    raw["version"] = raw.removeValue(forKey: "Version"); raw["SYSTEM"] = raw.removeValue(forKey: "System")
    let restored = try ModuleCodec.manifest(JSONSerialization.data(withJSONObject: raw))
    #expect(restored.version == native.version && restored.system == "macos" && restored.resources == native.resources)
    let omitted = try ModuleCodec.manifest(Data(#"{"Id":"test","Version":"1.0.0","EntryPoint":"worker","Name":{"Key":"name"}}"#.utf8))
    #expect(omitted.system == "windows" && omitted.architecture == "x64")
    #expect(throws: (any Error).self) {
        try ModuleCodec.manifest(Data(#"{"Id":"test","Version":"1.0.0","EntryPoint":"worker","Name":{"Key":"name"},"Publisher":null}"#.utf8))
    }
    let duplicate = try ModuleCodec.manifest(Data(#"{"Id":"test","Version":"1.0.0","version":"2.0.0","EntryPoint":"worker","Name":{"Key":"name"}}"#.utf8))
    #expect(duplicate.version == "2.0.0")
    let defaultCapability = try ModuleCodec.manifest(Data(#"{"Id":"test","Version":"1.0.0","EntryPoint":"worker","Name":{"Key":"name"},"OptionalCapabilities":[{"Id":"ui.pages"}]}"#.utf8))
    #expect(defaultCapability.optionalCapabilities.first?.version == 0)
    for field in ["MaximumHostInterface", "MinimumWindowsBuild"] {
        #expect(throws: (any Error).self) { try ModuleCodec.manifest(Data("{\"Id\":\"test\",\"Version\":\"1.0.0\",\"EntryPoint\":\"worker\",\"Name\":{\"Key\":\"name\"},\"\(field)\":2147483648}".utf8)) }
    }
    native.dependencies = [.init(id: "dependency", minimumVersion: "1.2.0", maximumVersionExclusive: "2.0.0")]
    let installed = ModuleInstallation(id: "dependency", version: "1.2.0", enabled: false, dataVersion: 1)
    try ModuleContract.validate(native, installed: [installed.id: installed], systemVersion: moduleSystem)
    var boundary = installed; boundary.version = "2.0.0"
    #expect(throws: (any Error).self) { try ModuleContract.validate(native, installed: [boundary.id: boundary], systemVersion: moduleSystem) }
    native.dependencies = []
    native.resources["en-US"]?["name"] = String(repeating: "😀", count: 2049)
    #expect(throws: (any Error).self) { try ModuleContract.validate(native, installed: [:], systemVersion: moduleSystem) }
    #expect(ModuleContract.version("1.2.3") == [1, 2, 3])
    for invalid in ["01.2.3", "1.2", "1.2.3.4", "1.2.2147483648", "1.2.３"] { #expect(ModuleContract.version(invalid) == nil) }
}

@Test func moduleV1WirePreservesNamesWideNumbersAndRejectsExpiredReplies() throws {
    let payload = try ModuleJSON.decode(Data(#"{"text":"\u4e2d\u6587","counter":12345678901234567890123456789012345678901234567890,"value":1,"value":2}"#.utf8))
    #expect(payload["value"]?.integer == 2)
    let request = ModuleEnvelope(session: "01234567890123456789012345678901", id: "1", method: "action", payload: payload)
    let bytes = try request.encoded()
    #expect(String(decoding: bytes, as: UTF8.self).contains("12345678901234567890123456789012345678901234567890"))
    #expect(try ModuleEnvelope.decode(bytes) == request)
    let reply = ModuleEnvelope(session: request.session, id: "1", method: "result", payload: .object([.init("Result", payload), .init("Island", .null)]))
    #expect(try reply.reply(expectedSession: request.session, expectedID: "1").result == payload)
    #expect(throws: (any Error).self) { try reply.reply(expectedSession: "other", expectedID: "1") }
    #expect(throws: (any Error).self) { try reply.reply(expectedSession: request.session, expectedID: "2") }
    for invalid in ["01", "1e", "[1,]", "{\"x\":1,}", "\"\\ud800\"", "true false"] {
        #expect(throws: (any Error).self) { try ModuleJSON.decode(Data(invalid.utf8)) }
    }
    let nested = String(repeating: "[", count: 65) + "0" + String(repeating: "]", count: 65)
    #expect(throws: (any Error).self) { try ModuleJSON.decode(Data(nested.utf8)) }
    #expect(throws: (any Error).self) { try ModuleJSON.string(String(repeating: "x", count: 65535)).encoded() }
}

@Test func moduleIslandRequiresDeclaredActionsResourcesAndBoundedExpiry() throws {
    let manifest = try nativeModuleFixture(), now = Date(timeIntervalSince1970: 1_000_000)
    let action = try #require(manifest.ui.pages.first?.actions.first)
    var content = ModuleIslandContent(compact: .init(key: "compact"), expanded: .init(key: "expanded"), actions: [action], expiresAt: now.addingTimeInterval(60))
    try ModuleContract.validateIsland(content, manifest: manifest, now: now)
    content.expiresAt = now.addingTimeInterval(60.001)
    #expect(throws: (any Error).self) { try ModuleContract.validateIsland(content, manifest: manifest, now: now) }
    content.expiresAt = now; #expect(throws: (any Error).self) { try ModuleContract.validateIsland(content, manifest: manifest, now: now) }
    content.expiresAt = now.addingTimeInterval(30); content.actions[0].label = .init(key: "name")
    #expect(throws: (any Error).self) { try ModuleContract.validateIsland(content, manifest: manifest, now: now) }
}
