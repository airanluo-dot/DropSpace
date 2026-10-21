import Foundation
import CryptoKit

public struct AIModelAsset: Codable, Equatable, Sendable {
    public let name: String
    public let bytes: Int64
    public let sha256: String
    public let url: URL
    public let order: Int?
}
public struct AIModelDescriptor: Codable, Equatable, Identifiable, Sendable {
    public let modelId: String
    public let name: String
    public let fullsize: Int64
    public let fullsha: String
    public let parts: [AIModelAsset]
    public let licenseFiles: [String]
    public var id: String { modelId }
    public var workerProfile: String { id == "hy-mt2-7b-q8-plain-beta" ? "hy-mt2-7b-q8" : "hy-mt2-1.8b-q8" }
    public var downloadDeadline: Duration { id == "hy-mt2-7b-q8-plain-beta" ? .seconds(7200) : .seconds(1800) }
}
public struct AIModelCatalog: Sendable {
    public static let manifestIdentity = "2da82e004022c52dc4315b8f6414273d13802c6c5202668e5be26a08ba6c0ef9"
    public static let releaseBase = "https://github.com/airanluo-dot/DropSpace/releases/download/models-hy-mt2-q8-v1/"
    public let models: [AIModelDescriptor]
    public let supportingAssets: [AIModelAsset]
    public init() throws {
        guard let url = Bundle.module.url(forResource: "models-hy-mt2-q8-v1", withExtension: "json", subdirectory: "Resources") else { throw DropSpaceError.invalid("Model catalog missing") }
        let data = try Data(contentsOf: url)
        guard data.count == 4785, SHA256.hash(data: data).map({ String(format: "%02x", $0) }).joined() == Self.manifestIdentity else { throw DropSpaceError.invalid("Model catalog identity mismatch") }
        struct Manifest: Decodable { var models: [AIModelDescriptor]; var supportingAssets: [AIModelAsset] }
        let manifest = try JSONDecoder().decode(Manifest.self, from: data)
        guard Set(manifest.models.map(\.id)) == ["hy-mt2-18-q8-plain-beta", "hy-mt2-7b-q8-plain-beta"] else { throw DropSpaceError.invalid("Model catalog selection") }
        for model in manifest.models {
            guard (1...4).contains(model.parts.count), model.parts.enumerated().allSatisfy({ $0.element.order == $0.offset + 1 }),
                  model.parts.reduce(Int64(0), { $0 + $1.bytes }) == model.fullsize,
                  model.licenseFiles.allSatisfy({ file in manifest.supportingAssets.contains { $0.name == file } }) else { throw DropSpaceError.invalid("Model delivery layout") }
        }
        guard (manifest.models.flatMap(\.parts) + manifest.supportingAssets).allSatisfy({ $0.bytes > 0 && $0.sha256.range(of: "^[0-9a-f]{64}$", options: .regularExpression) != nil && $0.url.absoluteString.hasPrefix(Self.releaseBase) }) else { throw DropSpaceError.invalid("Model delivery asset") }
        models = manifest.models; supportingAssets = manifest.supportingAssets
    }
    public func model(_ id: String) throws -> AIModelDescriptor {
        guard let model = models.first(where: { $0.id == id }) else { throw DropSpaceError.invalid("Unknown or retired translation model") }; return model
    }
    public func licenses(_ model: AIModelDescriptor) -> [AIModelAsset] { supportingAssets.filter { model.licenseFiles.contains($0.name) } }
    public static func trusted(_ url: URL) -> Bool {
        guard url.scheme?.lowercased() == "https", url.port == nil || url.port == 443, url.user == nil, url.password == nil else { return false }
        let host = url.host?.lowercased()
        return host == "github.com" && url.path.hasPrefix("/airanluo-dot/DropSpace/releases/download/models-hy-mt2-q8-v1/") || host == "release-assets.githubusercontent.com" || host == "objects.githubusercontent.com"
    }
}
