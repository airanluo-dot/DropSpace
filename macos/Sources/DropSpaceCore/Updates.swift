import Foundation

public struct MacVersion: Comparable, Equatable, Sendable {
    public let major: Int
    public let minor: Int
    public let patch: Int
    public let beta: Int?

    public init(_ tag: String) throws {
        let pattern = /^macos-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.([1-9][0-9]*))?$/
        guard let match = tag.wholeMatch(of: pattern),
              let major = Int(match.1), let minor = Int(match.2), let patch = Int(match.3),
              match.4 == nil || Int(match.4!) != nil else {
            throw DropSpaceError.invalid("Invalid macOS release version")
        }
        self.major = major; self.minor = minor; self.patch = patch
        beta = match.4.flatMap { Int($0) }
    }

    public static func < (lhs: Self, rhs: Self) -> Bool {
        if lhs.major != rhs.major { return lhs.major < rhs.major }
        if lhs.minor != rhs.minor { return lhs.minor < rhs.minor }
        if lhs.patch != rhs.patch { return lhs.patch < rhs.patch }
        switch (lhs.beta, rhs.beta) {
        case (.none, _): return false
        case (.some, .none): return true
        case (.some(let a), .some(let b)): return a < b
        }
    }
}

public struct MacUpdateManifest: Codable, Equatable, Sendable {
    public let schemaVersion: Int
    public let platform: String
    public let architecture: String
    public let tag: String
    public let channel: UpdateChannel
    public let bundleID: String
    public let bundleVersion: String
    public let minimumOS: String
    public let windowsBaseline: String
    public let signing: String
    public let notarized: Bool
    public let archive: Asset
    public let installer: Asset
    public let releaseNotes: String
    public let releaseURL: URL

    public struct Asset: Codable, Equatable, Sendable {
        public let name: String
        public let url: URL
        public let size: Int64
        public let sha256: String
    }

    public func validate(channel requestedChannel: UpdateChannel, os: OperatingSystemVersion) throws {
        let version = try MacVersion(tag)
        guard schemaVersion == 1, platform == "macos", architecture == "arm64",
              bundleID == "io.github.airanluo-dot.DropSpace.macOS",
              Int(bundleVersion).map({ $0 > 0 }) == true,
              channel == (version.beta == nil ? .stable : .beta),
              requestedChannel == .beta || channel == .stable,
              windowsBaseline == "v0.3.2-beta.7", signing == "ad-hoc", !notarized,
              releaseNotes.utf8.count <= 65536,
              releaseURL.absoluteString == "https://github.com/airanluo-dot/DropSpace/releases/tag/\(tag)" else {
            throw DropSpaceError.invalid("Incompatible macOS update manifest")
        }
        let components = minimumOS.split(separator: ".").compactMap { Int($0) }
        guard (2...3).contains(components.count), components.count == minimumOS.split(separator: ".").count,
              components.allSatisfy({ $0 >= 0 }), components[0] >= 26 else {
            throw DropSpaceError.invalid("Invalid minimum macOS version")
        }
        let required = [components[0], components[1], components.count == 3 ? components[2] : 0]
        let current = [os.majorVersion, os.minorVersion, os.patchVersion]
        guard !current.lexicographicallyPrecedes(required) else {
            throw DropSpaceError.unavailable("This update requires macOS \(minimumOS) or later")
        }
        for (asset, name) in [(archive, "DropSpace-macOS-arm64.zip"), (installer, "DropSpace-macOS-arm64.dmg")] {
            guard asset.name == name, asset.size > 0, asset.size <= 2 * 1024 * 1024 * 1024,
                  asset.sha256.range(of: "^[0-9a-f]{64}$", options: .regularExpression) != nil,
                  asset.url.absoluteString == "https://github.com/airanluo-dot/DropSpace/releases/download/\(tag)/\(name)" else {
                throw DropSpaceError.invalid("Invalid same-release macOS artifact")
            }
        }
    }
}

public enum MacUpdateOrigin {
    public static func feed(_ channel: UpdateChannel) -> URL {
        URL(string: "https://airanluo-dot.github.io/DropSpace/api/macos/v1/\(channel.rawValue).json")!
    }
    public static func allowsAssetRedirect(_ url: URL) -> Bool {
        guard url.scheme == "https", url.user == nil, url.password == nil,
              url.port == nil || url.port == 443, url.fragment == nil else { return false }
        switch url.host?.lowercased() {
        case "github.com": return url.path.hasPrefix("/airanluo-dot/DropSpace/releases/download/macos-v")
        case "release-assets.githubusercontent.com", "objects.githubusercontent.com": return true
        default: return false
        }
    }
}

public struct MacUpdateFeed: Codable, Sendable {
    public let schemaVersion: Int
    public let platform: String
    public let channel: UpdateChannel
    public let release: MacUpdateManifest?
    public func validate(channel expected: UpdateChannel) throws {
        guard schemaVersion == 1, platform == "macos", channel == expected else {
            throw DropSpaceError.invalid("Unexpected update feed platform or channel")
        }
    }
}
