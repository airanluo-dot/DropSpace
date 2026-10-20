import Foundation
import Darwin
import CryptoKit
import DropSpaceCore

/// Owns package bytes/journals, never workers or presentation. Constructing or
/// loading an empty store creates no directory and starts no network operation.
public actor ModulePackageStore {
    private let files: ModuleOwnedFiles
    private let downloads: HTTPDownloader
    private let gate = DownloadPermitPool(limit: 1)
    private let systemVersion: OperatingSystemVersion
    public init(root: URL, budget: DownloadBudget, systemVersion: OperatingSystemVersion = ProcessInfo.processInfo.operatingSystemVersion) {
        files = ModuleOwnedFiles(root: root); downloads = HTTPDownloader(budget: budget, requestTimeout: 120, userAgent: "DropSpace-official-module/1 macOS"); self.systemVersion = systemVersion
    }
    private func serial<T: Sendable>(_ work: (isolated ModulePackageStore) throws -> T) async throws -> T {
        let permit = try await gate.acquire()
        do { try Task.checkCancellation(); let result = try work(self); await gate.release(permit); return result }
        catch { await gate.release(permit); throw error }
    }
    public func load() async throws -> [ModuleInstallation] {
        try await serial { owner in
            let state = try owner.files.path("state")
            guard FileManager.default.fileExists(atPath: state.path) else { return [] }
            var values: [ModuleInstallation] = []
            for file in try FileManager.default.contentsOfDirectory(at: state, includingPropertiesForKeys: nil).filter({ $0.pathExtension == "json" }) {
                try Task.checkCancellation()
                let id = file.deletingPathExtension().lastPathComponent
                guard (try? Self.identity(id)) != nil else { continue }
                do { values.append(try owner.readState(file, id: id)) }
                catch is CancellationError { throw CancellationError() }
                catch {
                    if var restored = try? owner.readState(file.appendingPathExtension("backup"), id: id) {
                        restored.enabled = false; restored.errorCode = "ModuleStateRecovered"; values.append(restored)
                    } else { values.append(.init(id: id, enabled: false, dataVersion: 0, errorCode: "ModuleStateInvalid")) }
                }
            }
            return values.sorted { $0.id < $1.id }
        }
    }
    public func write(_ state: ModuleInstallation) async throws {
        try Self.validate(state)
        try await serial { owner in
            let target = try owner.files.path("state/\(state.id).json")
            if let previous = try? owner.readState(target, id: state.id) { try owner.files.write(try JSONEncoder().encode(previous), to: target.appendingPathExtension("backup")) }
            try owner.files.write(try JSONEncoder().encode(state), to: target)
        }
    }
    public func dataDirectory(_ id: String) async throws -> URL {
        try Self.identity(id)
        return try await serial { owner in let path = try owner.files.path("data/" + id); try owner.files.create(path); return path }
    }
    public func versionDirectory(_ id: String, version: String) throws -> URL {
        try Self.identity(id, version: version); return try files.path("packages/\(id)/\(version)")
    }
    public func readSettings(_ id: String) async throws -> [String: String] {
        try Self.identity(id)
        return try await serial { owner in
            let path = try owner.files.path("data/\(id)/settings.json")
            guard FileManager.default.fileExists(atPath: path.path) else { return [:] }
            let bytes = try owner.files.read(path, maximum: 150 * 1024)
            try ModuleStoredJSON.validate(bytes)
            let settings = try JSONDecoder().decode([String: String].self, from: bytes)
            try Self.validateSettings(settings); return settings
        }
    }
    public func writeSettings(_ id: String, values: [String: String]) async throws {
        try Self.identity(id); try Self.validateSettings(values)
        let bytes = try JSONEncoder().encode(values)
        guard bytes.count <= 150 * 1024 else { throw ModuleArchive.invalid("ModuleSettingsTooLarge") }
        try await serial { owner in try owner.files.write(bytes, to: owner.files.path("data/\(id)/settings.json")) }
    }
    public func removeVersion(_ id: String, version: String) async throws -> Bool {
        try Self.identity(id, version: version)
        return try await serial { owner in
            // Unsafe ancestors are a pending cleanup obligation too. Do not let
            // a path refusal bypass the runtime's durable retry journal.
            guard let path = try? owner.files.path("packages/\(id)/\(version)") else { return false }
            return owner.files.remove(path)
        }
    }
    public func removeStaging(_ id: String, version: String? = nil) async throws -> Bool {
        try Self.identity(id, version: version)
        return try await serial { owner in
            guard let path = try? owner.files.path("staging/" + id + (version.map { "/" + $0 } ?? "")) else { return false }
            return owner.files.remove(path)
        }
    }
    /// Runtime must first withdraw UI and confirm the worker and descendants exit.
    public func clearData(_ id: String) async throws {
        try Self.identity(id)
        try await serial { owner in
            guard owner.files.remove(try owner.files.path("data/" + id)) else { throw ModuleArchive.invalid("ModuleDataCleanupPending") }
        }
    }
    public func readManifest(_ id: String, version: String) async throws -> ModuleManifest {
        try await serial { owner in try owner.installed(id, version: version) }
    }
    public func prepare(_ package: OfficialModulePackage, installed: [String: ModuleInstallation]) async throws -> ModuleManifest {
        try ModuleCatalogRules.require(package); try Self.identity(package.id, version: package.version)
        let permit = try await gate.acquire()
        do {
            let final = try versionDirectory(package.id, version: package.version)
            if FileManager.default.fileExists(atPath: final.path) {
                let retained = try self.installed(package.id, version: package.version, expected: package)
                try compatibility(retained, installed: installed); await gate.release(permit); return retained
            }
            let staging = try files.path("staging/\(package.id)/\(package.version)")
            try files.create(staging)
            let archiveURL = staging.appendingPathComponent("package.zip")
            if try !files.verify(archiveURL, bytes: package.bytes, hash: package.sha256) {
                let capacity = try staging.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage
                if let capacity, capacity < package.bytes + Int64(ModuleArchive.maximumExpanded) { throw ModuleArchive.invalid("ModuleInsufficientDiskSpace") }
                let source = URL(string: package.url)!, downloader = downloads
                try await withThrowingTaskGroup(of: Void.self) { tasks in
                    tasks.addTask { try await downloader.download(source: source, staging: archiveURL, expectedBytes: package.bytes, sha256: package.sha256, maximumBytes: package.bytes, trusted: { ModuleCatalogRules.trustedHop($0, original: source) }) }
                    tasks.addTask { try await Task.sleep(for: .seconds(15 * 60)); throw ModuleArchive.invalid("ModuleDownloadDeadline") }
                    defer { tasks.cancelAll() }; _ = try await tasks.next()
                }
            }
            guard try files.verify(archiveURL, bytes: package.bytes, hash: package.sha256) else { throw ModuleArchive.invalid("ModuleArchiveIntegrity") }
            let archive = try ModuleArchive(url: archiveURL), (manifest, _) = try archive.manifest(package: package)
            try compatibility(manifest, installed: installed)
            let extracted = staging.appendingPathComponent("extracted")
            guard files.remove(extracted) else { throw ModuleArchive.invalid("ModuleStagingCleanupPending") }
            try files.create(extracted)
            do {
                for entry in archive.entries where !entry.directory {
                    try Task.checkCancellation()
                    let destination = extracted.appendingPathComponent(entry.name)
                    try files.create(destination.deletingLastPathComponent()); try files.check(destination)
                    let fd = open(destination.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
                    guard fd >= 0 else { throw POSIXError(.EIO) }
                    let output = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
                    do { try archive.stream(entry) { try output.write(contentsOf: $0) }; try output.synchronize(); try output.close() }
                    catch { try? output.close(); throw error }
                }
                for file in manifest.files {
                    guard try files.verify(extracted.appendingPathComponent(file.path), bytes: file.bytes, hash: file.sha256) else { throw ModuleArchive.invalid("ModuleFileIntegrity") }
                }
                let worker = extracted.appendingPathComponent(manifest.entryPoint)
                try verifyNativeWorker(worker)
                guard chmod(worker.path, 0o700) == 0 else { throw POSIXError(.EACCES) }
                try FileManager.default.copyItem(at: archiveURL, to: extracted.appendingPathComponent(".package.zip"))
                let retained = try FileHandle(forWritingTo: extracted.appendingPathComponent(".package.zip"))
                do { try retained.synchronize(); try retained.close() } catch { try? retained.close(); throw error }
                try files.write(try JSONEncoder().encode(package), to: extracted.appendingPathComponent(".package-record.json"))
                for directory in try files.tree(extracted).filter({ !$0.isFile }).map(\.url) { try files.syncDirectory(directory) }
                try files.syncDirectory(extracted)
                try Task.checkCancellation(); try files.create(final.deletingLastPathComponent()); try files.check(final)
                guard renamex_np(extracted.path, final.path, UInt32(RENAME_EXCL)) == 0 else { throw ModuleArchive.invalid("ModuleVersionPublishFailed") }
                try files.syncDirectory(final.deletingLastPathComponent())
            } catch { _ = files.remove(extracted); throw error }
            await gate.release(permit); return manifest
        } catch { await gate.release(permit); throw error }
    }
    private func installed(_ id: String, version: String, expected: OfficialModulePackage? = nil) throws -> ModuleManifest {
        let root = try versionDirectory(id, version: version)
        let record = try files.read(root.appendingPathComponent(".package-record.json"), maximum: 16 * 1024)
        try ModuleStoredJSON.validate(record, keys: ["Id", "Version", "Url", "Bytes", "Sha256"])
        let package = try ModuleCatalogRules.require(JSONDecoder().decode(OfficialModulePackage.self, from: record))
        guard package.id == id, package.version == version,
              expected.map({ $0.url == package.url && $0.bytes == package.bytes && $0.sha256.lowercased() == package.sha256.lowercased() }) ?? true else { throw ModuleArchive.invalid("ModulePackageIdentityConflict") }
        let archiveURL = root.appendingPathComponent(".package.zip")
        guard try files.verify(archiveURL, bytes: package.bytes, hash: package.sha256) else { throw ModuleArchive.invalid("ModuleArchiveIntegrity") }
        let archive = try ModuleArchive(url: archiveURL), (manifest, bytes) = try archive.manifest(package: package)
        let actual = try files.tree(root).filter { $0.isFile }.map { ModuleArchive.canonical(String($0.url.path.dropFirst(root.path.count + 1))) }
        let wanted = manifest.files.map { ModuleArchive.canonical($0.path) } + ["manifest.json", ".package.zip", ".package-record.json"]
        guard Set(actual) == Set(wanted), actual.count == wanted.count else { throw ModuleArchive.invalid("ModuleInstalledInventory") }
        guard try files.read(root.appendingPathComponent("manifest.json"), maximum: ModuleArchive.maximumManifest) == bytes else { throw ModuleArchive.invalid("ModuleManifestIntegrity") }
        for file in manifest.files {
            guard try files.verify(root.appendingPathComponent(file.path), bytes: file.bytes, hash: file.sha256) else { throw ModuleArchive.invalid("ModuleFileIntegrity") }
        }
        try verifyNativeWorker(root.appendingPathComponent(manifest.entryPoint)); return manifest
    }
    private func compatibility(_ manifest: ModuleManifest, installed: [String: ModuleInstallation]) throws {
        try ModuleContract.validate(manifest, installed: installed, systemVersion: systemVersion)
        if let previous = installed[manifest.id] {
            guard previous.errorCode != "ModuleStateInvalid" else { throw ModuleArchive.invalid("ModuleStateInvalid") }
            guard previous.dataVersion == 0 || previous.dataVersion == manifest.data.version else { throw ModuleArchive.invalid("ModuleDataMigrationUnsupported") }
        }
    }
    private func verifyNativeWorker(_ url: URL) throws {
        let file = try files.openRead(url); defer { try? file.close() }
        guard try file.read(upToCount: 8) == Data([0xcf, 0xfa, 0xed, 0xfe, 0x0c, 0, 0, 1]) else { throw ModuleArchive.invalid("ModuleWorkerNotNativeArm64") }
    }
    private func readState(_ url: URL, id: String) throws -> ModuleInstallation {
        let bytes = try files.read(url, maximum: 16 * 1024)
        try ModuleStoredJSON.validate(bytes, keys: ["Id", "Version", "Enabled", "DataVersion", "Transaction", "PreviousVersion", "CandidateVersion", "ErrorCode"])
        let value = try JSONDecoder().decode(ModuleInstallation.self, from: bytes); try Self.validate(value)
        guard value.id == id else { throw ModuleArchive.invalid("ModuleStateIdentityMismatch") }; return value
    }
    private static func identity(_ id: String, version: String? = nil) throws {
        guard ModuleContract.validID(id), !id.split(separator: ".", omittingEmptySubsequences: false).contains(""),
              version.map({ ModuleContract.version($0) != nil }) ?? true else { throw ModuleArchive.invalid("ModuleIdentityInvalid") }
        try ModuleArchive.validatePath(id + ".json", directory: false)
    }
    private static func validate(_ state: ModuleInstallation) throws {
        try identity(state.id, version: state.version)
        for value in [state.previousVersion, state.candidateVersion] { if let value { try identity(state.id, version: value) } }
        guard state.dataVersion >= 0, state.dataVersion <= Int32.max, (state.errorCode?.utf16.count ?? 0) <= 256 else { throw ModuleArchive.invalid("ModuleStateInvalid") }
    }
    private static func validateSettings(_ settings: [String: String]) throws {
        guard settings.count <= 32, settings.allSatisfy({ ModuleContract.validID($0.key) && $0.value.utf16.count <= 4096 }) else { throw ModuleArchive.invalid("ModuleSettingsInvalid") }
    }
}

enum ModuleStoredJSON {
    static func validate(_ bytes: Data, keys: Set<String>? = nil) throws {
        let root = try ModuleJSON.decode(bytes, maximumBytes: 512 * 1024)
        func visit(_ value: ModuleJSON) throws {
            switch value {
            case .object(let entries):
                var names = Set<String>()
                for entry in entries { guard names.insert(entry.name.lowercased()).inserted else { throw ModuleArchive.invalid("ModuleDuplicateJsonKey") }; try visit(entry.value) }
            case .array(let values): for value in values { try visit(value) }
            default: break
            }
        }
        try visit(root)
        if let keys {
            guard case .object(let entries) = root, entries.allSatisfy({ keys.contains($0.name) }) else { throw ModuleArchive.invalid("ModuleJsonInvalid") }
        }
    }
}

private struct ModuleOwnedFiles: Sendable {
    let root: URL
    init(root: URL) {
        // Resolve only the parent, for the system /var→/private/var alias; a link
        // at the module root itself is still refused, never silently followed.
        var parent = Self.lexical(root.deletingLastPathComponent())
        var missing: [String] = []
        while !FileManager.default.fileExists(atPath: parent.path), parent.path != "/" {
            missing.append(parent.lastPathComponent); parent.deleteLastPathComponent()
        }
        // Foundation intentionally keeps /var as a user-facing alias on macOS.
        // The ownership walk needs its actual POSIX path to reject other links.
        if let physical = realpath(parent.path, nil) {
            parent = URL(fileURLWithPath: String(cString: physical)); free(physical)
        }
        for component in missing.reversed() { parent.appendPathComponent(component) }
        self.root = Self.lexical(parent.appendingPathComponent(root.lastPathComponent))
    }
    private static func lexical(_ url: URL) -> URL {
        var parts: [String] = []
        for part in url.path.components(separatedBy: "/") {
            if part == ".." { if !parts.isEmpty { parts.removeLast() } }
            else if !part.isEmpty && part != "." { parts.append(part) }
        }
        return URL(fileURLWithPath: "/" + parts.joined(separator: "/"))
    }
    func path(_ relative: String) throws -> URL { let result = Self.lexical(root.appendingPathComponent(relative)); try check(result); return result }
    func check(_ url: URL) throws {
        let full = Self.lexical(url)
        guard full.path == root.path || full.path.hasPrefix(root.path + "/") else { throw ModuleArchive.invalid("ModulePathEscaped") }
        var part = full
        while true {
            var info = stat()
            if lstat(part.path, &info) == 0 {
                guard info.st_mode & S_IFMT != S_IFLNK else { throw ModuleArchive.invalid("ModuleLinkedPath") }
                if info.st_mode & S_IFMT == S_IFREG, info.st_nlink != 1 { throw ModuleArchive.invalid("ModuleLinkedFile") }
            } else if errno != ENOENT { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
            if part.path == "/" { break }; part.deleteLastPathComponent()
        }
    }
    func create(_ url: URL) throws {
        try check(url); try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]); try check(url)
    }
    func openRead(_ url: URL) throws -> FileHandle {
        try check(url)
        let fd = open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1 else { close(fd); throw ModuleArchive.invalid("ModuleUnsafeFile") }
        return FileHandle(fileDescriptor: fd, closeOnDealloc: true)
    }
    func read(_ url: URL, maximum: Int) throws -> Data {
        let file = try openRead(url); defer { try? file.close() }
        guard try file.seekToEnd() <= maximum else { throw ModuleArchive.invalid("ModuleJsonTooLarge") }
        try file.seek(toOffset: 0)
        let bytes = try file.read(upToCount: maximum + 1) ?? Data()
        guard bytes.count <= maximum else { throw ModuleArchive.invalid("ModuleJsonTooLarge") }; return bytes
    }
    func verify(_ url: URL, bytes: Int64, hash: String) throws -> Bool {
        try check(url)
        guard FileManager.default.fileExists(atPath: url.path) else { return false }
        let file = try openRead(url); defer { try? file.close() }
        guard try file.seekToEnd() == bytes else { return false }; try file.seek(toOffset: 0)
        var digest = SHA256(), count: Int64 = 0
        while let chunk = try file.read(upToCount: 131072), !chunk.isEmpty {
            try Task.checkCancellation(); count += Int64(chunk.count); guard count <= bytes else { return false }; digest.update(data: chunk)
        }
        return count == bytes && digest.finalize().map { String(format: "%02x", $0) }.joined() == hash.lowercased()
    }
    func write(_ data: Data, to destination: URL) throws {
        try create(destination.deletingLastPathComponent()); try check(destination)
        let staged = destination.deletingLastPathComponent().appendingPathComponent(".state-" + UUID().uuidString)
        defer { unlink(staged.path) }
        try AtomicFile.writeNew(data, to: staged); try check(destination); try Task.checkCancellation()
        guard rename(staged.path, destination.path) == 0 else { throw POSIXError(.EIO) }
        try syncDirectory(destination.deletingLastPathComponent())
    }
    func syncDirectory(_ url: URL) throws {
        try check(url)
        let directory = open(url.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW)
        guard directory >= 0 else { throw POSIXError(.EIO) }; defer { close(directory) }
        guard fsync(directory) == 0 else { throw POSIXError(.EIO) }
    }
    func tree(_ directory: URL) throws -> [(url: URL, isFile: Bool)] {
        try check(directory)
        var result: [(URL, Bool)] = [], pending = [directory]
        while let next = pending.popLast() {
            try Task.checkCancellation(); try check(next)
            for entry in try FileManager.default.contentsOfDirectory(at: next, includingPropertiesForKeys: nil) {
                try check(entry); var info = stat()
                guard lstat(entry.path, &info) == 0, [S_IFDIR, S_IFREG].contains(info.st_mode & S_IFMT) else { throw ModuleArchive.invalid("ModuleUnsafeFile") }
                let file = info.st_mode & S_IFMT == S_IFREG
                result.append((entry, file)); if !file { pending.append(entry) }
            }
        }
        return result
    }
    func remove(_ directory: URL) -> Bool {
        do {
            try check(directory)
            guard FileManager.default.fileExists(atPath: directory.path) else { return true }
            let entries = try tree(directory) // Validate the entire tree before any removal.
            for entry in entries.sorted(by: { $0.url.path.count > $1.url.path.count }) {
                try check(entry.url)
                guard (entry.isFile ? unlink(entry.url.path) : rmdir(entry.url.path)) == 0 else { return false }
            }
            try check(directory); return rmdir(directory.path) == 0
        } catch { return false }
    }
}
