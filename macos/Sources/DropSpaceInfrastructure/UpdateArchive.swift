import Foundation
import Security
import DropSpaceCore

/// Our release archives have one regular .app tree, no ZIP64, links or external paths.
/// Inspect the central directory before allowing the system extractor to write files.
public enum UpdateArchive {
    public static func inspect(_ url: URL, maximumExpandedBytes: UInt64 = 4 * 1024 * 1024 * 1024) throws {
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        let size = try file.seekToEnd()
        guard size >= 22, size <= 2 * 1024 * 1024 * 1024 else { throw invalid() }
        let tailSize = min(size, 65557)
        try file.seek(toOffset: size - tailSize)
        let tail = try read(file, Int(tailSize))
        var end: Int?
        for offset in stride(from: tail.count - 22, through: 0, by: -1) {
            if u32(tail, offset) == 0x06054b50, offset + 22 + Int(u16(tail, offset + 20)) == tail.count {
                end = offset; break
            }
        }
        guard let end, u16(tail, end + 4) == 0, u16(tail, end + 6) == 0,
              u16(tail, end + 8) == u16(tail, end + 10) else { throw invalid() }
        let count = Int(u16(tail, end + 10))
        let directorySize = UInt64(u32(tail, end + 12))
        let directoryOffset = UInt64(u32(tail, end + 16))
        guard count > 0, count < 65535, directorySize <= 32 * 1024 * 1024,
              directoryOffset + directorySize == size - tailSize + UInt64(end) else { throw invalid() }
        try file.seek(toOffset: directoryOffset)
        let directory = try read(file, Int(directorySize))
        var offset = 0
        var expanded: UInt64 = 0
        var names = Set<String>()
        for _ in 0..<count {
            guard offset + 46 <= directory.count, u32(directory, offset) == 0x02014b50 else { throw invalid() }
            let flags = u16(directory, offset + 8)
            let compression = u16(directory, offset + 10)
            let compressed = UInt64(u32(directory, offset + 20))
            let length = UInt64(u32(directory, offset + 24))
            let nameLength = Int(u16(directory, offset + 28))
            let extraLength = Int(u16(directory, offset + 30))
            let commentLength = Int(u16(directory, offset + 32))
            let localOffset = UInt64(u32(directory, offset + 42))
            let mode = u32(directory, offset + 38) >> 16
            guard flags & 1 == 0, [0, 8].contains(compression),
                  u16(directory, offset + 34) == 0,
                  mode & 0o170000 != 0o120000,
                  mode & 0o170000 == 0 || [0o040000, 0o100000].contains(mode & 0o170000),
                  nameLength > 0, offset + 46 + nameLength + extraLength + commentLength <= directory.count,
                  localOffset + 30 + compressed <= directoryOffset,
                  length <= maximumExpandedBytes - expanded else { throw invalid() }
            let nameData = directory.subdata(in: (offset + 46)..<(offset + 46 + nameLength))
            guard let name = String(data: nameData, encoding: .utf8),
                  name.hasPrefix("DropSpace.app/"), !name.contains("\\"),
                  !name.unicodeScalars.contains(where: { $0.value < 32 }),
                  name.split(separator: "/", omittingEmptySubsequences: false).dropLast(name.hasSuffix("/") ? 1 : 0).allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." }),
                  names.insert(name.trimmingCharacters(in: CharacterSet(charactersIn: "/")).precomposedStringWithCanonicalMapping.lowercased()).inserted else { throw invalid() }
            // Do not let a local-header path differ from the inspected central entry.
            try file.seek(toOffset: localOffset)
            let local = try read(file, 30)
            guard u32(local, 0) == 0x04034b50, u16(local, 6) == flags, u16(local, 8) == compression,
                  Int(u16(local, 26)) == nameLength,
                  localOffset + 30 + UInt64(nameLength) + UInt64(u16(local, 28)) + compressed <= directoryOffset,
                  try read(file, nameLength) == nameData else { throw invalid() }
            expanded += length
            offset += 46 + nameLength + extraLength + commentLength
        }
        guard offset == directory.count, names.contains("dropspace.app/contents/info.plist"),
              names.contains("dropspace.app/contents/macos/dropspace") else { throw invalid() }
    }

    private static func read(_ file: FileHandle, _ length: Int) throws -> Data {
        guard let bytes = try file.read(upToCount: length), bytes.count == length else { throw invalid() }
        return bytes
    }
    private static func u16(_ bytes: Data, _ offset: Int) -> UInt16 {
        UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8
    }
    private static func u32(_ bytes: Data, _ offset: Int) -> UInt32 {
        UInt32(u16(bytes, offset)) | UInt32(u16(bytes, offset + 2)) << 16
    }
    private static func invalid() -> DropSpaceError { .invalid("Unsafe or unsupported macOS update archive") }
}

public enum AppBundleIntegrity {
    public static func verify(_ url: URL, manifest: MacUpdateManifest? = nil) throws {
        let values = try url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard values.isDirectory == true, values.isSymbolicLink != true,
              let info = try PropertyListSerialization.propertyList(from: Data(contentsOf: url.appendingPathComponent("Contents/Info.plist")), format: nil) as? [String: Any],
              info["CFBundleIdentifier"] as? String == Baseline.bundleID,
              info["CFBundleExecutable"] as? String == "DropSpace" else {
            throw DropSpaceError.invalid("Unexpected application identity")
        }
        if let manifest {
            guard info["DropSpaceReleaseTag"] as? String == manifest.tag,
                  info["CFBundleVersion"] as? String == manifest.bundleVersion,
                  info["LSMinimumSystemVersion"] as? String == manifest.minimumOS else {
                throw DropSpaceError.invalid("Application metadata does not match update manifest")
            }
        }
        let executable = try FileHandle(forReadingFrom: url.appendingPathComponent("Contents/MacOS/DropSpace"))
        defer { try? executable.close() }
        guard let header = try executable.read(upToCount: 8), Array(header) == [0xcf, 0xfa, 0xed, 0xfe, 0x0c, 0x00, 0x00, 0x01] else {
            throw DropSpaceError.invalid("Update executable is not native arm64 Mach-O")
        }
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code else {
            throw DropSpaceError.invalid("Cannot inspect application signature")
        }
        let flags = SecCSFlags(rawValue: kSecCSStrictValidate | kSecCSCheckAllArchitectures | kSecCSCheckNestedCode)
        guard SecStaticCodeCheckValidity(code, flags, nil) == errSecSuccess else {
            throw DropSpaceError.invalid("Application signature integrity check failed")
        }
        // This intentionally verifies local ad-hoc integrity, never Apple identity or notarization.
    }
}
