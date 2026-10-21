import Foundation
import CryptoKit
import Darwin
import DropSpaceCore

public struct StoragePaths: Sendable {
    public let root: URL
    public var payloads: URL { root.appendingPathComponent("Payloads",isDirectory: true) }
    public var settings: URL { root.appendingPathComponent("settings.json") }
    public var cache: URL { root.appendingPathComponent("Cache",isDirectory: true) }
    public var exports: URL { FileManager.default.urls(for: .downloadsDirectory,in: .userDomainMask)[0].appendingPathComponent("DropSpace Exports",isDirectory: true) }
    public init(root: URL? = nil) {
        self.root = root ?? FileManager.default.urls(for: .applicationSupportDirectory,in: .userDomainMask)[0].appendingPathComponent("DropSpace/macOS",isDirectory: true)
    }
}
public actor SettingsStore: SettingsPersistence {
    private let file: URL
    public init(file: URL) { self.file = file }
    public func load() throws -> AppSettings {
        guard FileManager.default.fileExists(atPath: file.path) else { return AppSettings() }
        let data = try Data(contentsOf: file)
        guard data.count <= 2 * 1024 * 1024 else { throw DropSpaceError.storage("Settings file exceeds limit; original was preserved") }
        let stored = try JSONSerialization.jsonObject(with: data)
        let defaults = try JSONSerialization.jsonObject(with: JSONEncoder().encode(AppSettings()))
        func merge(_ base: Any, _ value: Any) -> Any {
            guard var base = base as? [String: Any], let value = value as? [String: Any] else { return value }
            for (key,item) in value { base[key] = base[key].map { merge($0,item) } ?? item }
            return base
        }
        let merged = try JSONSerialization.data(withJSONObject: merge(defaults,stored))
        return try JSONDecoder().decode(AppSettings.self,from: merged).validated()
    }
    public func save(_ settings: AppSettings) throws {
        let validated = try settings.validated()
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted,.sortedKeys]
        let bytes = try encoder.encode(validated)
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(),withIntermediateDirectories: true,attributes: [.posixPermissions: 0o700])
        try bytes.write(to: file,options: .atomic)
    }
}
public actor PayloadStore {
    public let directory: URL
    public init(directory: URL) { self.directory = directory }
    public func importPromisedFile(_ source: URL) throws -> String {
        let values = try source.resourceValues(forKeys: [.isSymbolicLinkKey,.isDirectoryKey])
        guard values.isSymbolicLink != true else { throw DropSpaceError.invalid("A promised file must not be a symbolic link") }
        try FileManager.default.createDirectory(at: directory,withIntermediateDirectories: true,attributes: [.posixPermissions: 0o700])
        let suffix = source.pathExtension.filter { $0.isASCII && $0.isLetter || $0.isNumber }
        let name = UUID().uuidString + (suffix.isEmpty ? "" : "." + String(suffix.prefix(12)))
        let temporary = directory.appendingPathComponent(".import-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: temporary) }
        try FileManager.default.copyItem(at: source,to: temporary)
        try Task.checkCancellation()
        guard renamex_np(temporary.path,directory.appendingPathComponent(name).path,UInt32(RENAME_EXCL)) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        return name
    }
    public func write(_ bytes: Data, extension suffix: String) throws -> String {
        guard suffix.range(of: "^[a-zA-Z0-9]{1,12}$",options: .regularExpression) != nil else { throw DropSpaceError.invalid("Payload extension") }
        try FileManager.default.createDirectory(at: directory,withIntermediateDirectories: true,attributes: [.posixPermissions: 0o700])
        let name = UUID().uuidString + "." + suffix
        try AtomicFile.writeNew(bytes,to: directory.appendingPathComponent(name))
        return name
    }
    public func url(_ name: String) throws -> URL {
        guard !name.isEmpty, name != ".", name != "..", !name.contains("/"), !name.contains("\\"), !name.contains("\0") else { throw DropSpaceError.invalid("Unsafe payload path") }
        let value = directory.appendingPathComponent(name)
        let attributes = try? FileManager.default.attributesOfItem(atPath: value.path)
        guard attributes?[.type] as? FileAttributeType != .typeSymbolicLink else { throw DropSpaceError.invalid("Linked payload") }
        return value
    }
    public func remove(_ name: String) throws {
        let value = try url(name)
        if (try? value.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true {
            // The name is a flat app-owned payload. FileManager removes directory contents
            // without following child symbolic links; external reference paths never enter here.
            try FileManager.default.removeItem(at: value)
            return
        }
        let fd = open(directory.path,O_RDONLY | O_DIRECTORY | O_NOFOLLOW)
        guard fd >= 0 else { if errno == ENOENT { return }; throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer { close(fd) }
        if unlinkat(fd,name,0) != 0 && errno != ENOENT { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
    }
    public func reconcile(_ store: ItemStore) async throws {
        for name in try await store.pendingCleanup() {
            do {
                if try await store.payloadIsReferenced(name) { continue }
                try remove(name); try await store.finishCleanup(name)
            } catch { try await store.finishCleanup(name,error: String(describing: type(of: error))) }
        }
    }
}
public enum Digests {
    public static func sha256(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x",$0) }.joined() }
    public static func sha256(file: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: file); defer { try? handle.close() }
        var hash = SHA256()
        while let bytes = try handle.read(upToCount: 1024 * 1024), !bytes.isEmpty { try Task.checkCancellation(); hash.update(data: bytes) }
        return hash.finalize().map { String(format: "%02x",$0) }.joined()
    }
}
