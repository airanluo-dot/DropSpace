import Foundation
import CryptoKit
import Darwin
import DropSpaceCore

/// Shared source/AI quota. Every filesystem operation is relative to one opened,
/// no-follow directory; clearing cannot follow a replaced entry into user files.
public actor LyricsCache {
    public enum Category: String, Sendable { case source, ai }
    public static let maximumDocumentBytes = 4 * 1024 * 1024
    private let root: URL
    private var quota: Int64 = 1_000_000_000
    public private(set) var generation: UInt64 = 0
    public private(set) var executionGeneration: UInt64 = 0
    public init(directory: URL) { root = directory }

    public func configure(maximumBytes: Int64) throws {
        try Task.checkCancellation()
        let value = min(10_000_000_000, max(0, maximumBytes))
        if (quota == 0) != (value == 0) { generation &+= 1 }
        quota = value
        guard let directory = try openRoot(create: false) else { return }
        defer { close(directory) }
        try trim(directory, to: quota)
    }
    public func readDocument(identity: String) throws -> LyricsDocument? {
        guard let bytes = try read(category: .source, identity: identity),
              let document = try? JSONDecoder().decode(LyricsDocument.self, from: bytes), Self.valid(document) else { return nil }
        return document
    }
    public func writeDocument(identity: String, document: LyricsDocument, generation: UInt64) throws {
        guard Self.valid(document) else { return }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        try write(category: .source, identity: identity, bytes: encoder.encode(document), generation: generation)
    }
    public func read(category: Category, identity: String) throws -> Data? {
        try Task.checkCancellation()
        guard quota > 0, let directory = try openRoot(create: false) else { return nil }
        defer { close(directory) }
        let fd = openat(directory, Self.name(category, identity), O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { if errno == ENOENT || errno == ELOOP { return nil }; throw Self.posix() }
        defer { close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1,
              info.st_size >= 0, info.st_size <= Self.maximumDocumentBytes else { return nil }
        var bytes = Data(), chunk = [UInt8](repeating: 0, count: 16384)
        while true {
            try Task.checkCancellation()
            let count = Darwin.read(fd, &chunk, chunk.count)
            if count < 0 { if errno == EINTR { continue }; throw Self.posix() }
            if count == 0 { break }
            guard bytes.count + count <= Self.maximumDocumentBytes else { return nil }
            bytes.append(contentsOf: chunk.prefix(count))
        }
        // Access order affects eviction, never lyric identity or source timestamps.
        _ = futimes(fd, nil)
        return bytes
    }
    public func write(category: Category, identity: String, bytes: Data, generation run: UInt64) throws {
        try Task.checkCancellation()
        guard bytes.count <= Self.maximumDocumentBytes else { throw DropSpaceError.limit("Lyrics cache entry exceeds 4 MiB") }
        guard quota > 0, generation == run, Int64(bytes.count) <= quota else { return }
        guard let directory = try openRoot(create: true) else { return }
        defer { close(directory) }
        var space = statfs()
        guard fstatfs(directory, &space) == 0 else { throw Self.posix() }
        let available = UInt64(space.f_bavail).multipliedReportingOverflow(by: UInt64(space.f_bsize))
        guard !available.overflow, available.partialValue >= UInt64(bytes.count) + 16 * 1024 * 1024 else { return }
        let final = Self.name(category, identity), temporary = final + "." + UUID().uuidString.replacingOccurrences(of: "-", with: "") + ".tmp"
        let fd = openat(directory, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw Self.posix() }
        defer { close(fd); _ = unlinkat(directory, temporary, 0) }
        try bytes.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                try Task.checkCancellation()
                let count = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), min(16384, buffer.count - offset))
                if count < 0 { if errno == EINTR { continue }; throw Self.posix() }
                guard count > 0 else { throw POSIXError(.EIO) }; offset += count
            }
        }
        guard fsync(fd) == 0 else { throw Self.posix() }
        try trim(directory, to: quota - Int64(bytes.count), replacing: final, preserving: temporary)
        try Task.checkCancellation()
        var previous = stat()
        if fstatat(directory, final, &previous, AT_SYMLINK_NOFOLLOW) == 0 {
            guard previous.st_mode & S_IFMT == S_IFREG, previous.st_nlink == 1 else { throw DropSpaceError.invalid("Unsafe lyrics cache destination") }
        } else if errno != ENOENT { throw Self.posix() }
        guard renameat(directory, temporary, directory, final) == 0 else { throw Self.posix() }
        guard fsync(directory) == 0 else { throw Self.posix() }
    }
    public func clear() throws {
        // Actor ownership serializes publication and this fence. Queued old writers
        // cannot repopulate after clear; future inference also observes retirement.
        generation &+= 1; executionGeneration &+= 1
        guard let directory = try openRoot(create: false) else { return }
        defer { close(directory) }
        for file in try files(directory) {
            try Task.checkCancellation()
            guard unlinkat(directory, file.name, 0) == 0 || errno == ENOENT else { throw Self.posix() }
        }
    }
    public func usage() throws -> Int64 {
        guard let directory = try openRoot(create: false) else { return 0 }
        defer { close(directory) }
        return try files(directory).reduce(Int64(0)) { $0 + $1.size }
    }
    private func openRoot(create: Bool) throws -> Int32? {
        if create { try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]) }
        let fd = open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { if errno == ENOENT && !create { return nil }; throw Self.posix() }
        return fd
    }
    private struct Entry { var name: String; var size: Int64; var modified: timespec }
    private func files(_ directory: Int32) throws -> [Entry] {
        let copy = dup(directory); guard copy >= 0 else { throw Self.posix() }
        guard let stream = fdopendir(copy) else { close(copy); throw Self.posix() }
        defer { closedir(stream) }
        var result: [Entry] = []
        while true {
            try Task.checkCancellation(); errno = 0
            guard let entry = readdir(stream) else { if errno != 0 { throw Self.posix() }; break }
            let name = withUnsafePointer(to: &entry.pointee.d_name) { $0.withMemoryRebound(to: CChar.self, capacity: 1024) { String(cString: $0) } }
            guard Self.owned(name) else { continue }
            var info = stat()
            guard fstatat(directory, name, &info, AT_SYMLINK_NOFOLLOW) == 0 else { if errno == ENOENT { continue }; throw Self.posix() }
            guard info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1 else { continue }
            result.append(.init(name: name, size: info.st_size, modified: info.st_mtimespec))
        }
        return result
    }
    private func trim(_ directory: Int32, to maximum: Int64, replacing: String? = nil, preserving: String? = nil) throws {
        let entries = try files(directory)
        for item in entries where item.name.hasSuffix(".tmp") && item.name != preserving {
            guard unlinkat(directory, item.name, 0) == 0 || errno == ENOENT else { throw Self.posix() }
        }
        let retained = entries.filter { !$0.name.hasSuffix(".tmp") && $0.name != replacing }.sorted {
            if $0.modified.tv_sec != $1.modified.tv_sec { return $0.modified.tv_sec < $1.modified.tv_sec }
            if $0.modified.tv_nsec != $1.modified.tv_nsec { return $0.modified.tv_nsec < $1.modified.tv_nsec }
            return $0.name < $1.name
        }
        var total = retained.reduce(Int64(0)) { $0 + $1.size }
        for item in retained where total > maximum {
            try Task.checkCancellation()
            guard unlinkat(directory, item.name, 0) == 0 || errno == ENOENT else { throw Self.posix() }
            total -= item.size
        }
    }
    private static func name(_ category: Category, _ identity: String) -> String {
        category.rawValue + "-" + SHA256.hash(data: Data((category.rawValue + "\n" + identity).utf8)).map { String(format: "%02X", $0) }.joined() + ".lyrics-cache"
    }
    private static func owned(_ name: String) -> Bool {
        name.range(of: #"^(source|ai)-[0-9A-Fa-f]{64}\.lyrics-cache(?:\.[0-9A-Fa-f]{32}\.tmp)?$"#, options: .regularExpression) != nil
    }
    private static func valid(_ value: LyricsDocument) -> Bool {
        guard !value.lines.isEmpty, value.lines.count <= 10000, let match = value.match,
              !(match.candidateID ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, match.duration.isFinite, match.score.isFinite else { return false }
        var last = -Double.infinity
        for line in value.lines {
            guard line.start.isFinite, line.end.isFinite, line.start >= last, line.end >= line.start, line.words.count <= 2048 else { return false }; last = line.start
            var previous = -Double.infinity
            for word in line.words {
                guard word.start.isFinite, word.end.isFinite, word.start >= previous, word.end >= word.start else { return false }; previous = word.start
            }
        }
        return true
    }
    private static func posix() -> POSIXError { POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
}
