import Foundation
import Darwin
import CZlib
import CryptoKit
import DropSpaceCore

/// Reads ZIP entries itself: an external extractor never gets an unchecked path,
/// link, declared length or decompression budget. Windows package limits retained.
struct ModuleArchive {
    struct Entry {
        let name: String
        let method: UInt16
        let crc: UInt32
        let compressed: Int
        let size: Int
        let dataOffset: UInt64
        let directory: Bool
    }
    let handle: FileHandle
    let entries: [Entry]
    static let maximumExpanded = 512 * 1024 * 1024
    static let maximumManifest = 512 * 1024

    init(url: URL) throws {
        let descriptor = open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK)
        guard descriptor >= 0 else { throw Self.invalid("ModuleArchiveUnavailable") }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        do {
            var info = stat()
            guard fstat(descriptor, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_size >= 22,
                  info.st_size <= ModuleCatalogRules.maximumArchiveBytes else { throw Self.invalid("ModuleArchiveSize") }
            let size = UInt64(info.st_size), tailSize = min(size, 65557)
            let tail = try Self.read(handle, offset: size - tailSize, length: Int(tailSize))
            guard let end = stride(from: tail.count - 22, through: 0, by: -1).first(where: {
                Self.u32(tail, $0) == 0x06054b50 && $0 + 22 + Int(Self.u16(tail, $0 + 20)) == tail.count
            }) else { throw Self.invalid("ModuleArchiveDirectory") }
            let count = Int(Self.u16(tail, end + 10)), directorySize = Int(Self.u32(tail, end + 12)), directoryOffset = UInt64(Self.u32(tail, end + 16))
            guard Self.u16(tail, end + 4) == 0, Self.u16(tail, end + 6) == 0, Self.u16(tail, end + 8) == count,
                  (2...257).contains(count), directorySize <= 1024 * 1024,
                  directoryOffset + UInt64(directorySize) == size - tailSize + UInt64(end) else { throw Self.invalid("ModuleArchiveInventory") }
            let directory = try Self.read(handle, offset: directoryOffset, length: directorySize)
            var entries: [Entry] = [], names = Set<String>(), offset = 0, expanded = 0
            var occupied: [Range<UInt64>] = []
            for _ in 0..<count {
                try Task.checkCancellation()
                guard offset + 46 <= directory.count, Self.u32(directory, offset) == 0x02014b50 else { throw Self.invalid("ModuleArchiveDirectory") }
                let flags = Self.u16(directory, offset + 8), method = Self.u16(directory, offset + 10)
                let compressed = Int(Self.u32(directory, offset + 20)), length = Int(Self.u32(directory, offset + 24))
                let nameLength = Int(Self.u16(directory, offset + 28)), extra = Int(Self.u16(directory, offset + 30)), comment = Int(Self.u16(directory, offset + 32))
                let attributes = Self.u32(directory, offset + 38), kind = attributes >> 16 & 0xf000
                let localOffset = UInt64(Self.u32(directory, offset + 42))
                guard flags & ~UInt16(0x080e) == 0, [0, 8].contains(method), Self.u16(directory, offset + 34) == 0,
                      [0, 0x8000, 0x4000].contains(kind), attributes & 0x400 == 0,
                      nameLength > 0, offset + 46 + nameLength + extra + comment <= directory.count,
                      length <= Self.maximumExpanded + Self.maximumManifest - expanded else { throw Self.invalid("ModuleArchiveUnsafeEntry") }
                let nameBytes = directory.subdata(in: offset + 46..<offset + 46 + nameLength)
                guard let name = String(data: nameBytes, encoding: .utf8) else { throw Self.invalid("ModulePayloadPathInvalid") }
                let isDirectory = name.hasSuffix("/")
                try Self.validatePath(name, directory: isDirectory)
                guard names.insert(Self.canonical(name.trimmingCharacters(in: CharacterSet(charactersIn: "/")))).inserted,
                      kind != 0x4000 || isDirectory, kind != 0x8000 || !isDirectory,
                      !isDirectory || length == 0, localOffset + 30 <= directoryOffset else { throw Self.invalid("ModuleArchiveUnsafeEntry") }
                let local = try Self.read(handle, offset: localOffset, length: 30)
                let dataOffset = localOffset + 30 + UInt64(nameLength) + UInt64(Self.u16(local, 28))
                guard Self.u32(local, 0) == 0x04034b50, Self.u16(local, 6) == flags, Self.u16(local, 8) == method,
                      Int(Self.u16(local, 26)) == nameLength, dataOffset + UInt64(compressed) <= directoryOffset,
                      try Self.read(handle, offset: localOffset + 30, length: nameLength) == nameBytes else { throw Self.invalid("ModuleArchiveLocalHeader") }
                if flags & 8 == 0 {
                    guard Self.u32(local, 14) == Self.u32(directory, offset + 16), Int(Self.u32(local, 18)) == compressed,
                          Int(Self.u32(local, 22)) == length else { throw Self.invalid("ModuleArchiveLocalHeader") }
                }
                let range = localOffset..<dataOffset + UInt64(compressed)
                guard !occupied.contains(where: { $0.overlaps(range) }) else { throw Self.invalid("ModuleArchiveOverlappingEntries") }
                occupied.append(range)
                entries.append(.init(name: name, method: method, crc: Self.u32(directory, offset + 16), compressed: compressed, size: length, dataOffset: dataOffset, directory: isDirectory))
                expanded += length; offset += 46 + nameLength + extra + comment
            }
            guard offset == directory.count else { throw Self.invalid("ModuleArchiveDirectory") }
            self.handle = handle; self.entries = entries
        } catch { try? handle.close(); throw error }
    }

