import Foundation
import Observation
import DropSpaceCore
import DropSpaceInfrastructure

@MainActor @Observable final class NativeModules {
    let runtime: FeatureModuleRuntime
    private(set) var snapshots: [ModuleSnapshot] = []
    private(set) var packages: [OfficialModulePackage] = []
    private(set) var busy = Set<String>()
    private(set) var retiring = Set<String>()
    var error: String?
    var onChange: (() -> Void)?
    private var observation: Task<Void, Never>?
    private var jobs: [UUID: Task<Void, Never>] = [:]
    init(directory: URL, budget: DownloadBudget) throws {
        let store = ModulePackageStore(root: directory, budget: budget)
        let catalog = try OfficialModuleCatalog(budget: budget)
        runtime = FeatureModuleRuntime(store: store, catalog: catalog,
            guardian: Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/module-guardian"))
    }
    func start() async {
        observation = Task { [weak self, runtime] in
            for await values in await runtime.updates() {
                guard let self, !Task.isCancelled else { return }
                snapshots = values; onChange?()
            }
        }
        do { try await runtime.initialize(); packages = await runtime.availablePackages() }
        catch { self.error = error.localizedDescription }
    }
    var active: [ModuleSnapshot] { snapshots.filter(\.visible) }
    var contributions: [ModuleSnapshot] { active.filter { $0.island != nil } }
    func refresh() { perform("catalog") { runtime in try await runtime.refreshCatalog() } }
    func install(_ id: String) { perform(id) { try await $0.install(id) } }
    func enable(_ id: String) { perform(id) { try await $0.enable(id) } }
    func disable(_ id: String) { perform(id, retire: true) { try await $0.disable(id) } }
    func uninstall(_ id: String) { perform(id, retire: true) { try await $0.uninstall(id) } }
    func cleanup(_ id: String) { perform(id) { try await $0.retryCleanup(id) } }
    func clearData(_ id: String) { perform(id) { try await $0.clearData(id) } }
    private func perform(_ id: String, retire: Bool = false, _ operation: @escaping @Sendable (FeatureModuleRuntime) async throws -> Void) {
        guard retire ? !retiring.contains(id) : busy.isEmpty else { return }
        if retire { retiring.insert(id) } else { busy.insert(id) }
        let token = UUID(); error = nil
        jobs[token] = Task { [weak self, runtime] in
            guard let self else { return }
            defer { jobs[token] = nil; if retire { retiring.remove(id) } else { busy.remove(id) } }
            do { try await operation(runtime) } catch is CancellationError { } catch { self.error = error.localizedDescription }
            packages = await runtime.availablePackages()
        }
    }
    func shutdown() async -> Bool {
        observation?.cancel(); observation = nil
        let owned = Array(jobs.values); for job in owned { job.cancel() }
        let stopped = await runtime.shutdown(); for job in owned { await job.value }; return stopped
    }
}

extension WorkspaceModel {
    var selectedModule: (manifest: ModuleManifest, page: ModulePage?)? {
        guard section.hasPrefix("module:"), let modules else { return nil }
        let parts = section.dropFirst(7).split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 2, let snapshot = modules.active.first(where: { $0.installation.id == parts[0] }), let manifest = snapshot.manifest else { return nil }
        if parts[1] == "@settings" { return (manifest, nil) }
        guard let page = manifest.ui.pages.first(where: { $0.id == parts[1] }) else { return nil }; return (manifest, page)
    }
    func selectModule(_ manifest: ModuleManifest, page: ModulePage?) {
        if !section.hasPrefix("module:") { moduleReturnSection = section }
        section = "module:" + manifest.id + "/" + (page?.id ?? "@settings"); openMainWindow?()
    }
    func closeModule() { section = moduleReturnSection }
}
