import Foundation

public enum ImageRules {
    public static func nativeOutputFormat(forPath path: String) -> String {
        switch URL(fileURLWithPath: path).pathExtension.lowercased() {
        case "jpg", "jpeg": "jpeg"
        case "bmp": "bmp"
        default: "png"
        }
    }
    public static let maximumDimension = 16_384
    public static let maximumPixels = 64 * 1024 * 1024
    public static let maximumEncodedBytes = 256 * 1024 * 1024
    public static func validate(width: Int,height: Int) throws {
        guard width > 0, height > 0, width <= maximumDimension, height <= maximumDimension,
              width <= maximumPixels / height else { throw DropSpaceError.limit("Image dimensions exceed the supported limit") }
    }
    public static func scaled(width: Int,height: Int,percent: Int) throws -> (width:Int,height:Int) {
        try validate(width:width,height:height)
        guard (1...100).contains(percent) else { throw DropSpaceError.invalid("Image percentage") }
        return (max(1,Int((Double(width) * Double(percent) / 100).rounded(.toNearestOrEven))),max(1,Int((Double(height) * Double(percent) / 100).rounded(.toNearestOrEven))))
    }
    public static func fitted(sourceWidth: Int,sourceHeight: Int,width: Int,height: Int,keepAspectRatio: Bool) throws -> (width:Int,height:Int) {
        try validate(width:sourceWidth,height:sourceHeight)
        guard (1...maximumDimension).contains(width),(1...maximumDimension).contains(height) else { throw DropSpaceError.invalid("Image target dimensions") }
        let scale = min(Double(width) / Double(sourceWidth),Double(height) / Double(sourceHeight))
        let result = keepAspectRatio ? (max(1,Int((Double(sourceWidth) * scale).rounded(.toNearestOrEven))),max(1,Int((Double(sourceHeight) * scale).rounded(.toNearestOrEven)))) : (width,height)
        try validate(width:result.0,height:result.1)
        return result
    }
    public static func canEncodeQR(_ text: String) -> Bool { !text.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty && text.utf16.count <= 1600 && text.utf8.count <= 1600 }
}