    func manifest(package: OfficialModulePackage) throws -> (ModuleManifest, Data) {
        guard let entry = entries.first(where: { $0.name == "manifest.json" }), (1...Self.maximumManifest).contains(entry.size) else { throw Self.invalid("ModuleManifestMissing") }
        var data = Data()
        try stream(entry) { data.append($0) }
        try ModuleStoredJSON.validate(data)
        let manifest = try ModuleCodec.manifest(data)
        guard manifest.id == package.id, manifest.version == package.version, (1...128).contains(manifest.files.count) else { throw Self.invalid("ModuleManifestIdentity") }
        var declared: [String: ModuleFile] = [:], expanded: Int64 = 0
        for file in manifest.files {
            try Self.validatePath(file.path, directory: false)
            let name = Self.canonical(file.path), suffix = URL(fileURLWithPath: file.path).pathExtension.lowercased()
            guard !["manifest.json", ".package.zip", ".package-record.json"].contains(name), declared[name] == nil,
                  ["", "dylib", "json", "txt", "md", "dat", "bin", "png", "ico", "icns", "metallib"].contains(suffix),
                  file.bytes >= 0, file.bytes <= Self.maximumExpanded, ModuleCatalogRules.validHash(file.sha256) else { throw Self.invalid("ModuleFileDeclaration") }
            expanded += file.bytes; guard expanded <= Self.maximumExpanded else { throw Self.invalid("ModuleExpandedSizeLimit") }
            declared[name] = file
        }
        let actual = entries.filter { !$0.directory && $0.name != "manifest.json" }
        guard actual.count == declared.count,
              actual.allSatisfy({ declared[Self.canonical($0.name)]?.bytes == Int64($0.size) }),
              entries.filter(\.directory).allSatisfy({ directory in manifest.files.contains { $0.path.hasPrefix(directory.name) } }) else { throw Self.invalid("ModuleArchiveInventory") }
        // A file cannot also be an ancestor of another entry on APFS.
        let files = Set(entries.filter { !$0.directory }.map { Self.canonical($0.name) })
        for entry in entries {
            let parts = entry.name.split(separator: "/")
            for count in 1..<max(1, parts.count) {
                guard !files.contains(Self.canonical(parts.prefix(count).joined(separator: "/"))) else { throw Self.invalid("ModuleArchivePathCollision") }
            }
        }
        return (manifest, data)
    }

