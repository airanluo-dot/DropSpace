import Foundation
import Darwin
import CryptoKit
import DropSpaceCore

/// Plaintext custody is separate from user sources. A durable share reservation
/// precedes this directory; cleanup accepts only its marker and numbered files.
actor InternetShareStaging {
    private let root: URL, id: UUID, count: Int
    private let parent: TransferDescriptor, directory: TransferDescriptor
    private let identity: stat
    private var snapshots: [Int: TransferSource] = [:]
    init(root: URL, id: UUID, count: Int) throws {
        guard id != .zero, (1...100).contains(count) else { throw DropSpaceError.invalid("Share staging reservation") }
        self.root = root; self.id = id; self.count = count
        parent = try TransferFileSystem.directory(root, create: true)
        guard mkdirat(parent.raw, id.dropLinkN, 0o700) == 0 else { throw TransferFileSystem.posix() }
        directory = try TransferDescriptor(openat(parent.raw, id.dropLinkN, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC))
        identity = try directory.info()
        let owner = try TransferDescriptor(openat(directory.raw, "owner", O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600))
        try TransferFileSystem.write(Self.marker(id, count), to: owner)
        guard fsync(owner.raw) == 0, fsync(directory.raw) == 0, fsync(parent.raw) == 0 else { throw TransferFileSystem.posix() }
    }
    func stage(_ sources: [ShareSource]) throws {
        guard sources.count == count, snapshots.isEmpty else { throw DropSpaceError.invalid("Share staging input count") }
        try ownership()
        for (index, source) in sources.enumerated() {
            try Task.checkCancellation(); try source.validate()
            let output = try TransferDescriptor(openat(directory.raw, Self.name(index), O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600))
            let input: TransferDescriptor?, original: TransferSource?
            switch source.storage {
            case .file(let url):
                let file = try TransferFileSystem.file(url), info = try file.info()
                guard info.st_size == source.length else { throw DropSpaceError.invalid("Share source length changed") }
                input = file; original = TransferSource(url: url, info: info)
            case .bytes: input = nil; original = nil
            }
            var digest = SHA256(), offset: Int64 = 0
            while offset < source.length {
                try Task.checkCancellation()
                let length = Int(min(81920, source.length - offset))
                var bytes: Data
                if let input { bytes = try TransferFileSystem.read(input, offset: offset, count: length) }
                else if case .bytes(let data) = source.storage { bytes = data.subdata(in: Int(offset)..<(Int(offset) + length)) }
                else { throw DropSpaceError.invalid("Share source reader") }
                defer { bytes.resetBytes(in: bytes.startIndex..<bytes.endIndex) }
                digest.update(data: bytes); try TransferFileSystem.write(bytes, to: output); offset += Int64(length)
            }
            if let input, let original { try original.validate(input.info()) }
            let hash = digest.finalize().map { String(format: "%02x", $0) }.joined()
            guard hash == source.sha256, try output.info().st_size == source.length else { throw DropSpaceError.invalid("Share source changed during staging") }
            guard fsync(output.raw) == 0 else { throw TransferFileSystem.posix() }
            snapshots[index] = try TransferSource(url: root.appendingPathComponent(id.dropLinkN).appendingPathComponent(Self.name(index)), info: output.info())
        }
        guard fsync(directory.raw) == 0 else { throw TransferFileSystem.posix() }; try ownership()
    }
    func read(index: Int, offset: Int64, count: Int) throws -> Data {
        try ownership()
        guard let snapshot = snapshots[index], offset >= 0, count >= 0, count <= ShareLimits.chunkPlainBytes,
              offset <= snapshot.size, Int64(count) <= snapshot.size - offset else { throw DropSpaceError.invalid("Share staged range") }
        let file = try TransferDescriptor(openat(directory.raw, Self.name(index), O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK))
        try snapshot.validate(file.info())
        let data = try TransferFileSystem.read(file, offset: offset, count: count); try snapshot.validate(file.info()); return data
    }
    private func ownership() throws {
        let current = try TransferFileSystem.directory(root.appendingPathComponent(id.dropLinkN)), info = try current.info()
        guard info.st_dev == identity.st_dev, info.st_ino == identity.st_ino else { throw DropSpaceError.invalid("Share staging replaced") }
        try Self.checkMarker(directory, id: id, count: count)
    }
    private static func marker(_ id: UUID, _ count: Int) -> Data { Data("DropSpace Internet Share v1\n\(id.dropLinkN)\n\(count)\n".utf8) }
    private static func name(_ index: Int) -> String { String(format: "%04d.payload", index) }
    private static func checkMarker(_ directory: TransferDescriptor, id: UUID, count: Int) throws {
        let owner = try TransferDescriptor(openat(directory.raw, "owner", O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)), info = try owner.info(), expected = marker(id, count)
        guard info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1, info.st_size == expected.count,
              try TransferFileSystem.read(owner, offset: 0, count: expected.count) == expected else { throw DropSpaceError.invalid("Share staging owner") }
    }
    /// Called only for a persisted reservation, on a separate IO actor. Empty
    /// or partially written marker-only directories recover interrupted init;
    /// unknown children or replaced markers remain pending, never recursive-delete.
    static func cleanup(root: URL, id: UUID, count: Int) throws {
        guard id != .zero, (1...100).contains(count) else { throw DropSpaceError.invalid("Share cleanup reservation") }
        let parent: TransferDescriptor
        do { parent = try TransferFileSystem.directory(root) } catch let error as POSIXError where error.code == .ENOENT { return }
        let raw = openat(parent.raw, id.dropLinkN, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        if raw < 0, errno == ENOENT { return }
        let directory = try TransferDescriptor(raw), identity = try directory.info()
        var entries: [(String, stat)] = []
        try TransferFileSystem.entries(directory) { name, info in
            guard entries.count <= count, info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1,
                  name == "owner" || (0..<count).contains(where: { Self.name($0) == name }) else { throw DropSpaceError.invalid("Unknown share staging content") }
            entries.append((name, info))
        }
        if !entries.isEmpty {
            if entries.count == 1, entries[0].0 == "owner" {
                let file = try TransferDescriptor(openat(directory.raw, "owner", O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)), info = try file.info(), expected = marker(id, count)
                guard info.st_size <= expected.count, info.st_size >= 0,
                      try TransferFileSystem.read(file, offset: 0, count: Int(info.st_size)) == expected.prefix(Int(info.st_size)) else { throw DropSpaceError.invalid("Incomplete share staging owner") }
            } else { try checkMarker(directory, id: id, count: count) }
        }
        // Keep owner until all payloads have gone so a crash remains retryable.
        for (name, previous) in entries.sorted(by: { $0.0 != "owner" && $1.0 == "owner" }) {
            var current = stat()
            guard fstatat(directory.raw, name, &current, AT_SYMLINK_NOFOLLOW) == 0,
                  current.st_dev == previous.st_dev, current.st_ino == previous.st_ino, current.st_nlink == 1,
                  current.st_mode & S_IFMT == S_IFREG else { throw DropSpaceError.invalid("Share staging changed during cleanup") }
            guard unlinkat(directory.raw, name, 0) == 0 else { throw TransferFileSystem.posix() }
        }
        var current = stat()
        guard fstatat(parent.raw, id.dropLinkN, &current, AT_SYMLINK_NOFOLLOW) == 0, current.st_dev == identity.st_dev,
              current.st_ino == identity.st_ino, current.st_mode & S_IFMT == S_IFDIR else { throw DropSpaceError.invalid("Share staging directory replaced") }
        guard unlinkat(parent.raw, id.dropLinkN, AT_REMOVEDIR) == 0, fsync(parent.raw) == 0 else { throw TransferFileSystem.posix() }
    }
}

actor InternetShareStagingCleanup {
    func remove(root: URL, id: UUID, count: Int) throws { try InternetShareStaging.cleanup(root: root, id: id, count: count) }
}
