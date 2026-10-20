import Foundation
import DropSpaceCore

public struct ModuleSnapshot: Sendable {
    public let installation: ModuleInstallation
    public let state: ModuleRunState
    public let manifest: ModuleManifest?
    public let island: ModuleIslandContent?
    public var visible: Bool { installation.enabled && state == .running && installation.version == manifest?.version }
}

/// Application service: owns generations, transactions and workers. Disk IO and
/// protocol IO remain in separate actors; views only consume validated snapshots.
public actor FeatureModuleRuntime {
    // Only touched on this actor, including operation tasks inheriting isolation.
    private final class Slot {
        var installation: ModuleInstallation
        var manifest: ModuleManifest?
        var worker: ModuleWorkerClient?
        var workerID: UUID?
        var state: ModuleRunState = .stopped
        var generation = 0
        var calls: [UUID: @Sendable () -> Void] = [:]
        var island: ModuleIslandContent?
        let gate = DownloadPermitPool(limit: 1)
        init(_ installation: ModuleInstallation) { self.installation = installation }
    }
    private let store: ModulePackageStore, catalog: OfficialModuleCatalog, guardian: URL
    private let systemVersion: OperatingSystemVersion
    private var slots: [String: Slot] = [:]
    private var packages: [OfficialModulePackage] = []
    private var initialization: Task<Void, any Error>?
    private var startupRetirements = Set<String>()
    private var disposed = false
    private var expiry: Task<Void, Never>?
    private var operations: [UUID: Task<Void, any Error>] = [:]
    private var observers: [UUID: AsyncStream<[ModuleSnapshot]>.Continuation] = [:]
    public init(store: ModulePackageStore, catalog: OfficialModuleCatalog, guardian: URL,
                systemVersion: OperatingSystemVersion = ProcessInfo.processInfo.operatingSystemVersion) {
        self.store = store; self.catalog = catalog; self.guardian = guardian; self.systemVersion = systemVersion
    }
    public func snapshots() -> [ModuleSnapshot] {
        let installed = slots.values.map { slot in ModuleSnapshot(installation: slot.installation, state: slot.state,
            manifest: slot.manifest, island: slot.island.flatMap { $0.expiresAt > Date() ? $0 : nil }) }
        return (installed + packages.filter { slots[$0.id] == nil }.map {
            ModuleSnapshot(installation: .init(id: $0.id, enabled: false, dataVersion: 0), state: .stopped, manifest: nil, island: nil)
        }).sorted { $0.installation.id < $1.installation.id }
    }
    public func availablePackages() -> [OfficialModulePackage] { packages }
    public func updates() -> AsyncStream<[ModuleSnapshot]> {
        let id = UUID(), pair = AsyncStream<[ModuleSnapshot]>.makeStream(bufferingPolicy: .bufferingNewest(1))
        guard !disposed else { pair.continuation.finish(); return pair.stream }
        observers[id] = pair.continuation; pair.continuation.yield(snapshots())
        pair.continuation.onTermination = { [weak self] _ in Task { await self?.removeObserver(id) } }
        return pair.stream
    }
    private func removeObserver(_ id: UUID) { observers[id] = nil }
    private func notify() { let current = snapshots(); for observer in observers.values { observer.yield(current) } }
    public func initialize() async throws {
        guard !disposed else { throw CancellationError() }
        if initialization == nil { initialization = Task { try await recover() } }
        try await initialization!.value
    }
    public func refreshCatalog() async throws {
        try await initialize(); try await catalog.refresh(); try requireAlive()
        packages = await catalog.packages; notify()
    }
    private func requireAlive() throws { try Task.checkCancellation(); if disposed { throw CancellationError() } }
    private func slot(_ id: String) throws -> Slot {
        try requireAlive(); guard ModuleContract.validID(id) else { throw DropSpaceError.invalid("ModuleNotOfficial") }
        if let slot = slots[id] { return slot }
        guard packages.contains(where: { $0.id == id }) else { throw DropSpaceError.invalid("ModuleNotOfficial") }
        let result = Slot(.init(id: id, enabled: false, dataVersion: 0)); slots[id] = result; return result
    }
    private func current(_ slot: Slot, _ generation: Int) throws {
        try requireAlive(); guard slot.generation == generation else { throw CancellationError() }
    }
    @discardableResult private func retire(_ slot: Slot) -> Int {
        slot.generation += 1; slot.island = nil
        if slot.state == .running || slot.state == .starting { slot.state = .stopping }
        for cancel in slot.calls.values { cancel() }; scheduleExpiry(); notify(); return slot.generation
    }
    private func call<T: Sendable>(_ slot: Slot, generation: Int, _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try current(slot, generation)
        let id = UUID(), task = Task { try await operation() }
        slot.calls[id] = { task.cancel() }; defer { slot.calls[id] = nil }
        let value = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        try current(slot, generation); return value
    }
    private func run(_ id: String, _ operation: @escaping @Sendable (isolated FeatureModuleRuntime) async throws -> Void) async throws {
        let target = try slot(id), token = UUID(), gate = target.gate
        let task = Task {
            let permit = try await gate.acquire()
            do {
                try requireAlive(); try await operation(self); await gate.release(permit)
            } catch {
                target.installation.errorCode = Self.errorCode(error); notify(); await gate.release(permit); throw error
            }
        }
        operations[token] = task; defer { operations[token] = nil }
        try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    private func publish(_ slot: Slot, _ value: ModuleInstallation) async throws {
        try await store.write(value); slot.installation = value; notify()
    }
    private func removeVersion(_ id: String, _ version: String?) async throws -> Bool {
        guard let version else { return true }; return try await store.removeVersion(id, version: version)
    }
    private func verifiedInstallations() -> [String: ModuleInstallation] {
        Dictionary(uniqueKeysWithValues: slots.values.filter {
            $0.manifest != nil && $0.manifest?.version == $0.installation.version && !Self.pendingUninstall($0.installation)
        }.map { ($0.installation.id, $0.installation) })
    }
    private func validate(_ manifest: ModuleManifest, dataVersion: Int, installed: [String: ModuleInstallation]? = nil) throws {
        try ModuleContract.validate(manifest, installed: installed ?? verifiedInstallations(), systemVersion: systemVersion)
        guard dataVersion == 0 || dataVersion == manifest.data.version else { throw DropSpaceError.invalid("ModuleDataMigrationUnsupported") }
    }
    static func pendingUninstall(_ value: ModuleInstallation) -> Bool {
        [.cleaning, .pendingCleanup].contains(value.transaction) && !value.enabled && value.version != nil && value.candidateVersion == value.version
    }
    private static func completedCleanup(_ entry: ModuleInstallation) -> ModuleInstallation {
        var value = entry
        if pendingUninstall(value) { value.version = nil }
        value.transaction = .none; value.candidateVersion = nil; value.previousVersion = nil; value.errorCode = nil; return value
    }
    private func recover() async throws {
        packages = await catalog.packages
        let entries = try await store.load(); try requireAlive()
        for entry in entries { slots[entry.id] = Slot(entry) }
        defer { startupRetirements.removeAll() }
        // All packages are verified before dependency resolution. A state file
        // alone is never evidence that another module's binary exists.
        for entry in entries {
            let slot = slots[entry.id]!
            do {
                var value = entry
                if [.downloading, .preparing, .activating].contains(value.transaction) {
                    let clean = try await removeVersion(value.id, value.candidateVersion == value.version ? nil : value.candidateVersion)
                    if clean { value.transaction = .none; value.candidateVersion = nil; value.previousVersion = nil; value.errorCode = "ModuleInterrupted" }
                    else { value.transaction = .pendingCleanup; value.errorCode = "ModuleCleanupPending" }
                    try await publish(slot, value)
                }
                if [.cleaning, .pendingCleanup].contains(value.transaction) {
                    if value.candidateVersion == value.version, value.version != nil, !Self.pendingUninstall(value) { throw DropSpaceError.invalid("ModuleStateInvalid") }
                    if try await removeVersion(value.id, value.candidateVersion) { value = Self.completedCleanup(value) }
                    else { value.transaction = .pendingCleanup; value.errorCode = "ModuleCleanupPending" }
                    try await publish(slot, value)
                }
                if try await !store.removeStaging(value.id) {
                    value.transaction = .pendingCleanup; value.errorCode = "ModuleCleanupPending"; try await publish(slot, value)
                }
                if let version = value.version, !Self.pendingUninstall(value) { slot.manifest = try await store.readManifest(value.id, version: version) }
            } catch { slot.state = .faulted; slot.installation.errorCode = Self.errorCode(error) }
        }
        var verified = verifiedInstallations(), changed = true
        while changed {
            changed = false
            for entry in entries where verified[entry.id] != nil {
                let slot = slots[entry.id]!
                do { try validate(slot.manifest!, dataVersion: slot.installation.dataVersion, installed: verified) }
                catch { verified[entry.id] = nil; slot.manifest = nil; slot.state = .faulted; slot.installation.errorCode = Self.errorCode(error); changed = true }
            }
        }
        for entry in entries {
            let slot = slots[entry.id]!
            guard slot.state != .faulted, let manifest = slot.manifest, slot.installation.enabled, !startupRetirements.contains(entry.id) else { continue }
            do { try await activate(slot, manifest: manifest, generation: slot.generation) }
            catch { slot.state = .faulted; slot.installation.errorCode = Self.errorCode(error) }
        }
        notify()
    }
    public func install(_ id: String) async throws {
        try await initialize()
        guard let package = packages.first(where: { $0.id == id }) else { throw DropSpaceError.unavailable("ModuleCatalogUnavailable") }
        try await installPackage(package)
    }
    // Internal so deterministic tests can exercise the actual transaction with
    // preverified local ZIP bytes without adding the sample to the public catalog.
    func installPackage(_ package: OfficialModulePackage) async throws {
        try await initialize(); try ModuleCatalogRules.require(package)
        if slots[package.id] == nil { slots[package.id] = Slot(.init(id: package.id, enabled: false, dataVersion: 0)) }
        try await run(package.id) { owner in try await owner.installCore(package) }
    }
    private func installCore(_ package: OfficialModulePackage) async throws {
        let slot = try slot(package.id), old = slot.installation, previousManifest = slot.manifest
        if let previous = old.version, ModuleContract.version(package.version)!.lexicographicallyPrecedes(ModuleContract.version(previous)!) { throw DropSpaceError.invalid("ModuleDowngradeRejected") }
        if old.version == package.version, old.transaction == .none { try await enableCore(slot); return }
        guard ![.cleaning, .pendingCleanup].contains(old.transaction) else { throw DropSpaceError.unavailable("ModuleCleanupPending") }
        var generation = slot.generation, switched = false, candidate: ModuleManifest?
        var transaction = old; transaction.transaction = .downloading; transaction.candidateVersion = package.version
        transaction.previousVersion = old.version; transaction.errorCode = nil
        try await publish(slot, transaction)
        do {
            let store = store, installed = verifiedInstallations()
            let manifest = try await call(slot, generation: generation) { try await store.prepare(package, installed: installed) }
            try validate(manifest, dataVersion: old.dataVersion); candidate = manifest; try current(slot, generation)
            generation = retire(slot); switched = true
            guard await stop(slot) else { throw DropSpaceError.unavailable("ModuleStopUnconfirmed") }
            transaction.transaction = .activating; transaction.dataVersion = manifest.data.version
            try await publish(slot, transaction); try current(slot, generation)
            try await activate(slot, manifest: manifest, generation: generation); try current(slot, generation)
            try await publish(slot, .init(id: package.id, version: package.version, enabled: true, dataVersion: manifest.data.version,
                                         transaction: .cleaning, candidateVersion: old.version == package.version ? nil : old.version))
            slot.manifest = manifest; notify()
        } catch {
            // Recovery must run even when the request that owned the download was
            // canceled. Generation checks still prevent reactivating retired UI.
            let failure = error, superseded = slot.generation != generation || disposed
            let rollback = Task {
                if switched { retire(slot) }
                let stopped = switched ? await stop(slot) : true
                let clean = stopped ? try await store.removeVersion(package.id, version: package.version) : false
                let staging = try await store.removeStaging(package.id)
                var restored = old; restored.errorCode = Self.errorCode(failure)
                restored.transaction = clean && staging ? .none : .pendingCleanup; restored.candidateVersion = clean ? nil : package.version
                if switched { restored.dataVersion = max(old.dataVersion, candidate?.data.version ?? 0) }
                try await publish(slot, restored); slot.manifest = previousManifest
                if switched, let version = old.version, old.enabled, stopped, !superseded {
                    let manifest = try await store.readManifest(package.id, version: version)
                    try validate(manifest, dataVersion: old.dataVersion); try await activate(slot, manifest: manifest, generation: slot.generation)
                }
            }
            try await rollback.value; throw failure
        }
        // Commit has succeeded. A cleanup error must never roll back usable code.
        var cleanup = slot.installation
        let clean = try await removeVersion(package.id, cleanup.candidateVersion)
        let staging = try await store.removeStaging(package.id)
        if clean && staging { cleanup = Self.completedCleanup(cleanup) }
        else { cleanup.transaction = .pendingCleanup; cleanup.errorCode = "ModuleCleanupPending" }
        try await publish(slot, cleanup)
    }
    public func enable(_ id: String) async throws {
        try await initialize(); try await run(id) { owner in try await owner.enableCore(owner.slot(id)) }
    }
    private func enableCore(_ slot: Slot) async throws {
        guard let version = slot.installation.version else { throw DropSpaceError.unavailable("ModuleNotInstalled") }
        if slot.state == .running && slot.installation.enabled { return }
        guard !Self.pendingUninstall(slot.installation), [.none, .pendingCleanup].contains(slot.installation.transaction) else { throw DropSpaceError.unavailable("ModuleCleanupPending") }
        let generation = retire(slot)
        guard await stop(slot) else { throw DropSpaceError.unavailable("ModuleStopUnconfirmed") }
        let manifest = try await store.readManifest(slot.installation.id, version: version)
        try validate(manifest, dataVersion: slot.installation.dataVersion)
        try await activate(slot, manifest: manifest, generation: generation)
        var value = slot.installation; value.enabled = true; value.errorCode = nil
        do { try current(slot, generation); try await publish(slot, value) }
        catch { retire(slot); _ = await stop(slot); throw error }
    }
    public func disable(_ id: String) async throws {
        startupRetirements.insert(id); if let existing = slots[id] { retire(existing) }
        defer { startupRetirements.remove(id) }
        try await initialize(); let target = try slot(id); retire(target)
        try await run(id) { owner in
            let target = try owner.slot(id)
            var value = target.installation; value.enabled = false
            // Even a persistence failure cannot leave a withdrawn worker running.
            do { try await owner.publish(target, value) }
            catch { _ = await owner.stop(target); throw error }
            guard await owner.stop(target) else { throw DropSpaceError.unavailable("ModuleStopUnconfirmed") }
        }
    }
    public func uninstall(_ id: String) async throws {
        try await initialize(); try requireUnused(id); retire(try slot(id))
        try await run(id) { owner in
            try owner.requireUnused(id); let slot = try owner.slot(id)
            var value = slot.installation; value.enabled = false
            do { try await owner.publish(slot, value) }
            catch { _ = await owner.stop(slot); throw error }
            guard await owner.stop(slot) else { throw DropSpaceError.unavailable("ModuleStopUnconfirmed") }
            if let prior = value.candidateVersion, prior != value.version, try await !owner.store.removeVersion(id, version: prior) { throw DropSpaceError.unavailable("ModuleCleanupPending") }
            let version = value.version; value.transaction = .cleaning; value.candidateVersion = version
            try await owner.publish(slot, value)
            let clean = try await owner.removeVersion(id, version)
            let staging = try await owner.store.removeStaging(id)
            value.version = nil; value.transaction = clean && staging ? .none : .pendingCleanup
            value.candidateVersion = clean ? nil : version; value.errorCode = clean && staging ? nil : "ModuleCleanupPending"
            try await owner.publish(slot, value); if clean { slot.manifest = nil }; owner.notify()
        }
    }
    private func requireUnused(_ id: String) throws {
        guard !slots.values.contains(where: { $0.installation.enabled && $0.manifest?.dependencies.contains(where: { $0.id == id }) == true }) else { throw DropSpaceError.unavailable("ModuleDependencyInUse") }
    }
    private func activate(_ slot: Slot, manifest: ModuleManifest, generation: Int) async throws {
        try current(slot, generation); slot.state = .starting; slot.manifest = manifest; notify()
        let id = manifest.id, identity = UUID()
        let executable = try await store.versionDirectory(id, version: manifest.version).appendingPathComponent(manifest.entryPoint)
        let data = try await store.dataDirectory(id); try current(slot, generation)
        let worker = ModuleWorkerClient(guardian: guardian, executable: executable, dataDirectory: data, manifest: manifest,
            onExit: { [weak self] in Task { await self?.exited(id, identity: identity, generation: generation) } })
        slot.worker = worker; slot.workerID = identity
        do {
            try await call(slot, generation: generation) { try await worker.start() }
            let saved = try await store.readSettings(id)
            let settings = try Self.settings(manifest, saved: saved)
            if !settings.isEmpty { _ = try await call(slot, generation: generation) { try await worker.request("settings", payload: Self.payload(settings)) } }
            try current(slot, generation)
            guard await worker.processState().running else { throw DropSpaceError.unavailable("ModuleCrashed") }
            try current(slot, generation); slot.state = .running; notify()
        } catch { _ = await stop(slot); throw error }
    }
    private func exited(_ id: String, identity: UUID, generation: Int) {
        guard !disposed, let slot = slots[id], slot.workerID == identity, slot.generation == generation,
              slot.state == .running || slot.state == .starting else { return }
        slot.state = .faulted; slot.island = nil; slot.installation.errorCode = "ModuleCrashed"; scheduleExpiry(); notify()
    }
    private func stop(_ slot: Slot) async -> Bool {
        slot.island = nil; scheduleExpiry()
        if let worker = slot.worker {
            slot.state = .stopping; notify()
            guard await worker.stop() else { slot.state = .faulted; notify(); return false }
        }
        slot.worker = nil; slot.workerID = nil; slot.state = .stopped; notify(); return true
    }
    public func invoke(_ id: String, action: String, input: ModuleJSON = .object([])) async throws -> ModuleReply {
        try await initialize(); let slot = try slot(id)
        guard slot.installation.enabled, slot.state == .running, let worker = slot.worker, let manifest = slot.manifest,
              slot.installation.version == manifest.version,
              manifest.ui.pages.flatMap(\.actions).contains(where: { $0.id == action }) else { throw DropSpaceError.unavailable("ModuleActionUnavailable") }
        let generation = slot.generation
        let reply = try await call(slot, generation: generation) { try await worker.request("action", payload: .object([.init("action", .string(action)), .init("input", input)])) }
        guard slot.state == .running else { throw CancellationError() }
        if let island = reply.island { try ModuleContract.validateIsland(island, manifest: manifest, now: Date()); slot.island = island; scheduleExpiry() }
        notify(); return reply
    }
    public func readSettings(_ id: String) async throws -> [String: String] { try await store.readSettings(id) }
    public func writeSetting(_ id: String, key: String, value: String) async throws {
        try await initialize()
        try await run(id) { owner in
            let slot = try owner.slot(id)
            guard slot.state == .running, slot.installation.enabled, let worker = slot.worker, let manifest = slot.manifest,
                  let descriptor = manifest.ui.settings.first(where: { $0.id == key }), value.utf16.count <= 4096,
                  descriptor.kind != "boolean" || ["true", "false"].contains(value) else { throw DropSpaceError.invalid("ModuleSettingInvalid") }
            let generation = slot.generation
            var values = try Self.settings(manifest, saved: await owner.store.readSettings(id)); values[key] = value
            let payload = Self.payload(values)
            _ = try await owner.call(slot, generation: generation) { try await worker.request("settings", payload: payload) }
            do { try owner.current(slot, generation); try await owner.store.writeSettings(id, values: values) }
            catch { owner.retire(slot); _ = await owner.stop(slot); throw error }
        }
    }
    private static func settings(_ manifest: ModuleManifest, saved: [String: String]) throws -> [String: String] {
        var values: [String: String] = [:]
        for descriptor in manifest.ui.settings {
            let value = saved[descriptor.id] ?? descriptor.defaultValue
            guard value.utf16.count <= 4096, descriptor.kind != "boolean" || ["true", "false"].contains(value) else { throw DropSpaceError.invalid("ModuleSettingInvalid") }
            values[descriptor.id] = value
        }
        return values
    }
    private static func payload(_ values: [String: String]) -> ModuleJSON { .object(values.sorted { $0.key < $1.key }.map { .init($0.key, .string($0.value)) }) }
    public func clearData(_ id: String) async throws {
        try await initialize()
        try await run(id) { owner in
            let slot = try owner.slot(id)
            guard !slot.installation.enabled, slot.worker == nil else { throw DropSpaceError.unavailable("ModuleDisableBeforeDataRemoval") }
            try await owner.store.clearData(id)
            var value = slot.installation; value.dataVersion = slot.manifest?.data.version ?? 0; value.errorCode = nil
            try await owner.publish(slot, value)
        }
    }
    public func retryCleanup(_ id: String) async throws {
        try await initialize()
        try await run(id) { owner in
            let slot = try owner.slot(id), value = slot.installation
            guard value.transaction == .pendingCleanup else { return }
            let version = value.candidateVersion
            if version == value.version, version != nil, !Self.pendingUninstall(value) { throw DropSpaceError.invalid("ModuleStateInvalid") }
            let running = slot.state == .running && value.version != nil && value.version != version
            if !running, await !owner.stop(slot) { throw DropSpaceError.unavailable("ModuleStopUnconfirmed") }
            if let version, try await !owner.store.removeVersion(id, version: version) { throw DropSpaceError.unavailable("ModuleCleanupPending") }
            guard try await owner.store.removeStaging(id) else { throw DropSpaceError.unavailable("ModuleCleanupPending") }
            let completed = Self.completedCleanup(value); try await owner.publish(slot, completed)
            if completed.version == nil { slot.manifest = nil }
            if !running, completed.enabled, completed.version != nil { try await owner.enableCore(slot) }; owner.notify()
        }
    }
    private func scheduleExpiry() {
        expiry?.cancel(); expiry = nil
        guard !disposed, let deadline = slots.values.compactMap({ $0.island?.expiresAt }).min() else { return }
        expiry = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(max(0.001, deadline.timeIntervalSinceNow))); await self?.expire() } catch {}
        }
    }
    private func expire() {
        for slot in slots.values where (slot.island?.expiresAt ?? .distantFuture) <= Date() { slot.island = nil }
        scheduleExpiry(); notify()
    }
    public func shutdown() async -> Bool {
        disposed = true; expiry?.cancel(); expiry = nil
        for slot in slots.values { retire(slot) }
        initialization?.cancel(); for operation in operations.values { operation.cancel() }
        _ = try? await initialization?.value
        let pending = Array(operations.values); for task in pending { _ = try? await task.value }
        var confirmed = true
        for slot in slots.values { if await !stop(slot) { confirmed = false } }
        await catalog.stop(); for observer in observers.values { observer.finish() }; observers.removeAll()
        return confirmed
    }
    public func diagnostics() -> (workers: Int, calls: Int, expiry: Bool) {
        (slots.values.filter { $0.worker != nil }.count, slots.values.reduce(0) { $0 + $1.calls.count }, expiry != nil)
    }
    private static func errorCode(_ error: any Error) -> String {
        let text = error.localizedDescription
        return text.utf16.count <= 256 && ["Module", "HostInterfaceRequired", "RequiredCapabilityMissing:", "DependencyUnavailable:"].contains(where: text.hasPrefix) ? text : "ModuleOperationFailed"
    }
}
