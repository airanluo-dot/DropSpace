import Foundation
import ImageIO
import CoreGraphics
import UniformTypeIdentifiers
import Testing
@testable import DropSpacePlatform

private func artworkFixture(width: Int, height: Int) throws -> Data {
    let context = try #require(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                         space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    context.setFillColor(CGColor(red: 0.2, green: 0.4, blue: 0.8, alpha: 1)); context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    let output = NSMutableData(), image = try #require(context.makeImage())
    let destination = try #require(CGImageDestinationCreateWithData(output, UTType.png.identifier as CFString, 1, nil))
    CGImageDestinationAddImage(destination, image, nil)
    #expect(CGImageDestinationFinalize(destination))
    return output as Data
}

@Test func mediaArtworkBoundsAndThumbnailPreserveSourceBytes() throws {
    let original = try artworkFixture(width: 512, height: 256), saved = original
    let rendered = try #require(NativeMediaArtwork.decode(original))
    let source = try #require(CGImageSourceCreateWithData(rendered as CFData, nil))
    let image = try #require(CGImageSourceCreateImageAtIndex(source, 0, nil))
    #expect(image.width == 192 && image.height == 96)
    #expect(original == saved)
    #expect(NativeMediaArtwork.decode(Data(repeating: 0, count: 4 * 1024 * 1024 + 1)) == nil)
    #expect(NativeMediaArtwork.decode(Data("not an image".utf8)) == nil)
    #expect(NativeMediaArtwork.decode(try artworkFixture(width: 8193, height: 1)) == nil)
}
