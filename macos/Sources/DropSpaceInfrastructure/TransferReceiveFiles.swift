import Foundation
import Darwin
import CryptoKit
import DropSpaceCore

public struct TransferPublicationFailure: Error, Sendable {
    public let relativePath: String
    public let reason: String
}
/// One actor owns the staging descriptor and its receipt marker. Only known
/// fragment names in this lease can be removed. Published Downloads files are
/// never reclaimed by cleanup, even if later files fail verification.
actor TransferReceiveFiles {
    private let manifest: TransferManifest
    private let stagingURL: URL
    private let parent: TransferDescriptor
    private let directory: TransferDescriptor
    private let destination: URL
    private let marker: Data
    private var cleaned = false
    init(parentURL: URL, manifest: TransferManifest, destination: URL, existing: Bool = false) throws {
        self.manifest = manifest; self.destination = destination
        let parent = try TransferFileSystem.directory(parentURL, create: true); self.parent = parent
        stagingURL = parentURL.appendingPathComponent(manifest.sessionId.dropLinkN)
        if !existing { guard mkdirat(parent.raw, manifest.sessionId.dropLinkN, 0o700) == 0 else { throw TransferFileSystem.posix() } }
        do {
            let directory = try TransferDescriptor(openat(parent.raw, manifest.sessionId.dropLinkN, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)); self.directory = directory
            let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
            marker = Data((manifest.sessionId.dropLinkD + "\n" + Digests.sha256(try encoder.encode(manifest))).utf8)
            if existing {
                let receipt = try TransferDescriptor(openat(directory.raw, "owner", O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK))
                let info = try receipt.info()
                guard info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1, info.st_size == marker.count,
                      try TransferFileSystem.read(receipt, offset: 0, count: marker.count) == marker else { throw DropSpaceError.invalid("Transfer recovery ownership") }
            } else {
                let receipt = try TransferDescriptor(openat(directory.raw, "owner", O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600))
                try TransferFileSystem.write(marker, to: receipt)
                guard fsync(receipt.raw) == 0, fsync(directory.raw) == 0, fsync(parent.raw) == 0 else { throw TransferFileSystem.posix() }
            }
        } catch {
            // A partially created lease remains visible to recovery; do not
            // recursively remove a directory without a verified owner marker.
            throw error
        }
    }
    private func checkOwnership() throws {
        guard !cleaned else { throw DropSpaceError.unavailable("Transfer staging retired") }
        let reopened = try TransferFileSystem.directory(stagingURL)
        let original = try directory.info(), current = try reopened.info()
        guard original.st_dev == current.st_dev, original.st_ino == current.st_ino else { throw DropSpaceError.invalid("Transfer staging replaced") }
        let receipt = try TransferDescriptor(openat(directory.raw, "owner", O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK))
        let info = try receipt.info()
        guard info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1, info.st_size == marker.count,
              try TransferFileSystem.read(receipt, offset: 0, count: marker.count) == marker else { throw DropSpaceError.invalid("Transfer staging ownership") }
    }
    private func part(_ item: UUID, _ index: Int32) -> String { item.dropLinkN + "-" + String(index) + ".part" }
    private func length(_ item: TransferItemManifest, _ index: Int32) throws -> Int {
        guard let count = item.chunkCount, index >= 0, index < count else { throw DropSpaceError.invalid("Transfer chunk index") }
        return Int(min(Int64(manifest.chunkBytes), item.size - Int64(index) * Int64(manifest.chunkBytes)))
    }
    func hasChunk(item: TransferItemManifest, index: Int32) throws -> Bool {
        try checkOwnership()
        var info = stat()
        guard fstatat(directory.raw, part(item.id, index), &info, AT_SYMLINK_NOFOLLOW) == 0 else {
            if errno == ENOENT { return false }; throw TransferFileSystem.posix()
        }
        guard info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1, info.st_size == (try length(item, index)) else {
            throw DropSpaceError.invalid("Unsafe existing transfer chunk")
        }
        return true
    }
    func write(item: TransferItemManifest, index: Int32, body: Data, hash: String) throws -> Bool {
        try checkOwnership()
        guard body.count == (try length(item, index)) else { throw DropSpaceError.invalid("chunk-length-mismatch") }
        guard hash.utf8.count == 64, hash.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }), Digests.sha256(body) == hash else { throw DropSpaceError.invalid("chunk-integrity") }
        let name = part(item.id, index); var old = stat()
        if fstatat(directory.raw, name, &old, AT_SYMLINK_NOFOLLOW) == 0 {
            guard old.st_mode & S_IFMT == S_IFREG, old.st_nlink == 1, old.st_size == body.count else { throw DropSpaceError.invalid("Unsafe existing transfer chunk") }
            let retained = try TransferDescriptor(openat(directory.raw, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK))
            guard try TransferFileSystem.hash(retained, size: old.st_size) == hash else { throw DropSpaceError.invalid("Retained transfer chunk integrity") }
            return false
        }
        guard errno == ENOENT else { throw TransferFileSystem.posix() }
        let temporary = ".part-" + UUID().dropLinkN
        let output = try TransferDescriptor(openat(directory.raw, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600))
        defer { unlinkat(directory.raw, temporary, 0) }
        try TransferFileSystem.write(body, to: output); try Task.checkCancellation()
        guard fsync(output.raw) == 0 else { throw TransferFileSystem.posix() }
        guard linkat(directory.raw, temporary, directory.raw, name, 0) == 0 else { throw TransferFileSystem.posix() }
        guard unlinkat(directory.raw, temporary, 0) == 0, fsync(directory.raw) == 0 else { throw TransferFileSystem.posix() }
        return true
    }
    func commit(_ item: TransferItemManifest) throws -> String {
        try checkOwnership(); try Task.checkCancellation()
        let path = try TransferManifestPolicy.normalizeRelativePath(item.relativePath, filenames: .macOS)
        let target = destination.appendingPathComponent(path), parentURL = target.deletingLastPathComponent()
        let outputParent = try TransferFileSystem.directory(parentURL, create: true)
        let temporary = ".dropspace-" + UUID().dropLinkN + ".tmp"
        let output = try TransferDescriptor(openat(outputParent.raw, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600))
        defer { unlinkat(outputParent.raw, temporary, 0) }
        var digest = SHA256()
        for index in 0..<(item.chunkCount ?? 0) {
            try Task.checkCancellation()
            let input = try TransferDescriptor(openat(directory.raw, part(item.id, index), O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK))
            let info = try input.info(), count = try length(item, index)
            guard info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1, info.st_size == count else { throw DropSpaceError.invalid("Transfer fragment changed") }
            var offset = 0
            while offset < count {
                let bytes = try TransferFileSystem.read(input, offset: Int64(offset), count: min(81920, count - offset))
                digest.update(data: bytes); try TransferFileSystem.write(bytes, to: output); offset += bytes.count
            }
        }
        let hash = digest.finalize().map { String(format: "%02x", $0) }.joined()
        guard hash == item.sha256.lowercased(), try output.info().st_size == item.size else { throw DropSpaceError.invalid("Transfer file integrity") }
        guard fsync(output.raw) == 0 else { throw TransferFileSystem.posix() }
        try Task.checkCancellation(); try checkOwnership()
        let currentParent = try TransferFileSystem.directory(parentURL), before = try outputParent.info(), current = try currentParent.info()
        guard before.st_dev == current.st_dev, before.st_ino == current.st_ino else { throw DropSpaceError.invalid("Transfer destination moved") }
        // linkat provides create-without-replacement semantics on the actual
        // filesystem, including case/Unicode collisions on APFS.
        guard linkat(outputParent.raw, temporary, outputParent.raw, target.lastPathComponent, 0) == 0 else { throw TransferFileSystem.posix() }
        guard unlinkat(outputParent.raw, temporary, 0) == 0, fsync(outputParent.raw) == 0 else {
            throw TransferPublicationFailure(relativePath: path, reason: String(describing: TransferFileSystem.posix()))
        }
        return path
    }
    func cleanup() throws {
        if cleaned { return }; try checkOwnership()
        var artifacts: [(String, stat)] = [], links: [String: Int] = [:]
        let maximum = manifest.items.reduce(64) { $0 + Int($1.chunkCount ?? 0) }
        try TransferFileSystem.entries(directory) { name, info in
            if name == "owner" { return }
            let pieces = name.split(separator: "-")
            let knownPart = pieces.count == 2 && pieces[1].hasSuffix(".part") && manifest.items.contains { item in
                item.id.dropLinkN == pieces[0] && Int32(pieces[1].dropLast(5)).map { $0 >= 0 && $0 < (item.chunkCount ?? 0) } == true
            }
            let temporaryID = name.dropFirst(6)
            let knownTemporary = name.hasPrefix(".part-") && temporaryID.utf8.count == 32 && temporaryID.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
            guard (knownPart || knownTemporary), info.st_mode & S_IFMT == S_IFREG else { throw DropSpaceError.invalid("Transfer cleanup contains an unknown or linked artifact") }
            artifacts.append((name, info))
            guard artifacts.count <= maximum else { throw DropSpaceError.limit("Transfer cleanup inventory") }
            links["\(info.st_dev):\(info.st_ino)", default: 0] += 1
        }
        // Crash between linkat and unlinkat can leave two owned names for one
        // fragment. Permit links only when every link is inside this inventory.
        for (_, info) in artifacts {
            guard links["\(info.st_dev):\(info.st_ino)"] == Int(info.st_nlink) else { throw DropSpaceError.invalid("Transfer cleanup artifact has external hard links") }
        }
        for (name, _) in artifacts { guard unlinkat(directory.raw, name, 0) == 0 || errno == ENOENT else { throw TransferFileSystem.posix() } }
        // Preserve the marker on partial cleanup. Once files are gone, remove
        // the marker immediately before the empty lease directory itself.
        guard unlinkat(directory.raw, "owner", 0) == 0 else { throw TransferFileSystem.posix() }
        guard unlinkat(parent.raw, manifest.sessionId.dropLinkN, AT_REMOVEDIR) == 0 else {
            let error = TransferFileSystem.posix()
            if let receipt = try? TransferDescriptor(openat(directory.raw, "owner", O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)) {
                try? TransferFileSystem.write(marker, to: receipt); _ = fsync(receipt.raw)
            }
            throw error
        }
        cleaned = true
    }
}
