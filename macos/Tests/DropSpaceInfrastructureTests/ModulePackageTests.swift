import Foundation
import Darwin
import Testing
import DropSpaceCore
@testable import DropSpaceInfrastructure

private let packageSources = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
private func packageRoot() throws -> URL {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-package-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); return root
}
private func fixture(_ root: URL, version: String = "1.0.0", mode: String = "valid", lifecycle: Bool = false, id: String = "dropspace.sample") throws -> OfficialModulePackage {
    let destination = root.appendingPathComponent("staging/" + id + "/" + version)
    let process = Process(); process.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
    process.arguments = [packageSources.appendingPathComponent("Tests/Fixtures/module-package.py").path, destination.path,
        packageSources.appendingPathComponent("Modules/Sample/manifest.template.json").path,
        packageSources.appendingPathComponent(lifecycle ? "build/module-guardian/module-lifecycle-worker" : ".build/debug/DropSpaceModuleSample").path, version, mode, id]
    try process.run(); process.waitUntilExit(); #expect(process.terminationStatus == 0)
    return try JSONDecoder().decode(OfficialModulePackage.self, from: Data(contentsOf: destination.appendingPathComponent("descriptor.json")))
}

@Test func nativeModuleRuntimeCommitsUpgradeExpiresAndRetiresFaultedGenerations() async throws {
    let root = try packageRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let store = ModulePackageStore(root: root, budget: .init()), service = try runtime(store), initial = try fixture(root)
    do {
        try await service.installPackage(initial)
        let updated = try fixture(root, version: "1.1.0", lifecycle: true)
        try await service.installPackage(updated)
        #expect(await service.snapshots().first?.installation.version == "1.1.0")
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("packages/dropspace.sample/1.0.0").path))
        _ = try await service.invoke(initial.id, action: "show")
        #expect(await service.snapshots().first?.island != nil)
        try await Task.sleep(for: .seconds(2.1))
        #expect(await service.snapshots().first?.island == nil)
        #expect(await service.diagnostics().expiry == false)
        let data = try await store.dataDirectory(initial.id)
        try Data("crash".utf8).write(to: data.appendingPathComponent("runtime-mode"))
        await #expect(throws: (any Error).self) { try await service.invoke(initial.id, action: "show") }
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while await service.snapshots().first?.state != .faulted, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
        #expect(await service.snapshots().first?.state == .faulted)
        #expect(await service.snapshots().first?.visible == false)
        try Data("stall".utf8).write(to: data.appendingPathComponent("runtime-mode"))
        try await service.enable(initial.id)
        let active = Task { try await service.invoke(initial.id, action: "show") }
        let started = data.appendingPathComponent("request-started"), startDeadline = ContinuousClock.now.advanced(by: .seconds(2))
        while !FileManager.default.fileExists(atPath: started.path), ContinuousClock.now < startDeadline { try await Task.sleep(for: .milliseconds(10)) }
        #expect(FileManager.default.fileExists(atPath: started.path))
        try await service.disable(initial.id)
        await #expect(throws: (any Error).self) { try await active.value }
        #expect(await service.snapshots().first?.island == nil)
        let state = await service.diagnostics(); #expect(state.workers == 0 && state.calls == 0 && !state.expiry)
        #expect(await service.shutdown())
    } catch { _ = await service.shutdown(); throw error }
}

@Test func nativeModulePackageInstallsImmutableBytesAndRunsActualWorker() async throws {
    let root = try packageRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let store = ModulePackageStore(root: root, budget: .init()), package = try fixture(root)
    let manifest = try await store.prepare(package, installed: [:])
    #expect(manifest.version == "1.0.0")
    #expect(try await store.load().isEmpty) // Preparing a package cannot silently enable it.
    let path = try await store.versionDirectory(package.id, version: package.version)
    let client = ModuleWorkerClient(guardian: packageSources.appendingPathComponent("build/module-guardian/module-guardian"),
        executable: path.appendingPathComponent(manifest.entryPoint), dataDirectory: try await store.dataDirectory(package.id), manifest: manifest)
    do {
        try await client.start()
        let reply = try await client.request("action", payload: .object([.init("action", .string("show"))]))
        #expect(reply.result["messageKey"]?.string == "message")
        #expect(await client.stop())
    } catch { _ = await client.stop(); throw error }
    #expect(try await store.readManifest(package.id, version: package.version) == manifest)
    let firstBytes = try Data(contentsOf: path.appendingPathComponent(".package.zip"))
    #expect(try await store.prepare(package, installed: [:]) == manifest)
    #expect(try Data(contentsOf: path.appendingPathComponent(".package.zip")) == firstBytes)
    let different = try fixture(root, mode: "stored")
    await #expect(throws: (any Error).self) { try await store.prepare(different, installed: [:]) }
    #expect(try Data(contentsOf: path.appendingPathComponent(".package.zip")) == firstBytes)
    let data = try await store.dataDirectory(package.id).appendingPathComponent("owned.txt")
    try Data("retained".utf8).write(to: data)
    #expect(try await store.removeVersion(package.id, version: package.version))
    #expect(try String(contentsOf: data, encoding: .utf8) == "retained")
}

