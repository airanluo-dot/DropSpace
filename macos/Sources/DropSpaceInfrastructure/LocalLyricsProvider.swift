import Foundation
import Darwin
import DropSpaceCore

public protocol LyricsSource: Sendable {
    var kind: LyricsProvider { get }
    func query(_ query: LyricsQuery) async throws -> LyricsDocument
    func query(_ query: LyricsQuery, progress: LyricsSourceProgress) async throws -> LyricsDocument
    func clearResponseCache() async
}
public extension LyricsSource {
    func clearResponseCache() async {}
    func query(_ query: LyricsQuery, progress: LyricsSourceProgress) async throws -> LyricsDocument {
        try await progress.prepare(self.query(query), query: query)
    }
}
public struct LyricsSourceProgress: Sendable {
    private let identifier: (any LyricsLanguageIdentifying)?
    private let report: @Sendable (LyricsDocument) async -> Void
    public init(identifier: (any LyricsLanguageIdentifying)? = nil, report: @escaping @Sendable (LyricsDocument) async -> Void = { _ in }) {
        self.identifier = identifier; self.report = report
    }
    public func prepare(_ document: LyricsDocument, query: LyricsQuery) async throws -> LyricsDocument {
        try Task.checkCancellation(); var value = document
        if let identifier, !query.preferredTranslationLanguage.isEmpty, !value.lines.isEmpty {
            value = try await identifier.prepare(value, target: query.preferredTranslationLanguage)
        }
        try Task.checkCancellation()
        if LyricsSelection.usable(value) { await report(value) }
        return value
    }
}

public struct LocalLyricsProvider: LyricsSource {
    public let kind = LyricsProvider.localLrc
    public let directory: URL
    public init(directory: URL) { self.directory = directory }
    public func query(_ query: LyricsQuery) async throws -> LyricsDocument {
        let task = Task.detached(priority: .utility) { try read(query) }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    private func read(_ query: LyricsQuery) throws -> LyricsDocument {
        let empty = LyricsDocument(lines: [], provider: .localLrc)
        guard let enumerator = FileManager.default.enumerator(at: directory, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey], options: [.skipsSubdirectoryDescendants], errorHandler: { _, _ in true }) else { return empty }
        var paths: [URL] = []
        for case let file as URL in enumerator {
            try Task.checkCancellation()
            guard file.pathExtension.lowercased() == "lrc",
                  let values = try? file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey]), values.isRegularFile == true, values.isSymbolicLink != true else { continue }
            paths.append(file); if paths.count >= 8192 { break }
        }
        let title = LyricsMatcher.normalize(query.title)
        let exact = Array(paths.filter { LyricsMatcher.normalize($0.deletingPathExtension().lastPathComponent) == title }.prefix(512))
        let seen = Set(exact.map(\.path))
        let candidates = exact + paths.filter { !seen.contains($0.path) }.prefix(max(0, 512 - exact.count))
        var best = empty, bestScore: Double = 0
        for path in candidates {
            try Task.checkCancellation()
            do {
                let text = try readBounded(path)
                if text.isEmpty { continue }
                let fileTitle = path.deletingPathExtension().lastPathComponent
                var title = fileTitle, artist = "", album = "", hasTitle = false
                for match in Self.metadata.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                    let name = (text as NSString).substring(with: match.range(at: 1)).lowercased(), value = (text as NSString).substring(with: match.range(at: 2))
                    switch name { case "ti": title = value; hasTitle = true; case "ar": artist = value; case "al": album = value; default: break }
                }
                if artist.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, let separator = fileTitle.range(of: " - ") {
                    let first = String(fileTitle[..<separator.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
                    let second = String(fileTitle[separator.upperBound...]).trimmingCharacters(in: .whitespacesAndNewlines)
                    if !first.isEmpty && !second.isEmpty { artist = first; if !hasTitle { title = second } }
                }
                let score = LyricsMatcher.score(query, title: title, artist: artist, album: album, duration: 0)
                guard score > bestScore, score >= 4 else { continue }
                let parsed = try LyricsParser.parse(text, provider: kind)
                if !parsed.lines.isEmpty {
                    bestScore = score; best = parsed.binding(.init(title: title, artist: artist, album: album, duration: 0, score: score, candidateID: path.path), to: query)
                }
            } catch is CancellationError { throw CancellationError() }
            catch { continue } // One unreadable/malformed candidate does not hide a later match.
        }
        return best
    }
    private func readBounded(_ path: URL) throws -> String {
        let fd = open(path.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true); defer { try? handle.close() }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_size <= LyricsParser.maximumCharacters else { throw DropSpaceError.limit("Local lyric file size/type") }
        var bytes = Data()
        while let chunk = try handle.read(upToCount: 16384), !chunk.isEmpty {
            try Task.checkCancellation(); bytes.append(chunk)
            guard bytes.count <= LyricsParser.maximumCharacters else { throw DropSpaceError.limit("Local lyric file grew beyond limit") }
        }
        // StreamReader's BOM detection includes UTF-8/16/32. Default remains UTF-8.
        let encoding: String.Encoding
        if bytes.starts(with: [0xff, 0xfe, 0, 0]) { encoding = .utf32LittleEndian; bytes.removeFirst(4) }
        else if bytes.starts(with: [0, 0, 0xfe, 0xff]) { encoding = .utf32BigEndian; bytes.removeFirst(4) }
        else if bytes.starts(with: [0xff, 0xfe]) { encoding = .utf16LittleEndian; bytes.removeFirst(2) }
        else if bytes.starts(with: [0xfe, 0xff]) { encoding = .utf16BigEndian; bytes.removeFirst(2) }
        else { encoding = .utf8; if bytes.starts(with: [0xef, 0xbb, 0xbf]) { bytes.removeFirst(3) } }
        // StreamReader uses replacement fallback. A malformed code unit does not
        // discard the otherwise matching file or modify its original bytes.
        let text: String
        switch encoding {
        case .utf16LittleEndian, .utf16BigEndian:
            let raw = Array(bytes), little = encoding == .utf16LittleEndian
            var units: [UInt16] = []; units.reserveCapacity((raw.count + 1) / 2)
            for offset in stride(from: 0, to: raw.count - raw.count % 2, by: 2) {
                units.append(little ? UInt16(raw[offset]) | UInt16(raw[offset + 1]) << 8 : UInt16(raw[offset]) << 8 | UInt16(raw[offset + 1]))
            }
            if raw.count % 2 != 0 { units.append(0xfffd) }
            text = String(decoding: units, as: UTF16.self)
        case .utf32LittleEndian, .utf32BigEndian:
            let raw = Array(bytes), little = encoding == .utf32LittleEndian
            var units: [UInt32] = []; units.reserveCapacity((raw.count + 3) / 4)
            for offset in stride(from: 0, to: raw.count - raw.count % 4, by: 4) {
                var value: UInt32 = 0
                for index in 0..<4 { value |= UInt32(raw[offset + index]) << ((little ? index : 3 - index) * 8) }
                units.append(value)
            }
            if raw.count % 4 != 0 { units.append(0xfffd) }
            text = String(decoding: units, as: UTF32.self)
        default: text = String(decoding: bytes, as: UTF8.self)
        }
        guard text.utf16.count <= LyricsParser.maximumCharacters else { throw DropSpaceError.limit("Decoded local lyric exceeds character limit") }
        return text
    }
    private static let metadata = try! NSRegularExpression(pattern: #"\[(ti|ar|al):([^\]\r\n]*)\]"#, options: .caseInsensitive)
}