    func stream(_ entry: Entry, consume: (Data) throws -> Void) throws {
        try handle.seek(toOffset: entry.dataOffset)
        var remaining = entry.compressed, written = 0, checksum = crc32(0, nil, 0), inflater = z_stream()
        if entry.method == 8 {
            guard inflateInit2_(&inflater, -MAX_WBITS, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { throw Self.invalid("ModuleInflateUnavailable") }
        }
        defer { if entry.method == 8 { inflateEnd(&inflater) } }
        var finished = entry.method == 0
        func output(_ data: Data) throws {
            try Task.checkCancellation()
            guard data.count <= entry.size - written else { throw Self.invalid("ModuleExpandedSizeLimit") }
            written += data.count
            checksum = data.withUnsafeBytes { crc32(checksum, $0.bindMemory(to: Bytef.self).baseAddress, uInt(data.count)) }
            try consume(data)
        }
        while remaining > 0 {
            try Task.checkCancellation()
            let size = min(131072, remaining)
            guard let bytes = try handle.read(upToCount: size), bytes.count == size else { throw Self.invalid("ModuleArchiveTruncated") }
            remaining -= size
            if entry.method == 0 { try output(bytes); continue }
            guard !finished else { throw Self.invalid("ModuleCompressedTrailingBytes") }
            try bytes.withUnsafeBytes { input in
                inflater.next_in = UnsafeMutablePointer(mutating: input.bindMemory(to: Bytef.self).baseAddress); inflater.avail_in = uInt(bytes.count)
                var buffer = [UInt8](repeating: 0, count: 131072)
                repeat {
                    let before = inflater.avail_in
                    let code = buffer.withUnsafeMutableBufferPointer { chunk -> Int32 in
                        inflater.next_out = chunk.baseAddress; inflater.avail_out = uInt(chunk.count); return inflate(&inflater, Z_NO_FLUSH)
                    }
                    let produced = buffer.count - Int(inflater.avail_out)
                    if produced > 0 { try output(Data(buffer.prefix(produced))) }
                    if code == Z_STREAM_END {
                        guard inflater.avail_in == 0, remaining == 0 else { throw Self.invalid("ModuleCompressedTrailingBytes") }
                        finished = true; break
                    }
                    // A full output buffer may exhaust this input chunk before
                    // zlib can see the next block. Refill instead of rejecting it.
                    if code == Z_BUF_ERROR, inflater.avail_in == 0, produced == 0 { break }
                    guard code == Z_OK, produced > 0 || inflater.avail_in < before else { throw Self.invalid("ModuleCompressedInvalid") }
                } while inflater.avail_in > 0 || inflater.avail_out == 0
            }
        }
        guard finished, written == entry.size, UInt32(checksum) == entry.crc else { throw Self.invalid("ModuleExpandedIntegrity") }
    }
    static func validatePath(_ name: String, directory: Bool) throws {
        guard !name.isEmpty, name.utf16.count <= 240, !name.hasPrefix("/"), !name.contains("\\"),
              !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) || ":*?<>|\"".unicodeScalars.contains($0) }) else { throw invalid("ModulePayloadPathInvalid") }
        let trimmed = directory ? String(name.dropLast()) : name
        for part in trimmed.components(separatedBy: "/") {
            let device = part.components(separatedBy: ".")[0].trimmingCharacters(in: .init(charactersIn: " ")).uppercased()
            guard !["", ".", ".."].contains(part), !part.hasSuffix(" "), !part.hasSuffix("."),
                  !["CON", "PRN", "AUX", "NUL"].contains(device),
                  !(device.count == 4 && (device.hasPrefix("COM") || device.hasPrefix("LPT")) && "123456789¹²³".contains(device.last!)) else { throw invalid("ModulePayloadPathInvalid") }
        }
    }
    static func canonical(_ value: String) -> String { value.precomposedStringWithCanonicalMapping.lowercased() }
    static func invalid(_ code: String) -> DropSpaceError { .invalid(code) }
    private static func read(_ handle: FileHandle, offset: UInt64, length: Int) throws -> Data {
        try handle.seek(toOffset: offset)
        guard let data = try handle.read(upToCount: length), data.count == length else { throw invalid("ModuleArchiveTruncated") }; return data
    }
    private static func u16(_ bytes: Data, _ index: Int) -> UInt16 { UInt16(bytes[index]) | UInt16(bytes[index + 1]) << 8 }
    private static func u32(_ bytes: Data, _ index: Int) -> UInt32 { UInt32(u16(bytes, index)) | UInt32(u16(bytes, index + 2)) << 16 }
}