@Test func nativeModulePackageRejectsMalformedArchivesBeforeActivation() async throws {
    for mode in ["traversal", "unicode", "case", "ancestor", "symlink", "unlisted", "duplicate-json", "crc", "expanded", "hash", "windows"] {
        let root = try packageRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let package = try fixture(root, mode: mode), store = ModulePackageStore(root: root, budget: .init())
        await #expect(throws: (any Error).self, "Accepted malformed fixture: \(mode)") { try await store.prepare(package, installed: [:]) }
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("packages/dropspace.sample/1.0.0").path))
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("staging/dropspace.sample/escaped.txt").path))
    }
    let root = try packageRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let store = ModulePackageStore(root: root, budget: .init()), package = try fixture(root, mode: "stored")
    _ = try await store.prepare(package, installed: [:]) // The stored/empty-entry path is valid too.
    let path = try await store.versionDirectory(package.id, version: package.version)
    try Data("tampered".utf8).write(to: path.appendingPathComponent("data/empty.txt"))
    await #expect(throws: (any Error).self) { try await store.readManifest(package.id, version: package.version) }
}

@Test func nativeModuleStateRecoveryAndCleanupPreserveUnownedData() async throws {
    let parent = try packageRoot(); defer { try? FileManager.default.removeItem(at: parent) }
    let root = parent.appendingPathComponent("store"), store = ModulePackageStore(root: root, budget: .init())
    #expect(try await store.load().isEmpty)
    #expect(!FileManager.default.fileExists(atPath: root.path))
    let first = ModuleInstallation(id: "dropspace.sample", version: "1.0.0", enabled: false, dataVersion: 1)
    try await store.write(first)
    var second = first; second.enabled = true; try await store.write(second)
    let state = root.appendingPathComponent("state/dropspace.sample.json")
    try Data("{invalid".utf8).write(to: state)
    let recovered = try #require(try await store.load().first)
    #expect(!recovered.enabled && recovered.errorCode == "ModuleStateRecovered" && recovered.version == "1.0.0")
    #expect(try String(contentsOf: state, encoding: .utf8) == "{invalid")
    try Data("{invalid".utf8).write(to: state.appendingPathExtension("backup"))
    #expect(try await store.load().first?.errorCode == "ModuleStateInvalid")
    let data = try await store.dataDirectory(first.id), external = parent.appendingPathComponent("external.txt")
    try Data("safe".utf8).write(to: external)
    try FileManager.default.createSymbolicLink(at: data.appendingPathComponent("escape"), withDestinationURL: external)
    await #expect(throws: (any Error).self) { try await store.clearData(first.id) }
    #expect(try String(contentsOf: external, encoding: .utf8) == "safe")
    #expect(FileManager.default.fileExists(atPath: data.path))
    try FileManager.default.removeItem(at: data.appendingPathComponent("escape"))
    try await store.writeSettings(first.id, values: ["show-island": "false"])
    #expect(try await store.readSettings(first.id) == ["show-island": "false"])
    try await store.clearData(first.id)
    #expect(!FileManager.default.fileExists(atPath: data.path))
}

@Test func nativeModulePreparationRejectsDataMigrationAndCancelledWork() async throws {
    let root = try packageRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let store = ModulePackageStore(root: root, budget: .init()), old = try fixture(root)
    _ = try await store.prepare(old, installed: [:])
    let installed = ModuleInstallation(id: old.id, version: old.version, enabled: true, dataVersion: 1)
    try await store.write(installed)
    let new = try fixture(root, version: "2.0.0", mode: "migration")
    await #expect(throws: (any Error).self) { try await store.prepare(new, installed: [old.id: installed]) }
    #expect(try await store.load() == [installed])
    #expect(try await store.readManifest(old.id, version: old.version).version == old.version)
    let task = Task { withUnsafeCurrentTask { $0?.cancel() }; return try await store.prepare(new, installed: [:]) }
    await #expect(throws: CancellationError.self) { try await task.value }
    #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("packages/dropspace.sample/2.0.0").path))
}

