import Foundation
import Testing
import ImageIO
import CoreGraphics
import CoreImage
import UniformTypeIdentifiers
import DropSpaceCore
import DropSpaceInfrastructure
@testable import DropSpacePlatform

@Test func actionOutputNamesPublishWithoutReplacingExistingFiles() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-action-names-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let existing = root.appendingPathComponent("_CON.txt")
    try Data("keep".utf8).write(to: existing)
    let output = try ItemActions.writeUnique(Data("new".utf8), name: " CON... ", extension: "txt", directory: root)
    #expect(output.lastPathComponent == "_CON (1).txt")
    #expect(try Data(contentsOf: existing) == Data("keep".utf8))
    #expect(try Data(contentsOf: output) == Data("new".utf8))
    let unicode = try ItemActions.writeUnique(Data("unicode".utf8), name: String(repeating: "界", count: 160), extension: "png", directory: root)
    #expect(unicode.lastPathComponent.utf8.count <= 255)
    #expect(try Data(contentsOf: unicode) == Data("unicode".utf8))
    let cancelled = Task {
        withUnsafeCurrentTask { $0?.cancel() }
        return try ItemActions.writeUnique(Data("cancelled".utf8), name: "cancelled", extension: "txt", directory: root)
    }
    await #expect(throws: CancellationError.self) { try await cancelled.value }
    #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("cancelled.txt").path))
    #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).allSatisfy { !$0.hasPrefix(".dropspace-") })
}

@Test func ownedImageExportPreservesBytesAndRejectsSourceAliases() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-export-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let payloads = PayloadStore(directory: root.appendingPathComponent("owned"))
    let bytes = Data("owned exact image payload".utf8)
    let name = try await payloads.write(bytes, extension: "png")
    let source = try await payloads.url(name)
    let item = DropItem(source: .clipboard, kind: .image, title: "image", payloadPath: name)
    let actions = ItemActions(payloads: payloads), output = root.appendingPathComponent("export.png")
    try Data("previous export".utf8).write(to: output)
    try await actions.exportOwnedImage(item, to: output)
    #expect(try Data(contentsOf: output) == bytes)
    let alias = root.appendingPathComponent("alias.png")
    try FileManager.default.linkItem(at: source, to: alias)
    await #expect(throws: (any Error).self) { try await actions.exportOwnedImage(item, to: source) }
    await #expect(throws: (any Error).self) { try await actions.exportOwnedImage(item, to: alias) }
    #expect(try Data(contentsOf: source) == bytes)
    let cancelled = Task { withUnsafeCurrentTask { $0?.cancel() }; try await actions.exportOwnedImage(item, to: output) }
    await #expect(throws: CancellationError.self) { try await cancelled.value }
    #expect(try Data(contentsOf: output) == bytes)
    #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).allSatisfy { !$0.hasPrefix(".dropspace-export-") })
}

@Test func nativeImageExportRespectsOrientationAndPreservesSource() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
    defer { try? FileManager.default.removeItem(at:root) }
    let bitmap = try #require(CGContext(data:nil,width:4,height:2,bitsPerComponent:8,bytesPerRow:0,space:CGColorSpaceCreateDeviceRGB(),bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue))
    bitmap.setFillColor(CGColor(red:1,green:0,blue:0,alpha:1)); bitmap.fill(CGRect(x:0,y:0,width:4,height:2))
    let pixels = try #require(bitmap.makeImage()), bytes = NSMutableData()
    let encoder = try #require(CGImageDestinationCreateWithData(bytes,UTType.jpeg.identifier as CFString,1,nil))
    CGImageDestinationAddImage(encoder,pixels,[kCGImagePropertyOrientation:6,kCGImagePropertyGPSDictionary:[kCGImagePropertyGPSLatitude:12.3]] as CFDictionary)
    #expect(CGImageDestinationFinalize(encoder))
    let source = root.appendingPathComponent("source.jpg"); try (bytes as Data).write(to:source)
    let item = DropItem(source:.space,kind:.file,title:"source.jpg",file:.init(path:source.path))
    let actions = ItemActions(payloads:PayloadStore(directory:root.appendingPathComponent("payloads")))
    let result = try await actions.image(item,format:"png",width:nil,height:nil,stripMetadata:true,directory:root)
    let decoded = try #require(CGImageSourceCreateWithURL(result as CFURL,nil))
    let properties = try #require(CGImageSourceCopyPropertiesAtIndex(decoded,0,nil) as? [CFString:Any])
    #expect(properties[kCGImagePropertyPixelWidth] as? Int == 2)
    #expect(properties[kCGImagePropertyPixelHeight] as? Int == 4)
    #expect(properties[kCGImagePropertyGPSDictionary] == nil)
    #expect(try Data(contentsOf:source) == bytes as Data)
    let bmp = try await actions.image(item,format:"bmp",width:2,height:2,stripMetadata:true,outputSuffix:"converted",directory:root)
    #expect(try Data(contentsOf:bmp).prefix(2) == Data("BM".utf8))
    #expect(bmp.lastPathComponent == "source-converted.bmp")
    let clean = try await actions.image(item,format:ImageRules.nativeOutputFormat(forPath: source.path),width:nil,height:nil,stripMetadata:true,outputSuffix:"clean",directory:root)
    #expect(clean.lastPathComponent == "source-clean.jpg")
    let cleanSource = try #require(CGImageSourceCreateWithURL(clean as CFURL,nil))
    #expect(CGImageSourceGetType(cleanSource) as String? == UTType.jpeg.identifier)
    #expect((CGImageSourceCopyPropertiesAtIndex(cleanSource,0,nil) as? [CFString:Any])?[kCGImagePropertyGPSDictionary] == nil)
    let qr = try await actions.qr("DropSpace native QR",directory:root)
    let detector = try #require(CIDetector(ofType:CIDetectorTypeQRCode,context:CIContext(),options:[CIDetectorAccuracy:CIDetectorAccuracyHigh]))
    let code = try #require(CIImage(contentsOf:qr))
    #expect((detector.features(in:code).first as? CIQRCodeFeature)?.messageString == "DropSpace native QR")
}

@Test func archiveStagingRejectsRootLinksPreservesEmptyFoldersAndAvoidsNameCollisions() throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at:root) }
    let source = root.appendingPathComponent("source"), stage = root.appendingPathComponent("stage")
    try FileManager.default.createDirectory(at:source.appendingPathComponent("empty"),withIntermediateDirectories:true)
    try FileManager.default.createDirectory(at:stage,withIntermediateDirectories:true)
    let file = source.appendingPathComponent("a.txt"); try Data("safe".utf8).write(to:file)
    let link = source.appendingPathComponent("link"); try FileManager.default.createSymbolicLink(at:link,withDestinationURL:file)
    var archive = ArchiveStaging(root:stage)
    try archive.add(source,name:"folder")
    try archive.add(file,name:"a.txt"); try archive.add(file,name:"A.txt")
    #expect(FileManager.default.fileExists(atPath:stage.appendingPathComponent("folder/empty").path))
    #expect(!FileManager.default.fileExists(atPath:stage.appendingPathComponent("folder/link").path))
    #expect(FileManager.default.fileExists(atPath:stage.appendingPathComponent("A (1).txt").path))
    do { try archive.add(link,name:"selected-link"); Issue.record("Explicit root link was accepted") } catch {}
    #expect(try String(contentsOf:file,encoding:.utf8) == "safe")
}
