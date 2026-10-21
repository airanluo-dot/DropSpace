import AppKit
import Foundation
import ImageIO
import UniformTypeIdentifiers
import DropSpaceCore
import DropSpaceInfrastructure

public struct IntakeResult: Sendable {
    public var items: [DropItem] = []
    public var failures: [String] = []
    public init() {}
}
public enum FileIntake {
    public static func prepare(_ urls: [URL], source: ItemSource, settings: AppSettings) throws -> IntakeResult {
        var batch = try Batch(count: urls.count, source: source, settings: settings)
        for input in urls {
            try Task.checkCancellation()
            if batch.stopped { break }
            do { batch.accept(try inspect(input, source: source)) }
            catch is CancellationError { throw CancellationError() }
            catch { batch.reject(input, error: error) }
        }
        return batch.finish()
    }
    struct Candidate: Sendable {
        let item: DropItem
        let caseSensitive: Bool
    }
    /// Metadata failure is a retained reference state, not an intake rejection.
    /// Invalid non-file inputs and scheduler timeouts remain per-input failures.
    static func inspect(_ input: URL, source: ItemSource) throws -> Candidate {
        guard input.isFileURL, !input.path.utf8.contains(0) else { throw DropSpaceError.invalid("Only valid file URLs are accepted") }
        let url = input.standardizedFileURL
        var item = DropItem(source: source, kind: .file, title: url.lastPathComponent.isEmpty ? url.path : url.lastPathComponent, file: .init(path: url.path))
        var caseSensitive = true
        do {
            let values = try url.resourceValues(forKeys: [.isDirectoryKey, .fileSizeKey, .volumeUUIDStringKey, .nameKey, .volumeSupportsCaseSensitiveNamesKey])
            caseSensitive = values.volumeSupportsCaseSensitiveNames ?? true
            item.kind = values.isDirectory == true ? .folder : .file
            item.title = values.name ?? item.title
            // Directory inode sizes are neither file bytes nor a recursive total.
            item.file?.size = item.kind == .folder ? nil : values.fileSize.map(Int64.init)
            item.file?.volumeID = values.volumeUUIDString
            // A bookmark is a native relocation aid. Failure to create one must
            // not discard an otherwise usable path reference.
            item.file?.bookmark = try? url.bookmarkData(options: .minimalBookmark, includingResourceValuesForKeys: [.volumeUUIDStringKey], relativeTo: nil)
        } catch {
            let reason = availabilityReason(error)
            item.status = reason == .missing ? .missing : .unavailable
            item.file?.availabilityReason = reason
        }
        item.file?.lastCheckedAt = Date()
        return Candidate(item: item, caseSensitive: caseSensitive)
    }
    struct Batch {
        let source: ItemSource
        let settings: AppSettings
        private let id = UUID()
        private var result = IntakeResult()
        private var seen = Set<String>()
        private var total: Int64 = 0
        private(set) var stopped: Bool
        init(count: Int, source: ItemSource, settings: AppSettings) throws {
            if source == .clipboard, settings.captureFiles, count > settings.maxClipboardFileItems { throw DropSpaceError.limit("File selection exceeds the configured item limit") }
            self.source = source; self.settings = settings
            stopped = source == .clipboard && !settings.captureFiles
        }
        mutating func accept(_ candidate: Candidate) {
            guard !stopped, let reference = candidate.item.file else { return }
            guard seen.insert(candidate.caseSensitive ? reference.path : reference.path.lowercased()).inserted else { return }
            if source == .space, seen.count > 2048 {
                result.failures.append("The manual file batch exceeds 2,048 items"); stopped = true; return
            }
            if source == .clipboard {
                if candidate.item.kind == .folder && !settings.captureFolders { return }
                if let size = reference.size {
                    guard size <= settings.maxClipboardFileBytes, size <= settings.maxClipboardFileTotalBytes - total else {
                        result.failures.append(candidate.item.title + ": File selection exceeds the configured byte limit"); return
                    }
                    total += size
                }
            }
            var item = candidate.item; item.batchID = id
            if source == .clipboard { item.fingerprint = Digests.sha256(Data(("clipboard-file\0" + reference.path).utf8)) }
            result.items.append(item)
        }
        mutating func reject(_ input: URL, error: any Error) { result.failures.append(input.lastPathComponent + ": " + error.localizedDescription) }
        func finish() -> IntakeResult {
            var value = result
            for index in value.items.indices {
                value.items[index].batchIndex = index; value.items[index].batchCount = value.items.count
                value.items[index].batchAcquisition = source == .space ? "file-drop" : "clipboard-files"
            }
            return value
        }
    }
    public static func resolve(_ reference: FileReference, expectedKind: ItemKind? = nil) throws -> URL {
        if let bookmark = reference.bookmark {
            var stale = false
            if let resolved = try? URL(resolvingBookmarkData: bookmark,options: [.withoutUI,.withoutMounting],relativeTo: nil,bookmarkDataIsStale: &stale) {
                do { return try verifiedFile(resolved, reference: reference, expectedKind: expectedKind) }
                catch { if availabilityReason(error) != .missing { throw error } }
            }
        }
        let url = URL(fileURLWithPath: reference.path)
        return try verifiedFile(url, reference: reference, expectedKind: expectedKind)
    }
    private static func verifiedFile(_ url: URL, reference: FileReference, expectedKind: ItemKind?) throws -> URL {
        let values = try url.resourceValues(forKeys: [.isDirectoryKey, .volumeUUIDStringKey])
        if let expected = reference.volumeID {
            guard values.volumeUUIDString == expected else { throw FileAvailabilityReason.volumeUnavailable }
        }
        if let expectedKind, (values.isDirectory == true) != (expectedKind == .folder) {
            throw FileAvailabilityReason.typeChanged
        }
        return url
    }
    static func availabilityReason(_ error: any Error) -> FileAvailabilityReason {
        if let reason = error as? FileAvailabilityReason { return reason }
        let value = error as NSError
        if value.domain == NSCocoaErrorDomain {
            if [NSFileNoSuchFileError, NSFileReadNoSuchFileError].contains(value.code) { return .missing }
            if value.code == NSFileReadNoPermissionError { return .accessDenied }
        }
        if value.domain == NSPOSIXErrorDomain {
            if value.code == Int(ENOENT) || value.code == Int(ENOTDIR) { return .missing }
            if value.code == Int(EACCES) || value.code == Int(EPERM) { return .accessDenied }
        }
        if let underlying = value.userInfo[NSUnderlyingErrorKey] as? NSError { return availabilityReason(underlying) }
        return .storageUnavailable
    }
    static func availability(_ reference: FileReference, kind: ItemKind) -> FileAvailability {
        do { return .init(status: .available, resolvedURL: try resolve(reference, expectedKind: kind)) }
        catch {
            let reason = availabilityReason(error)
            return .init(status: reason == .missing ? .missing : .unavailable, reason: reason)
        }
    }
    public static func text(_ value: String, source: ItemSource, settings: AppSettings, fingerprint: String? = nil) throws -> DropItem {
        guard !value.isEmpty, value.utf16.count <= settings.maxTextCharacters else { throw DropSpaceError.limit("Text exceeds the configured limit") }
        let normalized = try ContentRules.normalizedBody(value)
        return DropItem(source: source,kind: ItemPolicy.classify(normalized),title: ContentRules.title(normalized),text: normalized,fingerprint: fingerprint ?? Digests.sha256(Data(normalized.utf8)))
    }
    public static func handoff(_ message: HandoffMessage, settings: AppSettings) throws -> DropItem {
        try HandoffMessagePolicy.validate(message)
        var item = try text(message.utf8Payload, source: .space, settings: settings)
        item.batchID = UUID(); item.batchIndex = 0; item.batchCount = 1; item.batchAcquisition = "device-handoff"
        return item
    }
    public static func validateImage(_ data: Data, settings: AppSettings) throws { _ = try imageInfo(data, settings: settings) }
    public static func imageInfo(_ data: Data, settings: AppSettings) throws -> ClipboardImageInfo {
        guard data.count <= settings.maxImageBytes, let source = CGImageSourceCreateWithData(data as CFData,nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source,0,nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int64,
              let height = properties[kCGImagePropertyPixelHeight] as? Int64,
              width > 0, height > 0, width <= settings.maxImagePixels / height else {
            throw DropSpaceError.limit("Image is invalid or exceeds the configured byte/pixel limit")
        }
        guard let identifier = CGImageSourceGetType(source) as String?, let type = UTType(identifier), type.conforms(to: .image),
              let suffix = type.preferredFilenameExtension, suffix.range(of: "^[a-zA-Z0-9]{1,12}$", options: .regularExpression) != nil,
              let mime = type.preferredMIMEType, mime.lowercased().hasPrefix("image/") else { throw DropSpaceError.invalid("Unsupported clipboard image format") }
        return .init(suffix: suffix, mime: mime, typeIdentifier: identifier, width: width, height: height)
    }
}