private func runtime(_ store: ModulePackageStore) throws -> FeatureModuleRuntime {
    let catalog = try OfficialModuleCatalog(budget: .init())
    return FeatureModuleRuntime(store: store, catalog: catalog, guardian: packageSources.appendingPathComponent("build/module-guardian/module-guardian"))
}

@Test func nativeModuleFailedSettingsAndUninstallWritesStopWorkersWithoutLosingData() async throws {
    let root = try packageRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let store = ModulePackageStore(root: root, budget: .init()), service = try runtime(store)
    let package = try fixture(root, lifecycle: true)
    let data = try await store.dataDirectory(package.id), state = root.appendingPathComponent("state")
    defer { chmod(data.path, 0o700); chmod(state.path, 0o700) }
    do {
        try await service.installPackage(package)
        try await service.writeSetting(package.id, key: "show-island", value: "true")
        let settings = data.appendingPathComponent("settings.json"), before = try Data(contentsOf: settings)
        try Data("readonly-save".utf8).write(to: data.appendingPathComponent("runtime-mode"))
        // The actual worker acknowledges the request and makes its data directory
        // readonly. A failed host save must retire the worker's uncommitted state.
        await #expect(throws: (any Error).self) { try await service.writeSetting(package.id, key: "show-island", value: "false") }
        #expect(try Data(contentsOf: settings) == before)
        #expect(await service.snapshots().first?.visible == false)
        #expect(await service.diagnostics().workers == 0)
        #expect(chmod(data.path, 0o700) == 0)
        try FileManager.default.removeItem(at: data.appendingPathComponent("runtime-mode"))
        try await service.enable(package.id)
        #expect(await service.snapshots().first?.visible == true)
        #expect(chmod(state.path, 0o500) == 0)
        await #expect(throws: (any Error).self) { try await service.uninstall(package.id) }
        #expect(await service.diagnostics().workers == 0)
        #expect(await service.snapshots().first?.visible == false)
        #expect(try Data(contentsOf: settings) == before)
        #expect(chmod(state.path, 0o700) == 0)
        #expect(try await store.load().first?.version == package.version)
        try await service.enable(package.id)
        try await service.uninstall(package.id)
        #expect(try Data(contentsOf: settings) == before)
        #expect(await service.shutdown())
    } catch { _ = await service.shutdown(); throw error }
}

@Test func nativeModuleDependenciesRequireVerifiedPackagesAndProtectEnabledDependents() async throws {
    let root = try packageRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let store = ModulePackageStore(root: root, budget: .init()), service = try runtime(store)
    let base = try fixture(root, lifecycle: true, id: "dropspace.base")
    let child = try fixture(root, mode: "dependency", lifecycle: true, id: "dropspace.child")
    do {
        await #expect(throws: (any Error).self) { try await service.installPackage(child) }
        try await service.installPackage(base)
        // Failure cleanup removes the child's staged bytes. Rebuild only those
        // local bytes, using the same actual native binary and declaration.
        let childRetry = try fixture(root, mode: "dependency", lifecycle: true, id: child.id)
        try await service.installPackage(childRetry)
        await #expect(throws: (any Error).self) { try await service.uninstall(base.id) }
        #expect(await service.snapshots().allSatisfy(\.visible))
        try await service.disable(child.id)
        try await service.uninstall(base.id)
        await #expect(throws: (any Error).self) { try await service.enable(child.id) }
        #expect(await service.diagnostics().workers == 0)
        #expect(await service.shutdown())
        // A stale enabled state record is not evidence that a dependency exists.
        try await store.write(.init(id: base.id, version: base.version, enabled: true, dataVersion: 1))
        try await store.write(.init(id: child.id, version: child.version, enabled: true, dataVersion: 1))
        let recovered = try runtime(store)
        try await recovered.initialize()
        #expect(await recovered.snapshots().allSatisfy { !$0.visible && $0.state == .faulted })
        #expect(await recovered.diagnostics().workers == 0)
        #expect(await recovered.shutdown())
    } catch { _ = await service.shutdown(); throw error }
}

