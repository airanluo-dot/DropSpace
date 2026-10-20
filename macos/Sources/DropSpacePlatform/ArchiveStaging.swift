import Foundation
import Darwin
import DropSpaceCore

/// Stages bounded, regular file bytes. Explicit roots reject links; nested links
/// are excluded, matching ZipActionService's root-versus-descendant distinction.
struct ArchiveStaging {
    private var entries = 0
    private var bytes: Int64 = 0
    private var names = Set<String>()
    private let root: URL
    init(root: URL) { self.root = root }
    mutating func add(_ source: URL,name: String) throws {
        var queue: [(URL,String,Bool)] = [(source,name,true)]
        var index = 0
        while index < queue.count {
            try Task.checkCancellation()
            let (source,requested,isRoot) = queue[index]; index += 1
            let values = try source.resourceValues(forKeys:[.isSymbolicLinkKey,.isDirectoryKey,.isRegularFileKey])
            if values.isSymbolicLink == true {
                if isRoot { throw DropSpaceError.invalid("Selected ZIP roots must not be symbolic links") }
                continue
            }
            guard values.isDirectory == true || values.isRegularFile == true else { throw DropSpaceError.invalid("ZIP accepts regular files and directories") }
            entries += 1; guard entries <= 10000 else { throw DropSpaceError.limit("ZIP exceeds 10000 entries") }
            let name = try unique(requested), destination = root.appendingPathComponent(name)
            if values.isDirectory == true {
                try FileManager.default.createDirectory(at:destination,withIntermediateDirectories:true)
                var enumerationFailure: (any Error)?
                guard let children = FileManager.default.enumerator(at:source,includingPropertiesForKeys:[.isSymbolicLinkKey],options:[.skipsSubdirectoryDescendants],errorHandler:{ _,error in enumerationFailure = error; return false }) else { throw DropSpaceError.unavailable("ZIP directory cannot be read") }
                for case let child as URL in children {
                    try Task.checkCancellation()
                    if try child.resourceValues(forKeys:[.isSymbolicLinkKey]).isSymbolicLink == true { continue }
                    guard entries + queue.count - index < 10000 else { throw DropSpaceError.limit("ZIP exceeds 10000 entries") }
                    queue.append((child,name + "/" + Self.safeName(child.lastPathComponent),false))
                }
                if let enumerationFailure { throw enumerationFailure }
            } else { try copyFile(source,to:destination) }
        }
    }
    static func safeName(_ name: String) -> String {
        let value = name.replacingOccurrences(of:"/",with:"_").replacingOccurrences(of:"\\",with:"_").replacingOccurrences(of:"\0",with:"_")
        return value.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty || value == "." || value == ".." ? "item" : value
    }
    private mutating func unique(_ name: String) throws -> String {
        if names.insert(name.lowercased()).inserted { return name }
        let url = URL(fileURLWithPath:name), suffix = url.pathExtension
        let stem = suffix.isEmpty ? name : String(name.dropLast(suffix.count + 1))
        for n in 1...10000 {
            let value = stem + " (\(n))" + (suffix.isEmpty ? "" : "." + suffix)
            if names.insert(value.lowercased()).inserted { return value }
        }
        throw DropSpaceError.limit("ZIP entry name collision limit")
    }
    private mutating func copyFile(_ source: URL,to destination: URL) throws {
        let fd = open(source.path,O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue:errno) ?? .EIO) }
        let input = FileHandle(fileDescriptor:fd,closeOnDealloc:true); defer { try? input.close() }
        var info = stat()
        guard fstat(fd,&info) == 0,(info.st_mode & S_IFMT) == S_IFREG else { throw DropSpaceError.invalid("ZIP source changed type") }
        let remaining = Int64(64) * 1024 * 1024 * 1024 - bytes
        guard info.st_size <= remaining else { throw DropSpaceError.limit("ZIP exceeds 64 GiB") }
        let outputFD = open(destination.path,O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW,0o600)
        guard outputFD >= 0 else { throw POSIXError(POSIXErrorCode(rawValue:errno) ?? .EIO) }
        let output = FileHandle(fileDescriptor:outputFD,closeOnDealloc:true); defer { try? output.close() }
        var copied: Int64 = 0
        while let data = try input.read(upToCount:81920),!data.isEmpty {
            try Task.checkCancellation(); copied += Int64(data.count)
            guard copied <= remaining else { throw DropSpaceError.limit("ZIP source grew beyond 64 GiB budget") }
            try output.write(contentsOf:data)
        }
        bytes += copied
    }
}
