import Foundation
import CryptoKit
import Darwin
import DropSpaceCore

struct DropLinkBufferedBody: Sendable {
    let data: Data
    let sha256: String
    let wasSpooled: Bool
}

/// Authentication buffers at most 64 KiB in RAM before spilling to a private,
/// immediately unlinked file. Its descriptor owns cleanup even on process exit.
/// A completed large body is mapped read-only for the JSON/file route to consume.
actor DropLinkBodyBuffer {
    private let threshold: Int
    private let maximum: Int
    private let directory: URL
    private var memory = Data()
    private var file: FileHandle?
    private var hash = SHA256()
    private var count = 0
    private var finished = false
    init(maximum: Int, threshold: Int = 65536, directory: URL = FileManager.default.temporaryDirectory) {
        self.maximum = maximum; self.threshold = max(0, threshold); self.directory = directory
    }
    var storage: (bytes: Int, inMemory: Int, spooled: Bool) { (count, memory.count, file != nil) }
    func append(_ bytes: Data) throws {
        try Task.checkCancellation()
        guard !finished else { throw DropSpaceError.invalid("Closed DropLink request body") }
        guard bytes.count <= maximum - count else { throw DropSpaceError.limit("DropLink request body") }
        if file == nil, bytes.count > threshold - memory.count {
            let path = directory.appendingPathComponent(".DropSpace-DropLink-" + UUID().uuidString).path
            let fd = open(path, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
            guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
            let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
            // Never retain a pathname with user data across crash/restart. Mapping
            // later uses the descriptor; unlinking does not invalidate that owner.
            guard unlink(path) == 0 else { let error = POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO); try? handle.close(); throw error }
            file = handle; try handle.write(contentsOf: memory); memory = Data()
        }
        if let file { try file.write(contentsOf: bytes) } else { memory.append(bytes) }
        hash.update(data: bytes); count += bytes.count
    }
    func finish() throws -> DropLinkBufferedBody {
        try Task.checkCancellation()
        guard !finished else { throw DropSpaceError.invalid("Closed DropLink request body") }
        let spooled = file != nil, data: Data
        if let file, count > 0 {
            let address = mmap(nil, count, PROT_READ, MAP_PRIVATE, file.fileDescriptor, 0)
            guard address != MAP_FAILED, let address else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .ENOMEM) }
            data = Data(bytesNoCopy: address, count: count, deallocator: .custom { pointer, size in munmap(pointer, size) })
        } else { data = memory }
        let digest = hash.finalize().map { String(format: "%02x", $0) }.joined()
        close()
        return .init(data: data, sha256: digest, wasSpooled: spooled)
    }
    func close() { finished = true; try? file?.close(); file = nil; memory = Data() }
}