@Test func nativeModulePostcommitUnsafeAncestorKeepsNewWorkerAndRetryJournal() async throws {
    let parent = try packageRoot(); defer { try? FileManager.default.removeItem(at: parent) }
    let root = parent.appendingPathComponent("modules"), store = ModulePackageStore(root: root, budget: .init())
    let service = try runtime(store), first = try fixture(root, lifecycle: true)
    do {
        try await service.installPackage(first)
        let old = try await store.versionDirectory(first.id, version: first.version)
        let preserved = parent.appendingPathComponent("preserved-old-package")
        try FileManager.default.moveItem(at: old, to: preserved)
        try FileManager.default.createSymbolicLink(at: old, withDestinationURL: preserved)
        let updated = try fixture(root, version: "1.1.0", lifecycle: true)
        try await service.installPackage(updated)
        let committed = try #require(await service.snapshots().first)
        #expect(committed.visible && committed.installation.version == "1.1.0")
        #expect(committed.installation.transaction == .pendingCleanup && committed.installation.candidateVersion == "1.0.0")
        #expect(try await store.load().first?.transaction == .pendingCleanup)
        #expect(FileManager.default.fileExists(atPath: preserved.appendingPathComponent(".package.zip").path))
        #expect(try await service.invoke(first.id, action: "show").result["messageKey"]?.string == "message")
        try FileManager.default.removeItem(at: old) // Remove the owned link, never its target.
        try FileManager.default.moveItem(at: preserved, to: old)
        try await service.retryCleanup(first.id)
        #expect(await service.snapshots().first?.installation.transaction == ModuleTransactionState.none)
        #expect(await service.snapshots().first?.visible == true)
        #expect(!FileManager.default.fileExists(atPath: old.path))
        #expect(await service.shutdown())
    } catch { _ = await service.shutdown(); throw error }
}

@Test func nativeModuleRuntimeOwnsEnableSettingsRollbackAndUninstall() async throws {
    let root = try packageRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let store = ModulePackageStore(root: root, budget: .init()), service = try runtime(store), package = try fixture(root)
    do {
        try await service.installPackage(package)
        #expect(await service.snapshots().first?.visible == true)
        _ = try await service.invoke(package.id, action: "show")
        #expect(await service.snapshots().first?.island != nil)
        #expect(await service.diagnostics().expiry)
        try await service.writeSetting(package.id, key: "show-island", value: "false")
        #expect(try await store.readSettings(package.id)["show-island"] == "false")
        await #expect(throws: (any Error).self) { try await service.clearData(package.id) }
        try await service.disable(package.id)
        #expect(await service.snapshots().first?.visible == false)
        let stopped = await service.diagnostics(); #expect(stopped.workers == 0 && !stopped.expiry)
        await #expect(throws: (any Error).self) { try await service.invoke(package.id, action: "show") }
        try await service.enable(package.id)
        #expect(try await service.invoke(package.id, action: "show").island == nil)
        // The sample binary is compiled for 1.0.0; declaring 1.1.0 causes a real
        // worker handshake failure after switching. The committed old worker must recover.
        let incompatible = try fixture(root, version: "1.1.0")
        await #expect(throws: (any Error).self) { try await service.installPackage(incompatible) }
        let restored = try #require(await service.snapshots().first)
        #expect(restored.visible && restored.installation.version == "1.0.0" && restored.installation.transaction == .none)
        #expect(try await service.invoke(package.id, action: "show").result["messageKey"]?.string == "message")
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("packages/dropspace.sample/1.1.0").path))
        try await service.uninstall(package.id)
        #expect(await service.snapshots().first?.installation.version == nil)
        #expect(try await store.readSettings(package.id)["show-island"] == "false")
        try await service.clearData(package.id)
        #expect(try await store.readSettings(package.id).isEmpty)
        #expect(await service.shutdown())
    } catch { _ = await service.shutdown(); throw error }
}

