import Foundation
import Observation
import DropSpaceCore
import DropSpaceInfrastructure

@MainActor @Observable public final class NativeAIModels {
    public let store: AIModelStore
    public let catalog: [AIModelDescriptor]
    public private(set) var inventory: [String: AIModelInspection] = [:]
    public private(set) var progress: [String: AIModelProgress] = [:]
    public private(set) var failures: [String: String] = [:]
    public private(set) var running = Set<String>()
    public var beforeChange: (@MainActor () async -> Void)?
    public var afterChange: (@MainActor () -> Void)?
    private var jobs: [String: Task<Void, Never>] = [:]
    private var generations: [String: UUID] = [:]
    public init(directory: URL, budget: DownloadBudget) throws {
        store = try AIModelStore(directory: directory, budget: budget); catalog = store.catalog.models
    }
    public func refresh() async {
        for model in catalog { await refresh(model.id) }
    }
    private func refresh(_ id: String) async {
        do { inventory[id] = try await store.inspect(id) }
        catch { failures[id] = error.localizedDescription }
    }
    public func install(_ id: String) {
        guard jobs[id] == nil else { return }
        let generation = UUID(); generations[id] = generation; failures[id] = nil; running.insert(id)
        jobs[id] = Task { [weak self] in
            guard let self else { return }
            defer { jobs[id] = nil; running.remove(id); afterChange?() }
            do {
                try await store.install(id, consent: true) { [weak self] value in await self?.receive(value, id: id, generation: generation) }
            } catch is CancellationError { }
            catch { failures[id] = error.localizedDescription }
            await refresh(id)
        }
    }
    private func receive(_ value: AIModelProgress, id: String, generation: UUID) {
        guard generations[id] == generation else { return }; progress[id] = value
    }
    public func cancel(_ id: String) async {
        guard let job = jobs[id] else { return }
        generations[id] = UUID(); job.cancel(); await job.value; progress[id] = nil
    }
    public func setEnabled(_ id: String, _ enabled: Bool) async {
        await beforeChange?()
        do { try await store.setEnabled(id, enabled); failures[id] = nil }
        catch { failures[id] = error.localizedDescription }
        await refresh(id); afterChange?()
    }
    public func remove(_ id: String) async {
        await cancel(id); await beforeChange?()
        do { try await store.remove(id); failures[id] = nil; progress[id] = nil }
        catch { failures[id] = error.localizedDescription }
        await refresh(id); afterChange?()
    }
    public func retryCleanup() async { await store.retryPendingCleanup(); await refresh() }
    public func shutdown() async {
        let tasks = Array(jobs.values); generations.removeAll()
        for task in tasks { task.cancel() }; for task in tasks { await task.value }
        jobs.removeAll(); running.removeAll()
    }
}
