import Foundation
import CryptoKit
import Darwin
import DropSpaceCore

public struct AIModelInspection: Sendable {
    public var installed: Bool
    public var artifactBytes: Int64
    public var enabled: Bool
    public var pendingCleanup: Bool
}
public struct AIModelProgress: Sendable {
    public enum Phase: String, Sendable { case checking, downloading, verifying, merging, publishing, completed }
    public var phase: Phase
    public var received: Int64
    public var total: Int64
}
public struct AIModelLease: Sendable {
    public let id: UUID
    public let model: AIModelDescriptor
    public let url: URL
}

/// Frozen models are immutable packages. Publication is atomic; partials survive
/// cancellation, and a consumer lease prevents deletion until worker exit.
public actor AIModelStore {
    public nonisolated let catalog: AIModelCatalog
    private let root: URL
    private let downloader: HTTPDownloader
    private let gate = DownloadPermitPool(limit: 1)
    private var leases: [UUID: String] = [:]
    private struct State: Codable {
        var disabled: Set<String> = []
        var pendingRemoval: Set<String> = []
        var pendingFragments: Set<String>?
    }
    private var state: State
    public init(directory: URL, budget: DownloadBudget) throws {
        catalog = try AIModelCatalog(); root = directory; downloader = HTTPDownloader(budget: budget)
        try Self.validateRoot(directory)
        let file = directory.appendingPathComponent("models-state.json")
        if let bytes = try Self.readSmall(file, maximum: 16384) { state = try JSONDecoder().decode(State.self, from: bytes) }
        else { state = State() }
    }
    public func inspect(_ id: String) async throws -> AIModelInspection {
        // Inventory may observe a partial download. It never grants execution
        // trust, and must remain responsive while a long transfer owns the gate.
        let model = try catalog.model(id); try Self.validateRoot(root)
        var bytes: Int64 = 0
        for url in owned(model) {
            let sum = bytes.addingReportingOverflow(try Self.size(url) ?? 0)
            guard !sum.overflow else { throw DropSpaceError.limit("Model inventory size overflow") }; bytes = sum.partialValue
        }
        for url in [assembly(model)] + model.parts.map({ partURL(model, $0) }) {
            let files = try DownloadFiles(staging: url)
            let sum = bytes.addingReportingOverflow(try files.rangeBytes())
            guard !sum.overflow else { throw DropSpaceError.limit("Model inventory size overflow") }; bytes = sum.partialValue
        }
        let licensesPresent = try catalog.licenses(model).allSatisfy { try Self.size(license($0)) == $0.bytes }
        let installed = try Self.size(final(model)) == model.fullsize && licensesPresent && !state.pendingRemoval.contains(id)
        return AIModelInspection(installed: installed, artifactBytes: bytes, enabled: !state.disabled.contains(id), pendingCleanup: state.pendingRemoval.contains(id) || state.pendingFragments?.contains(id) == true)
    }
    public func setEnabled(_ id: String, _ enabled: Bool) async throws {
        let permit = try await gate.acquire()
        do {
            _ = try catalog.model(id)
            guard !leases.values.contains(id) else { throw DropSpaceError.unavailable("Stop translation before changing this component") }
            var next = state
            if enabled { next.disabled.remove(id) } else { next.disabled.insert(id) }
            try save(next); await gate.release(permit)
        } catch { await gate.release(permit); throw error }
    }
    public func lease(_ id: String) async throws -> AIModelLease {
        try await acquireLease(id, verifyModel: true)
    }
    /// Only the pinned worker may use this reservation: it verifies the complete
    /// model on one held descriptor before parsing any GGUF data. Avoid two hash
    /// reads that would charge/activate the same file pages twice at startup.
    func leaseForNativeVerification(_ id: String) async throws -> AIModelLease {
        try await acquireLease(id, verifyModel: false)
    }
    private func acquireLease(_ id: String, verifyModel: Bool) async throws -> AIModelLease {
        let permit = try await gate.acquire()
        do {
            let model = try catalog.model(id)
            guard !state.disabled.contains(id), !state.pendingRemoval.contains(id) else { throw DropSpaceError.unavailable("Translation component is disabled or awaiting cleanup") }
            try Self.validateRoot(root)
            guard try Self.size(final(model)) == model.fullsize else { throw DropSpaceError.unavailable("Translation model is missing or has an unexpected size") }
            if verifyModel, try await !verify(final(model), bytes: model.fullsize, hash: model.fullsha) { throw DropSpaceError.unavailable("Translation model failed integrity verification") }
            for asset in catalog.licenses(model) {
                guard try await verify(license(asset), bytes: asset.bytes, hash: asset.sha256) else { throw DropSpaceError.unavailable("Translation model license is missing or invalid") }
            }
            try Task.checkCancellation()
            let value = AIModelLease(id: UUID(), model: model, url: final(model)); leases[value.id] = id
            await gate.release(permit); return value
        } catch { await gate.release(permit); throw error }
    }
    public func release(_ lease: AIModelLease) { leases.removeValue(forKey: lease.id) }

    public func install(_ id: String, consent: Bool, progress: @escaping @Sendable (AIModelProgress) async -> Void = { _ in }) async throws {
        guard consent else { throw DropSpaceError.invalid("Explicit model download consent is required") }
        let model = try catalog.model(id), permit = try await gate.acquire()
        do {
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask { try await self.installOwned(model, progress: progress) }
                group.addTask { try await Task.sleep(for: model.downloadDeadline); throw DropSpaceError.unavailable("Model download deadline exceeded") }
                defer { group.cancelAll() }; _ = try await group.next()
            }
            await gate.release(permit)
        } catch { await gate.release(permit); throw error }
    }
    private func installOwned(_ model: AIModelDescriptor, progress: @escaping @Sendable (AIModelProgress) async -> Void) async throws {
        try Self.validateRoot(root)
        guard !state.pendingRemoval.contains(model.id) else { throw DropSpaceError.unavailable("Finish pending model cleanup before installation") }
        await progress(.init(phase: .checking, received: 0, total: model.fullsize))
        var valid = try await verify(final(model), bytes: model.fullsize, hash: model.fullsha)
        if !valid {
            guard !leases.values.contains(model.id) else { throw DropSpaceError.unavailable("Translation worker still owns this model") }
            if try await verify(assembly(model), bytes: model.fullsize, hash: model.fullsha) {
                try publish(assembly(model), to: final(model)); valid = true
            }
        }
        if !valid {
            var verified = Set<String>(), completed: Int64 = 0, retained: Int64 = 0
            for part in model.parts {
                let url = partURL(model, part)
                let size = try Self.size(url) ?? 0
                if try await verify(url, bytes: part.bytes, hash: part.sha256) { verified.insert(part.name); completed += part.bytes; retained += part.bytes }
                else if size >= part.bytes { try deleteOwned(url); try deleteOwned(url.appendingPathExtension("resume.json")) }
                else { retained += size }
            }
            try deleteOwned(assembly(model))
            var disk = statfs()
            guard statfs(root.path, &disk) == 0 else { throw Self.posix() }
            let needed = max(0, model.fullsize * 2 - retained) + 64 * 1024 * 1024
            guard UInt64(disk.f_bavail) * UInt64(disk.f_bsize) >= UInt64(needed) else { throw DropSpaceError.storage("Insufficient space for model fragments and assembly") }
            for part in model.parts where !verified.contains(part.name) {
                let offset = completed
                try await downloader.download(source: part.url, staging: partURL(model, part), expectedBytes: part.bytes, sha256: part.sha256, maximumBytes: part.bytes, trusted: AIModelCatalog.trusted, progress: { received, _ in
                    await progress(.init(phase: received == part.bytes ? .verifying : .downloading, received: offset + received, total: model.fullsize))
                })
                completed += part.bytes
                // A complete, hash-verified part no longer needs resumable range
                // copies before the full-model assembly reserves its own space.
                try DownloadStaging.cleanSidecars(partURL(model, part))
            }
            await progress(.init(phase: .merging, received: completed, total: model.fullsize))
            let sourceFiles = model.parts.map { partURL(model, $0) }, assembly = assembly(model)
            if sourceFiles.count == 1 { try publish(sourceFiles[0], to: assembly) }
            else { try await diskTask { try Self.assemble(sourceFiles, output: assembly, bytes: model.fullsize, hash: model.fullsha) } }
            try Task.checkCancellation()
            await progress(.init(phase: .publishing, received: completed, total: model.fullsize))
            try publish(assembly, to: final(model))
        }
        for asset in catalog.licenses(model) {
            if try await !verify(license(asset), bytes: asset.bytes, hash: asset.sha256) {
                try await downloader.download(source: asset.url, staging: license(asset), expectedBytes: asset.bytes, sha256: asset.sha256, maximumBytes: asset.bytes, trusted: AIModelCatalog.trusted)
            }
        }
        try Task.checkCancellation()
        // Persist the cleanup responsibility before deleting retained fragments.
        // A valid model remains usable if cleanup fails or the app exits midway.
        var next = state; next.pendingFragments = (next.pendingFragments ?? []).union([model.id]); try save(next)
        try? clearFragments(model)
        await progress(.init(phase: .completed, received: model.fullsize, total: model.fullsize))
    }
    public func remove(_ id: String) async throws {
        let permit = try await gate.acquire()
        do {
            let model = try catalog.model(id)
            guard !leases.values.contains(id) else { throw DropSpaceError.unavailable("Translation worker must exit before removing its model") }
            var next = state; next.pendingRemoval.insert(id); try save(next)
            try clearDownloadFragments(model)
            for file in owned(model) { try Task.checkCancellation(); try deleteOwned(file) }
            next = state; next.pendingRemoval.remove(id); next.pendingFragments?.remove(id); next.disabled.remove(id); try save(next)
            await gate.release(permit)
        } catch { await gate.release(permit); throw error }
    }
    public func retryPendingCleanup() async {
        for id in state.pendingRemoval { try? await remove(id) }
        for id in state.pendingFragments ?? [] {
            guard let permit = try? await gate.acquire() else { return }
            if let model = try? catalog.model(id) { try? clearFragments(model) }
            await gate.release(permit)
        }
    }
    private func clearFragments(_ model: AIModelDescriptor) throws {
        try clearDownloadFragments(model)
        for file in transient(model) { try Task.checkCancellation(); try deleteOwned(file) }
        var next = state; next.pendingFragments?.remove(model.id); try save(next)
    }
    private func clearDownloadFragments(_ model: AIModelDescriptor) throws {
        for url in [assembly(model)] + model.parts.map({ partURL(model, $0) }) + catalog.licenses(model).map(license) {
            try Task.checkCancellation(); try DownloadStaging.cleanSidecars(url)
        }
    }
    private func final(_ model: AIModelDescriptor) -> URL { root.appendingPathComponent(model.fullsha + ".gguf") }
    private func assembly(_ model: AIModelDescriptor) -> URL { root.appendingPathComponent(model.fullsha + ".partial") }
    private func partURL(_ model: AIModelDescriptor, _ part: AIModelAsset) -> URL {
        root.appendingPathComponent(model.fullsha + "." + AIModelCatalog.manifestIdentity.prefix(16) + String(format: ".part%03d", part.order!))
    }
    private func license(_ asset: AIModelAsset) -> URL { root.appendingPathComponent(asset.sha256 + ".license.txt") }
    private func transient(_ model: AIModelDescriptor) -> [URL] {
        ([assembly(model)] + model.parts.map { partURL(model, $0) }).flatMap { [$0, $0.appendingPathExtension("resume.json")] } + catalog.licenses(model).map { license($0).appendingPathExtension("resume.json") }
    }
    private func owned(_ model: AIModelDescriptor) -> [URL] { [final(model)] + transient(model) + catalog.licenses(model).map(license) }
    private func save(_ value: State) throws {
        try Self.validateRoot(root)
        let temporary = root.appendingPathComponent("models-state-" + UUID().uuidString + ".tmp")
        defer { try? deleteOwned(temporary) }
        try AtomicFile.writeNew(JSONEncoder().encode(value), to: temporary)
        let target = root.appendingPathComponent("models-state.json")
        do { try publish(temporary, to: target); state = value }
        catch {
            // A directory-sync failure can occur after rename committed. Reflect
            // actual saved state while still reporting the failed durability step.
            if let data = try? Self.readSmall(target, maximum: 16384), let saved = try? JSONDecoder().decode(State.self, from: data) { state = saved }
            throw error
        }
    }
    private func deleteOwned(_ url: URL) throws {
        try Self.validateRoot(root)
        let fd = open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw Self.posix() }; defer { close(fd) }
        guard unlinkat(fd, url.lastPathComponent, 0) == 0 || errno == ENOENT else { throw Self.posix() }
    }
    private func publish(_ source: URL, to target: URL) throws {
        try Task.checkCancellation(); try Self.validateRoot(root)
        _ = try Self.size(source); _ = try Self.size(target)
        let fd = open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw Self.posix() }; defer { close(fd) }
        guard renameat(fd, source.lastPathComponent, fd, target.lastPathComponent) == 0, fsync(fd) == 0 else { throw Self.posix() }
    }
    private func verify(_ url: URL, bytes: Int64, hash: String) async throws -> Bool {
        try await diskTask {
            guard try Self.size(url) == bytes else { return false }
            let input = try Self.openRead(url); defer { try? input.close() }
            var digest = SHA256(), count: Int64 = 0
            while let buffer = try input.read(upToCount: 1024 * 1024), !buffer.isEmpty {
                try Task.checkCancellation(); count += Int64(buffer.count)
                guard count <= bytes else { return false }; digest.update(data: buffer)
            }
            return count == bytes && digest.finalize().map { String(format: "%02x", $0) }.joined() == hash
        }
    }
    private func diskTask<T: Sendable>(_ operation: @escaping @Sendable () throws -> T) async throws -> T {
        let task = Task.detached(priority: .utility, operation: operation)
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    private static func validateRoot(_ root: URL) throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let fd = open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw posix() }; close(fd)
    }
    private static func size(_ url: URL) throws -> Int64? {
        var value = stat()
        guard lstat(url.path, &value) == 0 else { if errno == ENOENT { return nil }; throw posix() }
        guard value.st_mode & S_IFMT == S_IFREG, value.st_nlink == 1, value.st_size >= 0 else { throw DropSpaceError.invalid("Unsafe model artifact") }
        return value.st_size
    }
    private static func openRead(_ url: URL) throws -> FileHandle {
        let fd = open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw posix() }
        var value = stat()
        guard fstat(fd, &value) == 0, value.st_mode & S_IFMT == S_IFREG, value.st_nlink == 1 else { close(fd); throw DropSpaceError.invalid("Unsafe model file") }
        return FileHandle(fileDescriptor: fd, closeOnDealloc: true)
    }
    private static func readSmall(_ url: URL, maximum: Int) throws -> Data? {
        guard let count = try size(url) else { return nil }
        guard count <= maximum else { throw DropSpaceError.limit("Model state size") }
        let file = try openRead(url); defer { try? file.close() }
        let result = try file.read(upToCount: maximum + 1) ?? Data()
        guard result.count <= maximum else { throw DropSpaceError.limit("Model state grew beyond limit") }; return result
    }
    private static func assemble(_ sources: [URL], output: URL, bytes: Int64, hash: String) throws {
        let fd = open(output.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw posix() }
        let destination = FileHandle(fileDescriptor: fd, closeOnDealloc: true); defer { try? destination.close() }
        var digest = SHA256(), count: Int64 = 0
        for source in sources {
            let input = try openRead(source); defer { try? input.close() }
            while let buffer = try input.read(upToCount: 131072), !buffer.isEmpty {
                try Task.checkCancellation(); count += Int64(buffer.count)
                guard count <= bytes else { throw DropSpaceError.limit("Model assembly exceeds catalog") }
                digest.update(data: buffer); try destination.write(contentsOf: buffer)
            }
        }
        try destination.synchronize()
        guard count == bytes, digest.finalize().map({ String(format: "%02x", $0) }).joined() == hash else { throw DropSpaceError.invalid("Assembled model checksum mismatch") }
    }
    private static func posix() -> POSIXError { POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
}
