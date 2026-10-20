import Foundation
import Darwin

public enum AtomicFile {
    /// Publishes a complete sibling temporary file without replacing an existing destination.
    public static func writeNew(_ data: Data,to destination: URL) throws {
        let temporary = destination.deletingLastPathComponent().appendingPathComponent(".dropspace-" + UUID().uuidString + ".tmp")
        let fd = open(temporary.path,O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW,0o600)
        guard fd >= 0 else { throw posix() }
        defer { close(fd); unlink(temporary.path) }
        try data.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                try Task.checkCancellation()
                let count = Darwin.write(fd,buffer.baseAddress!.advanced(by: offset),min(1024 * 1024,buffer.count - offset))
                if count < 0 { if errno == EINTR { continue }; throw posix() }
                guard count > 0 else { throw POSIXError(.EIO) }; offset += count
            }
        }
        guard fsync(fd) == 0 else { throw posix() }
        try Task.checkCancellation()
        guard renamex_np(temporary.path,destination.path,UInt32(RENAME_EXCL)) == 0 else { throw posix() }
    }
    private static func posix() -> POSIXError { POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
}
