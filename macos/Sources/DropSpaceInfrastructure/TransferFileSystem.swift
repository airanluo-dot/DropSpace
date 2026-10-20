import Foundation
import Darwin
import CryptoKit
import DropSpaceCore

/// Descriptor-relative traversal never follows a sender/user-controlled link.
/// Instances stay inside one actor or synchronous operation; descriptors are not
/// shared across concurrent close/read operations.
final class TransferDescriptor {
    let raw: Int32
    init(_ raw: Int32) throws { guard raw >= 0 else { throw TransferFileSystem.posix() }; self.raw = raw }
    deinit { Darwin.close(raw) }
    func info() throws -> stat { var value = stat(); guard fstat(raw, &value) == 0 else { throw TransferFileSystem.posix() }; return value }
}
enum TransferFileSystem {
    static func posix() -> POSIXError { .init(POSIXErrorCode(rawValue: errno) ?? .EIO) }
    static func components(_ url: URL) throws -> [String] {
        guard url.isFileURL, url.path.hasPrefix("/"), !url.path.contains("\0") else { throw DropSpaceError.invalid("Transfer file URL") }
        let parts = url.path.split(separator: "/").map(String.init)
        guard !parts.contains(".."), !parts.contains(".") else { throw DropSpaceError.invalid("Transfer file path") }; return parts
    }
    static func directory(_ url: URL, create: Bool = false) throws -> TransferDescriptor {
        var parts = try components(url)
        // Foundation deliberately collapses /private/var back to /var even
        // after resolvingSymlinksInPath. Permit only these root-owned macOS
        // aliases with their exact system target, never arbitrary user links.
        if let first = parts.first, ["var", "tmp", "etc"].contains(first) {
            var info = stat(), target = [CChar](repeating: 0, count: 1024)
            let alias = "/" + first
            if lstat(alias, &info) == 0, info.st_mode & S_IFMT == S_IFLNK {
                let length = readlink(alias, &target, target.count - 1)
                guard info.st_uid == 0, length > 0, String(cString: target) == "private/" + first else { throw DropSpaceError.invalid("Unexpected macOS system path alias") }
                parts.insert("private", at: 0)
            }
        }
        var current = try TransferDescriptor(Darwin.open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC))
        for component in parts {
            try Task.checkCancellation()
            if create, mkdirat(current.raw, component, 0o700) != 0, errno != EEXIST { throw posix() }
            current = try TransferDescriptor(openat(current.raw, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC))
        }
        return current
    }
    static func file(_ url: URL) throws -> TransferDescriptor {
        _ = try components(url)
        let parent = try directory(url.deletingLastPathComponent())
        let descriptor = try TransferDescriptor(openat(parent.raw, url.lastPathComponent, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK))
        guard try descriptor.info().st_mode & S_IFMT == S_IFREG else { throw DropSpaceError.invalid("Transfer source must be a regular file") }
        return descriptor
    }
    static func entries(_ directory: TransferDescriptor, visit: (String, stat) throws -> Void) throws {
        let copy = dup(directory.raw); guard copy >= 0 else { throw posix() }
        guard let stream = fdopendir(copy) else { let error = posix(); Darwin.close(copy); throw error }
        defer { closedir(stream) }
        rewinddir(stream)
        while true {
            try Task.checkCancellation(); errno = 0
            guard let entry = readdir(stream) else { if errno != 0 { throw posix() }; return }
            let name = withUnsafePointer(to: &entry.pointee.d_name) { pointer in
                pointer.withMemoryRebound(to: CChar.self, capacity: Int(MAXNAMLEN) + 1) { String(cString: $0) }
            }
            guard name != ".", name != ".." else { continue }
            var info = stat()
            guard fstatat(directory.raw, name, &info, AT_SYMLINK_NOFOLLOW) == 0 else { throw posix() }
            try visit(name, info)
        }
    }
    static func read(_ file: TransferDescriptor, offset: Int64, count: Int) throws -> Data {
        guard offset >= 0, count >= 0 else { throw DropSpaceError.invalid("Transfer read range") }
        var output = Data(count: count)
        try output.withUnsafeMutableBytes { buffer in
            var done = 0
            while done < count {
                try Task.checkCancellation()
                let length = pread(file.raw, buffer.baseAddress!.advanced(by: done), count - done, offset + Int64(done))
                if length < 0, errno == EINTR { continue }
                guard length > 0 else { if length == 0 { throw DropSpaceError.invalid("Transfer source truncated") }; throw posix() }
                done += length
            }
        }
        return output
    }
    static func write(_ data: Data, to file: TransferDescriptor) throws {
        try data.withUnsafeBytes { buffer in
            var done = 0
            while done < buffer.count {
                try Task.checkCancellation()
                let length = Darwin.write(file.raw, buffer.baseAddress!.advanced(by: done), buffer.count - done)
                if length < 0, errno == EINTR { continue }
                guard length > 0 else { throw posix() }; done += length
            }
        }
    }
    static func hash(_ file: TransferDescriptor, size: Int64) throws -> String {
        var digest = SHA256(), offset: Int64 = 0
        while offset < size {
            let bytes = try read(file, offset: offset, count: Int(min(81920, size - offset))); digest.update(data: bytes); offset += Int64(bytes.count)
        }
        return digest.finalize().map { String(format: "%02x", $0) }.joined()
    }
}

