import Foundation
import Darwin
import DropSpaceCore

public enum ShareSourceInput: Sendable {
    case file(URL), folder(URL), payload(URL, name: String, mime: String), text(String, name: String, isURL: Bool)
}
public struct ShareSource: Sendable, Identifiable {
    public let id: UUID
    public let name: String, mime: String, length: Int64, sha256: String
    let storage: Storage
    enum Storage: Sendable { case file(URL), bytes(Data) }
    init(name: String, mime: String, length: Int64, hash: String, storage: Storage) {
        id = UUID(); self.name = name; self.mime = mime; self.length = length; sha256 = hash; self.storage = storage
    }
    func validate() throws {
        guard id != .zero, !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, name.utf16.count <= 512,
              !name.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }),
              !mime.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, mime.utf16.count <= 128, !mime.contains("\r"), !mime.contains("\n"), length >= 0 else { throw DropSpaceError.invalid("Share item") }
    }
}
/// Sources remain references. Text/URL bytes are snapshotted; a URL never causes
/// a fetch. File reads are descriptor-relative and cannot follow user symlinks.
public actor ShareSourcePreparation {
    public init() {}
    public func prepare(_ inputs: [ShareSourceInput]) throws -> [ShareSource] { try Self.build(inputs) }
    private static func build(_ inputs: [ShareSourceInput]) throws -> [ShareSource] {
        guard (1...100).contains(inputs.count) else { throw DropSpaceError.limit("Share item count") }
        var result: [ShareSource] = []
        func addFile(_ url: URL, name: String, mime: String? = nil) throws {
            let file = try TransferFileSystem.file(url), info = try file.info(), source = TransferSource(url: url, info: info)
            let hash = try TransferFileSystem.hash(file, size: info.st_size); try source.validate(file.info())
            result.append(.init(name: safeName(name), mime: mime ?? guessMime(url), length: info.st_size, hash: hash, storage: .file(url)))
            guard result.count <= 100 else { throw DropSpaceError.limit("Share item count") }
        }
        for input in inputs {
            try Task.checkCancellation()
            switch input {
            case .file(let url): try addFile(url, name: url.lastPathComponent)
            case .payload(let url, let name, let mime): try addFile(url, name: name, mime: mime)
            case .text(let text, let name, let isURL):
                guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
                let bytes = Data(text.utf8)
                result.append(.init(name: safeName(name + ".txt"), mime: isURL ? "text/uri-list" : "text/plain", length: Int64(bytes.count), hash: Digests.sha256(bytes), storage: .bytes(bytes)))
            case .folder(let root):
                _ = try TransferFileSystem.directory(root)
                var queue: [(URL, Int)] = [(root, 0)], cursor = 0, entries = 0, files = 0
                while cursor < queue.count {
                    let (directoryURL, depth) = queue[cursor]; cursor += 1
                    let directory = try TransferFileSystem.directory(directoryURL)
                    try TransferFileSystem.entries(directory) { name, info in
                        entries += 1; guard entries <= 4096 else { throw DropSpaceError.limit("Share folder traversal") }
                        if info.st_mode & S_IFMT == S_IFLNK { return }
                        let url = directoryURL.appendingPathComponent(name)
                        if info.st_mode & S_IFMT == S_IFDIR {
                            guard depth < 32 else { throw DropSpaceError.limit("Share folder depth") }; queue.append((url, depth + 1))
                        } else {
                            files += 1; guard files <= 100 else { throw DropSpaceError.limit("Share folder file count") }
                            let relative = String(url.path.dropFirst(root.path.count + 1)).replacingOccurrences(of: "/", with: "_")
                            try addFile(url, name: safeName(root.lastPathComponent) + " - " + relative)
                        }
                    }
                }
            }
        }
        guard !result.isEmpty, result.count <= 100 else { throw DropSpaceError.invalid("The selected items have no shareable local payload") }
        for source in result { try source.validate() }; return result
    }
    private static func safeName(_ text: String) -> String {
        let name = text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "DropSpace item" : text.replacingOccurrences(of: "\0", with: "_").trimmingCharacters(in: .whitespacesAndNewlines)
        // Native filenames permit characters forbidden on Windows. Slash is
        // still excluded from a download filename; never split a surrogate pair.
        var value = "", count = 0
        for scalar in name.unicodeScalars where scalar != "/" {
            let width = scalar.value > 0xffff ? 2 : 1; if count + width > 512 { break }; value.unicodeScalars.append(scalar); count += width
        }
        return value
    }
    private static func guessMime(_ url: URL) -> String {
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
actor ShareSourceReader {
    private let source: ShareSource
    private var file: TransferDescriptor?
    init(_ source: ShareSource) throws {
        self.source = source
        if case .file(let url) = source.storage {
            let descriptor = try TransferFileSystem.file(url)
            guard try descriptor.info().st_size == source.length else { throw DropSpaceError.invalid("Share source length changed") }; file = descriptor
        }
    }
    func read(offset: Int64, count: Int) throws -> Data {
        try Task.checkCancellation()
        guard offset >= 0, count >= 0, offset <= source.length, Int64(count) <= source.length - offset else { throw DropSpaceError.invalid("Share source range") }
        if case .bytes(let bytes) = source.storage { return bytes.subdata(in: Int(offset)..<(Int(offset) + count)) }
        guard let file else { throw DropSpaceError.unavailable("Share source closed") }
        return try TransferFileSystem.read(file, offset: offset, count: count)
    }
    func close() { file = nil }
}
