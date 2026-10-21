import AppKit
import ImageIO
import CoreImage
import UniformTypeIdentifiers
import DropSpaceCore
import DropSpaceInfrastructure
import Darwin

public actor ItemActions {
    private let payloads: PayloadStore
    private let references: FileReferenceService
    public init(payloads: PayloadStore, references: FileReferenceService = FileReferenceService()) { self.payloads = payloads; self.references = references }
    public func contentURL(_ item: DropItem) async throws -> URL {
        if let reference = item.file {
            let availability = try await references.check(reference, kind: item.kind)
            guard availability.status == .available, let url = availability.resolvedURL else {
                throw availability.reason ?? .storageUnavailable
            }
            return url
        }
        if let path = item.payloadPath { return try await payloads.url(path) }
        throw DropSpaceError.unavailable("This item has no file content")
    }
    public func hash(_ item: DropItem) async throws -> String {
        let url = try await contentURL(item)
        try Task.checkCancellation()
        let task = Task.detached(priority: .utility) { try Digests.sha256(file: url) }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    public func exportHash(_ item: DropItem,directory: URL) async throws -> (digest:String,url:URL) {
        let source = try await contentURL(item), digest = try await hash(item)
        let output = try exportText(digest + "  " + source.lastPathComponent + "\n",name:source.lastPathComponent + ".sha256",directory:directory)
        return (digest,output)
    }
    public func exportText(_ text: String, name: String, directory: URL) throws -> URL {
        try reserveAndWrite(Data(text.utf8),name: name,extension: "txt",directory: directory)
    }
    public func exportOwnedImage(_ item: DropItem, to destination: URL) async throws {
        guard item.kind == .image, let path = item.payloadPath else { throw DropSpaceError.invalid("Only owned clipboard images can be exported") }
        let source = try await payloads.url(path)
        try Task.checkCancellation()
        let task = Task.detached(priority: .utility) {
            let manager = FileManager.default
            guard source.resolvingSymlinksInPath().standardizedFileURL != destination.resolvingSymlinksInPath().standardizedFileURL else { throw DropSpaceError.invalid("The export cannot replace its source") }
            if let existing = try? manager.attributesOfItem(atPath: destination.path), let original = try? manager.attributesOfItem(atPath: source.path),
               let inode = existing[.systemFileNumber] as? NSNumber, let device = existing[.systemNumber] as? NSNumber,
               inode == original[.systemFileNumber] as? NSNumber, device == original[.systemNumber] as? NSNumber { throw DropSpaceError.invalid("The export cannot replace its source") }
            let staging = destination.deletingLastPathComponent().appendingPathComponent(".dropspace-export-" + UUID().uuidString)
            defer { try? manager.removeItem(at: staging) }
            try Task.checkCancellation()
            try manager.copyItem(at: source, to: staging)
            let handle = try FileHandle(forWritingTo: staging)
            do { try handle.synchronize(); try handle.close() } catch { try? handle.close(); throw error }
            try Task.checkCancellation()
            guard rename(staging.path, destination.path) == 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        }
        try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    public func imageSize(_ item: DropItem) async throws -> (width:Int,height:Int) {
        let url = try await contentURL(item)
        try Task.checkCancellation()
        guard let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= ImageRules.maximumEncodedBytes,
              let source = CGImageSourceCreateWithURL(url as CFURL,nil),let properties = CGImageSourceCopyPropertiesAtIndex(source,0,nil) as? [CFString:Any],
              let w = properties[kCGImagePropertyPixelWidth] as? Int,let h = properties[kCGImagePropertyPixelHeight] as? Int else { throw DropSpaceError.invalid("Image dimensions unavailable") }
        try ImageRules.validate(width:w,height:h)
        return (5...8).contains(properties[kCGImagePropertyOrientation] as? Int ?? 1) ? (h,w) : (w,h)
    }
    public func qr(_ value: String, name: String = "DropSpace QR", directory: URL) throws -> URL {
        guard ImageRules.canEncodeQR(value), let filter = CIFilter(name: "CIQRCodeGenerator") else { throw DropSpaceError.limit("QR payload exceeds limit") }
        filter.setValue(Data(value.utf8),forKey: "inputMessage"); filter.setValue("Q",forKey: "inputCorrectionLevel")
        guard let output = filter.outputImage?.transformed(by: .init(scaleX: 8,y: 8)), let code = CIContext().createCGImage(output,from: output.extent),
              let context = CGContext(data:nil,width:code.width + 64,height:code.height + 64,bitsPerComponent:8,bytesPerRow:0,space:CGColorSpaceCreateDeviceRGB(),bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue) else { throw DropSpaceError.unavailable("QR generation failed") }
        context.setFillColor(CGColor(red:1,green:1,blue:1,alpha:1)); context.fill(CGRect(x:0,y:0,width:code.width + 64,height:code.height + 64))
        context.interpolationQuality = .none; context.draw(code,in:CGRect(x:32,y:32,width:code.width,height:code.height))
        guard let image = context.makeImage() else { throw DropSpaceError.unavailable("QR export failed") }
        return try encode(image,type: .png,properties: [:],name: (name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "DropSpace QR" : name) + ".qr",directory: directory)
    }
    public func image(_ item: DropItem, format: String, width: Int?, height: Int?, stripMetadata: Bool, keepAspectRatio: Bool = true, outputSuffix: String? = nil, directory: URL) async throws -> URL {
        let url = try await contentURL(item)
        try Task.checkCancellation()
        let task = Task.detached(priority: .utility) {
            try Task.checkCancellation()
            guard let size = try url.resourceValues(forKeys:[.fileSizeKey]).fileSize, size <= ImageRules.maximumEncodedBytes,
                  let source = CGImageSourceCreateWithURL(url as CFURL,nil), let properties = CGImageSourceCopyPropertiesAtIndex(source,0,nil) as? [CFString:Any],
                  let w = properties[kCGImagePropertyPixelWidth] as? Int, let h = properties[kCGImagePropertyPixelHeight] as? Int else { throw DropSpaceError.invalid("Image dimensions are unavailable or exceed limit") }
            try ImageRules.validate(width:w,height:h)
            guard (width == nil) == (height == nil),["png","jpeg","bmp"].contains(format) else { throw DropSpaceError.invalid("Provide both dimensions and a supported format") }
            let orientation = properties[kCGImagePropertyOrientation] as? Int ?? 1
            let rotated = (5...8).contains(orientation)
            let naturalW = rotated ? h : w, naturalH = rotated ? w : h
            let target = try ImageRules.fitted(sourceWidth:naturalW,sourceHeight:naturalH,width:width ?? naturalW,height:height ?? naturalH,keepAspectRatio:keepAspectRatio)
            let targetW = target.width, targetH = target.height
            let options: [CFString:Any] = [kCGImageSourceCreateThumbnailFromImageAlways: true,kCGImageSourceCreateThumbnailWithTransform: true,kCGImageSourceThumbnailMaxPixelSize: max(w,h)]
            guard let original = CGImageSourceCreateThumbnailAtIndex(source,0,options as CFDictionary),
                  let context = CGContext(data: nil,width: targetW,height: targetH,bitsPerComponent: 8,bytesPerRow: 0,space: CGColorSpaceCreateDeviceRGB(),bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { throw DropSpaceError.unavailable("Image decode failed") }
            try Task.checkCancellation()
            if format == "jpeg" { context.setFillColor(CGColor(red: 1,green: 1,blue: 1,alpha: 1)); context.fill(CGRect(x: 0,y: 0,width: targetW,height: targetH)) }
            context.interpolationQuality = .high; context.draw(original,in: CGRect(x: 0,y: 0,width: targetW,height: targetH))
            guard let image = context.makeImage() else { throw DropSpaceError.unavailable("Image transform failed") }
            let type: UTType = switch format { case "jpeg": .jpeg; case "bmp": .bmp; default: .png }
            // Beta7 re-encodes pixels and omits arbitrary metadata for every transform.
            var metadata: [CFString:Any] = [:]
            metadata[kCGImagePropertyOrientation] = 1; metadata[kCGImagePropertyPixelWidth] = targetW; metadata[kCGImagePropertyPixelHeight] = targetH
            let name = URL(fileURLWithPath: item.title.isEmpty ? url.lastPathComponent : item.title).deletingPathExtension().lastPathComponent
            return try Self.encodeImage(image,type: type,properties: metadata,name: name + (outputSuffix.map { "-" + $0 } ?? ""),directory: directory)
        }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }
    private func encode(_ image: CGImage,type: UTType,properties: [CFString:Any],name: String,directory: URL) throws -> URL {
        try Self.encodeImage(image,type: type,properties: properties,name: name,directory: directory)
    }
    private static func encodeImage(_ image: CGImage,type: UTType,properties: [CFString:Any],name: String,directory: URL) throws -> URL {
        try Task.checkCancellation()
        let bytes = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(bytes,type.identifier as CFString,1,nil) else { throw DropSpaceError.unavailable("Image encoder unavailable") }
        CGImageDestinationAddImage(destination,image,properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { throw DropSpaceError.unavailable("Image export failed") }
        guard bytes.length <= ImageRules.maximumEncodedBytes else { throw DropSpaceError.limit("Encoded image exceeds limit") }
        try Task.checkCancellation()
        return try writeUnique(bytes as Data,name: name,extension: type == .jpeg ? "jpg" : type.preferredFilenameExtension ?? "png",directory: directory)
    }
    private func reserveAndWrite(_ bytes: Data,name: String,extension suffix: String,directory: URL) throws -> URL {
        try Self.writeUnique(bytes,name: name,extension: suffix,directory: directory)
    }
    public static func writeUnique(_ bytes: Data,name: String,extension suffix: String,directory: URL) throws -> URL {
        try FileManager.default.createDirectory(at: directory,withIntermediateDirectories: true)
        try Task.checkCancellation()
        let safe = ActionOutputNames.stem(name, extension: suffix)
        for index in 0..<ActionOutputNames.maximumCollisionAttempts {
            try Task.checkCancellation()
            let output = directory.appendingPathComponent(safe + (index == 0 ? "" : " (\(index))") + "." + suffix)
            do { try AtomicFile.writeNew(bytes,to: output); return output }
            catch let error as POSIXError where error.code == .EEXIST { continue }
        }
        throw DropSpaceError.storage("No available export filename")
    }
    public func zip(_ items: [DropItem], directory: URL) async throws -> URL {
        guard !items.isEmpty else { throw DropSpaceError.invalid("Empty selection") }
        let staging = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-" + UUID().uuidString,isDirectory: true)
        try FileManager.default.createDirectory(at: staging,withIntermediateDirectories: true,attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: staging) }
        var intake = ArchiveStaging(root:staging)
        for item in items {
            try Task.checkCancellation()
            let source = try await contentURL(item)
            try intake.add(source,name:ArchiveStaging.safeName(item.title.isEmpty ? source.lastPathComponent : item.title))
        }
        let archive = staging.deletingLastPathComponent().appendingPathComponent(UUID().uuidString + ".zip")
        defer { try? FileManager.default.removeItem(at: archive) }
        try await ProcessRunner.run(executable: URL(fileURLWithPath: "/usr/bin/ditto"),arguments: ["-c","-k","--norsrc","--noextattr","--noqtn",staging.path,archive.path])
        return try Self.writeUnique(Data(contentsOf: archive,options: .mappedIfSafe),name: items.count == 1 ? items[0].title : "DropSpace files",extension: "zip",directory: directory)
    }
}
