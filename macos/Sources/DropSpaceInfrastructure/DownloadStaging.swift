import Foundation
import Darwin
import DropSpaceCore

/// Only named artifacts belonging to one transfer. Unknown files and linked
/// files are never recursively removed when a transfer is cancelled or retried.
public enum DownloadStaging {
    public static func clean(_ staging: URL) throws {
        let files = try DownloadFiles(staging: staging)
        try files.remove(files.staging)
        try cleanSidecars(staging)
    }
    public static func cleanSidecars(_ staging: URL) throws {
        let files = try DownloadFiles(staging: staging)
        for suffix in ["resume.json", "request.json"] { try files.remove(files.staging.appendingPathExtension(suffix)) }
        try files.cleanRanges()
    }
}

struct DownloadFiles: Sendable {
    let staging: URL
    var ranges: URL { staging.appendingPathExtension("ranges") }
    var identity: URL { ranges.appendingPathComponent("identity.json") }
    init(staging: URL) throws {
        guard staging.isFileURL, !["", ".", ".."].contains(staging.lastPathComponent) else { throw DropSpaceError.invalid("Invalid download staging path") }
        // Resolve the parent only (including macOS's /var alias). The staging
        // file and its range directory themselves must never be links.
        let parent = staging.deletingLastPathComponent().resolvingSymlinksInPath()
        self.staging = parent.appendingPathComponent(staging.lastPathComponent)
        try check(self.staging)
    }
    func part(_ index: Int) -> URL { ranges.appendingPathComponent(String(format: "part-%02d.bin", index)) }
    func check(_ url: URL) throws {
        let root = staging.deletingLastPathComponent().path
        guard url.path.hasPrefix(root + "/") else { throw DropSpaceError.invalid("Download path escaped") }
        var current = url
        while current.path != root {
            var info = stat()
            if lstat(current.path, &info) == 0 {
                let kind = info.st_mode & S_IFMT
                guard kind == S_IFREG || kind == S_IFDIR, kind != S_IFREG || info.st_nlink == 1 else { throw DropSpaceError.invalid("Unsafe download artifact") }
            } else if errno != ENOENT { throw Self.posix() }
            current.deleteLastPathComponent()
        }
    }
    func size(_ url: URL) throws -> Int64? {
        try check(url); var info = stat()
        guard lstat(url.path, &info) == 0 else { if errno == ENOENT { return nil }; throw Self.posix() }
        guard info.st_mode & S_IFMT == S_IFREG else { throw DropSpaceError.invalid("Download artifact is not a file") }
        return info.st_size
    }
    func open(_ url: URL, append: Bool = false, reading: Bool = false) throws -> FileHandle {
        try check(url)
        let flags = (reading ? O_RDONLY : O_WRONLY | O_CREAT | (append ? O_APPEND : 0)) | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK
        let fd = Darwin.open(url.path, flags, 0o600)
        guard fd >= 0 else { throw Self.posix() }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1 else { close(fd); throw DropSpaceError.invalid("Unsafe download artifact") }
        if !reading && !append && ftruncate(fd, 0) != 0 { let error = Self.posix(); close(fd); throw error }
        return FileHandle(fileDescriptor: fd, closeOnDealloc: true)
    }
    func remove(_ url: URL) throws {
        guard try size(url) != nil else { return }
        guard unlink(url.path) == 0 || errno == ENOENT else { throw Self.posix() }
    }
    func read(_ url: URL, maximum: Int = 16384) throws -> Data? {
        guard let size = try size(url) else { return nil }
        guard size < maximum else { throw DropSpaceError.limit("Download metadata size") }
        let input = try open(url, reading: true); defer { try? input.close() }
        let data = try input.read(upToCount: maximum) ?? Data()
        guard data.count < maximum else { throw DropSpaceError.limit("Download metadata size") }; return data
    }
    func write<T: Encodable>(_ value: T, to target: URL) throws {
        try check(target)
        let temporary = target.deletingLastPathComponent().appendingPathComponent(".download-state-" + UUID().uuidString)
        defer { unlink(temporary.path) }
        try AtomicFile.writeNew(JSONEncoder().encode(value), to: temporary)
        try check(target); try Task.checkCancellation()
        guard rename(temporary.path, target.path) == 0 else { throw Self.posix() }
        let fd = Darwin.open(target.deletingLastPathComponent().path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw Self.posix() }; defer { close(fd) }
        guard fsync(fd) == 0 else { throw Self.posix() }
    }
    func prepareRanges() throws {
        try check(ranges)
        guard mkdir(ranges.path, 0o700) == 0 || errno == EEXIST else { throw Self.posix() }
        var info = stat()
        guard lstat(ranges.path, &info) == 0, info.st_mode & S_IFMT == S_IFDIR else { throw DropSpaceError.invalid("Unsafe download range directory") }
        try check(ranges)
    }
    func cleanRanges() throws {
        try check(ranges)
        guard FileManager.default.fileExists(atPath: ranges.path) else { return }
        let known = [identity] + (0..<256).map(part)
        // Validate all known paths before deleting the first one.
        for url in known { _ = try size(url) }
        for url in known { try remove(url) }
        if try FileManager.default.contentsOfDirectory(atPath: ranges.path).isEmpty,
           rmdir(ranges.path) != 0 { throw Self.posix() }
    }
    func rangeBytes() throws -> Int64 {
        try check(ranges)
        guard FileManager.default.fileExists(atPath: ranges.path) else { return 0 }
        var total: Int64 = 0
        let known = Set((0..<256).map { part($0).lastPathComponent })
        for name in try FileManager.default.contentsOfDirectory(atPath: ranges.path) where known.contains(name) {
            let (sum, overflow) = total.addingReportingOverflow(try size(ranges.appendingPathComponent(name)) ?? 0)
            guard !overflow else { throw DropSpaceError.limit("Download fragment size") }; total = sum
        }
        return total
    }
    func requireSpace(_ bytes: Int64) throws {
        let (required, overflow) = bytes.addingReportingOverflow(64 * 1024 * 1024)
        guard bytes >= 0, !overflow else { throw DropSpaceError.limit("Download storage requirement") }
        var info = statfs()
        guard statfs(staging.deletingLastPathComponent().path, &info) == 0 else { throw Self.posix() }
        guard UInt64(info.f_bavail) * UInt64(info.f_bsize) >= UInt64(required) else { throw DropSpaceError.storage("Insufficient download disk space") }
    }
    private static func posix() -> POSIXError { POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
}