@Test func nativeModuleRuntimeRecoversInterruptedInstallAndPendingUninstall() async throws {
    let root = try packageRoot(); defer { try? FileManager.default.removeItem(at: root) }
    let store = ModulePackageStore(root: root, budget: .init()), package = try fixture(root)
    _ = try await store.prepare(package, installed: [:])
    let candidate = try fixture(root, version: "1.1.0"); _ = try await store.prepare(candidate, installed: [:])
    try await store.write(.init(id: package.id, version: package.version, enabled: true, dataVersion: 1,
                                transaction: .activating, previousVersion: package.version, candidateVersion: candidate.version))
    let service = try runtime(store)
    do {
        try await service.initialize()
        #expect(await service.snapshots().first?.visible == true)
        #expect(try await store.load().first?.transaction == ModuleTransactionState.none)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("packages/dropspace.sample/1.1.0").path))
        #expect(await service.shutdown())
    } catch { _ = await service.shutdown(); throw error }
    try await store.write(.init(id: package.id, version: package.version, enabled: false, dataVersion: 1,
                                transaction: .cleaning, candidateVersion: package.version))
    let recovering = try runtime(store)
    try await recovering.initialize()
    #expect(await recovering.snapshots().first?.installation.version == nil)
    #expect(await recovering.diagnostics().workers == 0)
    #expect(await recovering.shutdown())
}

@Test func nativeModuleRuntimeKeepsEmptyHostIdleAndRetriesUnsafeCleanup() async throws {
    let parent = try packageRoot(); defer { try? FileManager.default.removeItem(at: parent) }
    let root = parent.appendingPathComponent("modules"), store = ModulePackageStore(root: root, budget: .init()), empty = try runtime(store)
    try await empty.initialize()
    #expect(!FileManager.default.fileExists(atPath: root.path))
    #expect(await empty.snapshots().isEmpty)
    let idle = await empty.diagnostics(); #expect(idle.workers == 0 && !idle.expiry)
    #expect(await empty.shutdown())
    let package = try fixture(root), service = try runtime(store)
    do {
        try await service.installPackage(package)
        let version = try await store.versionDirectory(package.id, version: package.version)
        let external = parent.appendingPathComponent("outside.txt"); try Data("keep".utf8).write(to: external)
        let link = version.appendingPathComponent("unsafe-link.txt")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: external)
        try await service.uninstall(package.id)
        #expect(await service.snapshots().first?.installation.transaction == .pendingCleanup)
        #expect(await service.diagnostics().workers == 0)
        #expect(try String(contentsOf: external, encoding: .utf8) == "keep")
        try FileManager.default.removeItem(at: link)
        try await service.retryCleanup(package.id)
        #expect(await service.snapshots().first?.installation.transaction == ModuleTransactionState.none)
        #expect(!FileManager.default.fileExists(atPath: version.path))
        #expect(await service.shutdown())
    } catch { _ = await service.shutdown(); throw error }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_PREPARE_MODULE_UI"] == "1"))
func prepareNativeModuleUIFixture() async throws {
    // Explicit, disposable acceptance profile. Never touches normal app data or
    // publishes the sample. Refuse overwrite to keep reruns reviewable.
    let profile = packageSources.appendingPathComponent("build/ModuleExperienceSmoke")
    guard !FileManager.default.fileExists(atPath: profile.path) else { throw DropSpaceError.invalid("Validation profile already exists") }
    let root = profile.appendingPathComponent("Modules")
    let package = try fixture(root), store = ModulePackageStore(root: root, budget: .init())
    _ = try await store.prepare(package, installed: [:])
    try await store.write(.init(id: package.id, version: package.version, enabled: true, dataVersion: 1))
    try Data("Owned, disposable module UI acceptance profile. Not a published module.\n".utf8).write(to: profile.appendingPathComponent("VALIDATION-ONLY.txt"))
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_PREPARE_MODULE_INVENTORY"] == "1"))
func prepareNativeModuleInventoryUIFixture() async throws {
    let profile = packageSources.appendingPathComponent("build/ModuleInventorySmoke")
    guard !FileManager.default.fileExists(atPath: profile.path) else { throw DropSpaceError.invalid("Validation profile already exists") }
    let root = profile.appendingPathComponent("Modules")
    let package = try fixture(root, lifecycle: true, id: "dropspace.validation"), store = ModulePackageStore(root: root, budget: .init())
    _ = try await store.prepare(package, installed: [:])
    try await store.write(.init(id: package.id, version: package.version, enabled: true, dataVersion: 1))
    try await store.writeSettings(package.id, values: ["show-island": "false"])
    try Data("Owned inventory acceptance fixture. Not an official or published module.\n".utf8).write(to: profile.appendingPathComponent("VALIDATION-ONLY.txt"))
}
