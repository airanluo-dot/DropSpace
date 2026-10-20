import Foundation
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

@Test func updateArchiveRejectsTraversalLinksAndConflictingHeaders() throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let file = directory.appendingPathComponent("candidate.zip")
    func make(_ path: String = "DropSpace.app/Contents/Info.plist", mode: UInt32 = 0o100644, localName: String? = nil) -> Data {
        var local = Data(), central = Data()
        func word(_ value: UInt16, to data: inout Data) { data.append(UInt8(truncatingIfNeeded: value)); data.append(UInt8(truncatingIfNeeded: value >> 8)) }
        func long(_ value: UInt32, to data: inout Data) { word(UInt16(truncatingIfNeeded: value), to: &data); word(UInt16(truncatingIfNeeded: value >> 16), to: &data) }
        for (index, name) in [path, "DropSpace.app/Contents/MacOS/DropSpace"].enumerated() {
            let offset = UInt32(local.count); let actual = index == 0 ? localName ?? name : name
            long(0x04034b50, to: &local); word(20, to: &local)
            for _ in 0..<4 { word(0, to: &local) }; for _ in 0..<3 { long(0, to: &local) }
            word(UInt16(actual.utf8.count), to: &local); word(0, to: &local); local.append(contentsOf: actual.utf8)
            long(0x02014b50, to: &central); word(0x0314, to: &central); word(20, to: &central)
            for _ in 0..<4 { word(0, to: &central) }; for _ in 0..<3 { long(0, to: &central) }
            word(UInt16(name.utf8.count), to: &central); for _ in 0..<4 { word(0, to: &central) }
            long((index == 0 ? mode : 0o100755) << 16, to: &central); long(offset, to: &central); central.append(contentsOf: name.utf8)
        }
        var end = Data(); long(0x06054b50, to: &end); word(0, to: &end); word(0, to: &end)
        word(2, to: &end); word(2, to: &end); long(UInt32(central.count), to: &end); long(UInt32(local.count), to: &end); word(0, to: &end)
        return local + central + end
    }
    try make().write(to: file); try UpdateArchive.inspect(file)
    for invalid in [make("DropSpace.app/../../escape"), make(mode: 0o120777), make(localName: "DropSpace.app/Contents/evil.plist"), make("DropSpace.app/Contents/../Info.plist")] {
        try invalid.write(to: file)
        #expect(throws: (any Error).self) { try UpdateArchive.inspect(file) }
    }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_UPDATE_PACKAGE"] != nil))
func actualPackagedUpdatePreparesAndRejectsTampering() async throws {
    let input = URL(fileURLWithPath: ProcessInfo.processInfo.environment["DROPSPACE_UPDATE_PACKAGE"]!)
    let manifest = try JSONDecoder().decode(MacUpdateManifest.self, from: Data(contentsOf: input.appendingPathComponent("update-manifest-macos.json")))
    let archive = input.appendingPathComponent(manifest.archive.name)
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).resolvingSymlinksInPath()
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    try await ProcessRunner.run(executable: URL(fileURLWithPath: "/usr/bin/ditto"), arguments: ["-x", "-k", archive.path, directory.path])
    let app = directory.appendingPathComponent("DropSpace.app")
    try AppBundleIntegrity.verify(app, manifest: manifest)
    let store = MacUpdateStore(dataDirectory: directory.appendingPathComponent("Data"), budget: DownloadBudget())
    let (plan, _) = try await store.prepare(archive: archive, manifest: manifest, application: app)
    try plan.validateLocations()
    #expect(FileManager.default.fileExists(atPath: plan.candidate.path))
    #expect(!FileManager.default.fileExists(atPath: plan.backup.path))
    let damagedResource = plan.candidate.appendingPathComponent("Contents/Resources/Distribution.md")
    try Data("modified".utf8).write(to: damagedResource)
    #expect(throws: (any Error).self) { try AppBundleIntegrity.verify(plan.candidate, manifest: manifest) }
    try AppBundleIntegrity.verify(app, manifest: manifest)
}