public struct TransferSource: Sendable {
    public let url: URL
    public let device: Int32
    public let inode: UInt64
    public let size: Int64
    let modifiedSeconds: Int, modifiedNanoseconds: Int, changedSeconds: Int, changedNanoseconds: Int
    init(url: URL, info: stat) {
        self.url = url; device = info.st_dev; inode = info.st_ino; size = info.st_size
        modifiedSeconds = info.st_mtimespec.tv_sec; modifiedNanoseconds = info.st_mtimespec.tv_nsec
        changedSeconds = info.st_ctimespec.tv_sec; changedNanoseconds = info.st_ctimespec.tv_nsec
    }
    func validate(_ info: stat) throws {
        guard info.st_mode & S_IFMT == S_IFREG, info.st_dev == device, info.st_ino == inode, info.st_size == size,
              info.st_mtimespec.tv_sec == modifiedSeconds, info.st_mtimespec.tv_nsec == modifiedNanoseconds,
              info.st_ctimespec.tv_sec == changedSeconds, info.st_ctimespec.tv_nsec == changedNanoseconds else { throw DropSpaceError.invalid("Transfer source changed; prepare it again") }
    }
}
public struct PreparedTransfer: Sendable {
    public let manifest: TransferManifest
    public let sources: [UUID: TransferSource]
}
public actor TransferSourcePreparation {
    public init() {}
    public func prepare(_ urls: [URL], limits: TransferLimits = .init(), filenames: TransferFilenameRules = .windows) throws -> PreparedTransfer {
        try Self.prepareCore(urls, limits: limits, filenames: filenames)
    }
    private static func prepareCore(_ urls: [URL], limits: TransferLimits, filenames: TransferFilenameRules) throws -> PreparedTransfer {
        try limits.validate(); guard !urls.isEmpty else { throw DropSpaceError.invalid("Select at least one transfer source") }
        var visits = 0, total: Int64 = 0, files: [(URL, String)] = []
        func visit() throws { visits += 1; guard visits <= limits.maxItems + 4096 else { throw DropSpaceError.limit("Transfer traversal entry limit") } }
        func path(_ value: String) throws -> String {
            guard value.utf16.count <= limits.maxRelativePathLength else { throw DropSpaceError.limit("Transfer relative path") }
            return try TransferManifestPolicy.normalizeRelativePath(value, maximum: limits.maxRelativePathLength, filenames: filenames)
        }
        func add(_ url: URL, relative: String, size: Int64) throws {
            guard size >= 0, files.count < limits.maxItems, size <= limits.maxTotalBytes - total else { throw DropSpaceError.limit("Transfer enumeration limit") }
            files.append((url, relative)); total += size
        }
        for url in urls {
            try Task.checkCancellation(); try visit()
            _ = try TransferFileSystem.components(url)
            let parent = try TransferFileSystem.directory(url.deletingLastPathComponent()); var info = stat()
            guard fstatat(parent.raw, url.lastPathComponent, &info, AT_SYMLINK_NOFOLLOW) == 0 else { throw TransferFileSystem.posix() }
            guard !url.lastPathComponent.contains("\\") else { throw DropSpaceError.invalid("DropLink v1 cannot represent a literal backslash in a file name") }
            let relative = try path(TransferManifestPolicy.safeDisplayName(url.lastPathComponent))
            if info.st_mode & S_IFMT == S_IFREG { try add(url, relative: relative, size: info.st_size); continue }
            guard info.st_mode & S_IFMT == S_IFDIR else { throw DropSpaceError.invalid("Transfer root is a link or special file") }
            guard files.count < limits.maxItems, total < limits.maxTotalBytes else { throw DropSpaceError.limit("Transfer enumeration limit") }
            var pending = [(url, relative)], visited = Set<String>()
            while let (directoryURL, relative) = pending.popLast() {
                let directory = try TransferFileSystem.directory(directoryURL), identity = try directory.info()
                guard visited.insert("\(identity.st_dev):\(identity.st_ino)").inserted else { continue }
                try TransferFileSystem.entries(directory) { name, entry in
                    try visit(); if entry.st_mode & S_IFMT == S_IFLNK { return }
                    guard !name.contains("\\") else { throw DropSpaceError.invalid("DropLink v1 cannot represent a literal backslash in a file name") }
                    let child = directoryURL.appendingPathComponent(name), childPath = try path(relative + "/" + name)
                    if entry.st_mode & S_IFMT == S_IFDIR { pending.append((child, childPath)) }
                    else if entry.st_mode & S_IFMT == S_IFREG { try add(child, relative: childPath, size: entry.st_size) }
                    else { throw DropSpaceError.invalid("Transfer contains a special file") }
                }
            }
        }
        guard !files.isEmpty else { throw DropSpaceError.invalid("The transfer contains no regular files") }
        var items: [TransferItemManifest] = [], sources: [UUID: TransferSource] = [:]; total = 0
        for (url, relative) in files {
            let file = try TransferFileSystem.file(url), source = try TransferSource(url: url, info: file.info())
            guard source.size <= limits.maxTotalBytes - total else { throw DropSpaceError.limit("Transfer size after source revalidation") }
            let hash = try TransferFileSystem.hash(file, size: source.size); try source.validate(file.info()); total += source.size
            let item = TransferItemManifest(name: try TransferManifestPolicy.safeDisplayName(url.lastPathComponent), path: relative, size: source.size, hash: hash, mime: Self.mime(url), chunks: Int32(source.size == 0 ? 0 : 1 + (source.size - 1) / Int64(limits.chunkBytes)))
            items.append(item); sources[item.id] = source
        }
        return .init(manifest: try TransferManifestPolicy.create(items: items, limits: limits, filenames: filenames), sources: sources)
    }
    private static func mime(_ url: URL) -> String {
        switch url.pathExtension.lowercased() {
        case "txt", "md", "json", "csv": "text/plain"
        case "png": "image/png"
        case "jpg", "jpeg": "image/jpeg"
        case "gif": "image/gif"
        case "pdf": "application/pdf"
        default: "application/octet-stream"
        }
    }
}
