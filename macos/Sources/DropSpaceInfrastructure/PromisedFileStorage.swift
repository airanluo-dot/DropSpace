import Foundation
import Darwin
import DropSpaceCore

public struct PromisedFileEntry: Sendable {
    public let url: URL
    public let title: String
    public let kind: ItemKind
    public let bytes: Int64
    fileprivate let identity: PromiseFileIdentity
}
fileprivate struct PromiseFileIdentity: Sendable {
    let device: Int32, inode: UInt64, size: Int64, type: mode_t
    let modifiedSeconds: Int, modifiedNanoseconds: Int, changedSeconds: Int, changedNanoseconds: Int
    init(_ info: stat) {
        device = info.st_dev; inode = info.st_ino; size = info.st_size; type = info.st_mode & S_IFMT
        modifiedSeconds = info.st_mtimespec.tv_sec; modifiedNanoseconds = info.st_mtimespec.tv_nsec
        changedSeconds = info.st_ctimespec.tv_sec; changedNanoseconds = info.st_ctimespec.tv_nsec
    }
    func matches(_ info: stat) -> Bool {
        device == info.st_dev && inode == info.st_ino && size == info.st_size && type == info.st_mode & S_IFMT
            && modifiedSeconds == info.st_mtimespec.tv_sec && modifiedNanoseconds == info.st_mtimespec.tv_nsec
            && changedSeconds == info.st_ctimespec.tv_sec && changedNanoseconds == info.st_ctimespec.tv_nsec
    }
}

extension PayloadStore {
    public func createPromiseStaging(_ lease: OwnedImportLease) throws -> URL {
        let parent = try TransferFileSystem.directory(directory, create: true)
        guard mkdirat(parent.raw, lease.stagingName, 0o700) == 0 else { throw TransferFileSystem.posix() }
        guard fsync(parent.raw) == 0 else { throw TransferFileSystem.posix() }
        return directory.appendingPathComponent(lease.stagingName, isDirectory: true)
    }

    /// AppKit owns the producer's write. Only completed, confined results enter
    /// the workspace; no external URL or symbolic-link ancestor can be adopted.
    public func inspectPromises(_ urls: [URL], lease: OwnedImportLease) throws -> [PromisedFileEntry] {
        guard (1...PromisedFileRules.maximumItems).contains(urls.count) else { throw PromisedFileError.countLimit }
        let staging = directory.appendingPathComponent(lease.stagingName, isDirectory: true).standardizedFileURL
        let stage = try TransferFileSystem.directory(staging)
        var seen = Set<String>(), total: Int64 = 0
        var result: [PromisedFileEntry] = []
        for url in urls {
            try Task.checkCancellation()
            let normalized = url.standardizedFileURL
            // A promised root must be a direct child. Nested directory contents
            // travel under that root; a provider cannot claim another batch.
            guard normalized.deletingLastPathComponent() == staging,
                  seen.insert(normalized.lastPathComponent).inserted else {
                throw PromisedFileError.invalidResult
            }
            var info = stat()
            guard fstatat(stage.raw, normalized.lastPathComponent, &info, AT_SYMLINK_NOFOLLOW) == 0 else { throw TransferFileSystem.posix() }
            let type = info.st_mode & S_IFMT
            guard type == S_IFREG || type == S_IFDIR else { throw PromisedFileError.invalidResult }
            var bytes: Int64 = 0
            func count(_ value: stat) throws {
                guard value.st_size >= 0, value.st_size <= PromisedFileRules.maximumFileBytes - bytes else {
                    throw PromisedFileError.fileLimit
                }
                bytes += value.st_size
            }
            func walk(_ folder: TransferDescriptor) throws {
                try TransferFileSystem.entries(folder) { name, value in
                    switch value.st_mode & S_IFMT {
                    case S_IFREG: try count(value)
                    case S_IFDIR:
                        let child = try TransferDescriptor(openat(folder.raw, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC))
                        try walk(child)
                    case S_IFLNK: break // Retained as a link; never traversed or counted as its target.
                    default: throw PromisedFileError.invalidResult
                    }
                }
            }
            if type == S_IFREG { try count(info) }
            else {
                let folder = try TransferDescriptor(openat(stage.raw, normalized.lastPathComponent, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC))
                try walk(folder)
            }
            guard bytes <= PromisedFileRules.maximumBatchBytes - total else { throw PromisedFileError.batchLimit }
            total += bytes
            result.append(.init(url: normalized, title: normalized.lastPathComponent, kind: type == S_IFDIR ? .folder : .file,
                                bytes: bytes, identity: .init(info)))
        }
        return result
    }

    public func adoptPromise(_ entry: PromisedFileEntry, lease: OwnedImportLease, name: String) throws -> URL {
        try Task.checkCancellation()
        let destination = try url(name)
        let stageURL = directory.appendingPathComponent(lease.stagingName, isDirectory: true).standardizedFileURL
        guard entry.url.deletingLastPathComponent() == stageURL else { throw PromisedFileError.invalidResult }
        let stage = try TransferFileSystem.directory(stageURL)
        let parent = try TransferFileSystem.directory(directory)
        var info = stat()
        guard fstatat(stage.raw, entry.url.lastPathComponent, &info, AT_SYMLINK_NOFOLLOW) == 0 else { throw TransferFileSystem.posix() }
        guard entry.identity.matches(info) else { throw PromisedFileError.invalidResult }
        guard renameatx_np(stage.raw, entry.url.lastPathComponent, parent.raw, name, UInt32(RENAME_EXCL)) == 0 else { throw TransferFileSystem.posix() }
        guard fsync(stage.raw) == 0, fsync(parent.raw) == 0 else { throw TransferFileSystem.posix() }
        return destination
    }
}
