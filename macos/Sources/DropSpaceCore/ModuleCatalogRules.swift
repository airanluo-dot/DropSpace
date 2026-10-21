import Foundation

public enum ModuleCatalogRules {
    public static let maximumArchiveBytes: Int64 = 256 * 1024 * 1024
    public static let maximumCatalogBytes = 256 * 1024
    public static let catalogURL = URL(string: "https://raw.githubusercontent.com/airanluo-dot/DropSpace/main/macos/Modules/catalog.json")!
    public static func packageURL(id: String, version: String) -> String {
        "https://github.com/airanluo-dot/DropSpace/releases/download/macos-dlc-\(id)-\(version)/\(id)-\(version)-macos-arm64.zip"
    }
    public static func validHash(_ value: String) -> Bool {
        value.utf8.count == 64 && value.utf8.allSatisfy { (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }
    }
    @discardableResult public static func require(_ package: OfficialModulePackage) throws -> OfficialModulePackage {
        guard ModuleContract.validID(package.id), ModuleContract.version(package.version) != nil,
              package.bytes > 0, package.bytes <= maximumArchiveBytes, validHash(package.sha256),
              package.url == packageURL(id: package.id, version: package.version) else { throw DropSpaceError.invalid("UnofficialModulePackage") }
        return package
    }
    public static func trustedHop(_ destination: URL, original: URL) -> Bool {
        destination.scheme == "https" && (destination.port == nil || destination.port == 443) && destination.user == nil && destination.password == nil &&
            (destination == original || ["release-assets.githubusercontent.com", "objects.githubusercontent.com"].contains(destination.host?.lowercased() ?? ""))
    }
    public static func parse(_ bytes: Data) throws -> [OfficialModulePackage] {
        func fields(_ value: ModuleJSON, _ expected: Set<String>) throws -> [String: ModuleJSON] {
            guard case .object(let members) = value, members.count == expected.count, Set(members.map(\.name)) == expected else { throw DropSpaceError.invalid("OfficialModuleCatalogInvalidField") }
            return Dictionary(uniqueKeysWithValues: members.map { ($0.name, $0.value) })
        }
        let root = try fields(ModuleJSON.decode(bytes, maximumBytes: maximumCatalogBytes), ["schemaVersion", "packages"])
        guard root["schemaVersion"]?.integer == 1 else { throw DropSpaceError.invalid("OfficialModuleCatalogVersion") }
        guard case .array(let entries) = root["packages"], entries.count <= 64 else { throw DropSpaceError.invalid("OfficialModuleCatalogInvalid") }
        var packages: [OfficialModulePackage] = [], seen = Set<String>()
        for entry in entries {
            let value = try fields(entry, ["id", "version", "url", "bytes", "sha256"])
            guard let id = value["id"]?.string, let version = value["version"]?.string, let url = value["url"]?.string,
                  let bytes = value["bytes"]?.integer, let sha = value["sha256"]?.string else { throw DropSpaceError.invalid("OfficialModuleCatalogInvalid") }
            let package = try require(.init(id: id, version: version, url: url, bytes: Int64(bytes), sha256: sha))
            guard seen.insert(id).inserted else { throw DropSpaceError.invalid("OfficialModuleCatalogDuplicateId") }
            if id != "dropspace.sample" { packages.append(package) }
        }
        return packages
    }
    public static func embedded() throws -> [OfficialModulePackage] {
        guard let path = Bundle.module.url(forResource: "official-modules-macos", withExtension: "json", subdirectory: "Resources") else { throw DropSpaceError.unavailable("OfficialModuleCatalogMissing") }
        return try parse(Data(contentsOf: path))
    }
}
