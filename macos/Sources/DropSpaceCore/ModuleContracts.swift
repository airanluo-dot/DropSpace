import Foundation

// The PascalCase v1 declaration/wire names are preserved. The manifest reader
// accepts case variants; native state is separately versioned, not Windows disk IO.
public struct ModuleCapability: Codable, Equatable, Sendable {
    public var id: String
    public var version: Int
    public var fallback: String?
    public init(id: String, version: Int, fallback: String? = nil) {
        self.id = id; self.version = version; self.fallback = fallback
    }
    enum CodingKeys: String, CodingKey {
        case id = "Id"
        case version = "Version"
        case fallback = "Fallback"
    }
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decode(String.self, forKey: .id)
        version = try values.contains(.version) ? Int(values.decode(Int32.self, forKey: .version)) : 0
        fallback = try values.decodeIfPresent(String.self, forKey: .fallback)
    }
}

public struct ModuleDependency: Codable, Equatable, Sendable {
    public var id: String
    public var minimumVersion: String
    public var maximumVersionExclusive: String
    public init(id: String, minimumVersion: String, maximumVersionExclusive: String) {
        self.id = id; self.minimumVersion = minimumVersion; self.maximumVersionExclusive = maximumVersionExclusive
    }
    enum CodingKeys: String, CodingKey {
        case id = "Id"
        case minimumVersion = "MinimumVersion"
        case maximumVersionExclusive = "MaximumVersionExclusive"
    }
}

public struct ModuleFile: Codable, Equatable, Sendable {
    public var path: String
    public var bytes: Int64
    public var sha256: String
    public init(path: String, bytes: Int64, sha256: String) {
        self.path = path; self.bytes = bytes; self.sha256 = sha256
    }
    enum CodingKeys: String, CodingKey {
        case path = "Path"
        case bytes = "Bytes"
        case sha256 = "Sha256"
    }
}

public struct ModuleText: Codable, Equatable, Sendable {
    public var key: String
    public init(key: String) {
        self.key = key
    }
    enum CodingKeys: String, CodingKey {
        case key = "Key"
    }
}

public struct ModuleAction: Codable, Equatable, Sendable {
    public var id: String
    public var label: ModuleText
    public init(id: String, label: ModuleText) {
        self.id = id; self.label = label
    }
    enum CodingKeys: String, CodingKey {
        case id = "Id"
        case label = "Label"
    }
}

public struct ModuleSetting: Codable, Equatable, Sendable {
    public var id: String
    public var label: ModuleText
    public var kind: String
    public var defaultValue: String
    public init(id: String, label: ModuleText, kind: String, defaultValue: String) {
        self.id = id; self.label = label; self.kind = kind; self.defaultValue = defaultValue
    }
    enum CodingKeys: String, CodingKey {
        case id = "Id"
        case label = "Label"
        case kind = "Kind"
        case defaultValue = "DefaultValue"
    }
}

public struct ModulePage: Codable, Equatable, Sendable {
    public var id: String
    public var title: ModuleText
    public var description: ModuleText
    public var actions: [ModuleAction]
    public init(id: String, title: ModuleText, description: ModuleText, actions: [ModuleAction]) {
        self.id = id; self.title = title; self.description = description; self.actions = actions
    }
    enum CodingKeys: String, CodingKey {
        case id = "Id"
        case title = "Title"
        case description = "Description"
        case actions = "Actions"
    }
}

public struct ModuleUI: Codable, Equatable, Sendable {
    public var icon: String
    public var pages: [ModulePage]
    public var settings: [ModuleSetting]
    public init(icon: String, pages: [ModulePage], settings: [ModuleSetting]) {
        self.icon = icon; self.pages = pages; self.settings = settings
    }
    enum CodingKeys: String, CodingKey {
        case icon = "Icon"
        case pages = "Pages"
        case settings = "Settings"
    }
}

public struct ModuleDataFormat: Codable, Equatable, Sendable {
    public var version: Int
    public var minimumReadableVersion: Int
    public var maximumReadableVersion: Int
    public init(version: Int, minimumReadableVersion: Int, maximumReadableVersion: Int) {
        self.version = version; self.minimumReadableVersion = minimumReadableVersion; self.maximumReadableVersion = maximumReadableVersion
    }
    enum CodingKeys: String, CodingKey {
        case version = "Version"
        case minimumReadableVersion = "MinimumReadableVersion"
        case maximumReadableVersion = "MaximumReadableVersion"
    }
}

public struct OfficialModulePackage: Codable, Equatable, Sendable {
    public var id: String
    public var version: String
    public var url: String
    public var bytes: Int64
    public var sha256: String
    public init(id: String, version: String, url: String, bytes: Int64, sha256: String) {
        self.id = id; self.version = version; self.url = url; self.bytes = bytes; self.sha256 = sha256
    }
    enum CodingKeys: String, CodingKey {
        case id = "Id"
        case version = "Version"
        case url = "Url"
        case bytes = "Bytes"
        case sha256 = "Sha256"
    }
}

public struct ModuleInstallation: Codable, Equatable, Sendable {
    public var id: String
    public var version: String?
    public var enabled: Bool
    public var dataVersion: Int
    public var transaction: ModuleTransactionState
    public var previousVersion: String?
    public var candidateVersion: String?
    public var errorCode: String?
    public init(id: String, version: String? = nil, enabled: Bool, dataVersion: Int, transaction: ModuleTransactionState = .none, previousVersion: String? = nil, candidateVersion: String? = nil, errorCode: String? = nil) {
        self.id = id; self.version = version; self.enabled = enabled; self.dataVersion = dataVersion; self.transaction = transaction; self.previousVersion = previousVersion; self.candidateVersion = candidateVersion; self.errorCode = errorCode
    }
    enum CodingKeys: String, CodingKey {
        case id = "Id"
        case version = "Version"
        case enabled = "Enabled"
        case dataVersion = "DataVersion"
        case transaction = "Transaction"
        case previousVersion = "PreviousVersion"
        case candidateVersion = "CandidateVersion"
        case errorCode = "ErrorCode"
    }
}

public struct ModuleIslandContent: Codable, Equatable, Sendable {
    public var compact: ModuleText
    public var expanded: ModuleText
    public var actions: [ModuleAction]
    public var expiresAt: Date
    public init(compact: ModuleText, expanded: ModuleText, actions: [ModuleAction], expiresAt: Date) {
        self.compact = compact; self.expanded = expanded; self.actions = actions; self.expiresAt = expiresAt
    }
    enum CodingKeys: String, CodingKey {
        case compact = "Compact"
        case expanded = "Expanded"
        case actions = "Actions"
        case expiresAt = "ExpiresAt"
    }
}

public enum ModuleRunState: String, Codable, Sendable { case stopped, starting, running, stopping, faulted }
public enum ModuleTransactionState: Int, Codable, Sendable { case none, downloading, preparing, activating, cleaning, pendingCleanup }

public struct ModuleEnvelope: Equatable, Sendable {
    public var protocolVersion: Int
    public var session: String
    public var id: String
    public var method: String
    public var payload: ModuleJSON
    public var errorCode: String?
    public init(protocolVersion: Int = ModuleContract.protocolVersion, session: String, id: String, method: String, payload: ModuleJSON, errorCode: String? = nil) {
        self.protocolVersion = protocolVersion; self.session = session; self.id = id; self.method = method; self.payload = payload; self.errorCode = errorCode
    }
    public func encoded() throws -> Data {
        try ModuleJSON.object([.init("Protocol", .number(String(protocolVersion))), .init("Session", .string(session)), .init("Id", .string(id)),
                               .init("Method", .string(method)), .init("Payload", payload), .init("ErrorCode", errorCode.map(ModuleJSON.string) ?? .null)]).encoded()
    }
    public static func decode(_ bytes: Data) throws -> ModuleEnvelope {
        let value = try ModuleJSON.decode(bytes)
        guard let version = value["Protocol"]?.integer, Int32(exactly: version) != nil, let session = value["Session"]?.string,
              let id = value["Id"]?.string, let method = value["Method"]?.string, let payload = value["Payload"] else { throw DropSpaceError.invalid("ModuleProtocolInvalid") }
        let error: String?
        switch value["ErrorCode"] { case nil, .null: error = nil; case .string(let text): error = text; default: throw DropSpaceError.invalid("ModuleProtocolInvalid") }
        return .init(protocolVersion: version, session: session, id: id, method: method, payload: payload, errorCode: error)
    }
    public func reply(expectedSession: String, expectedID: String) throws -> ModuleReply {
        guard protocolVersion == ModuleContract.protocolVersion, session == expectedSession, id == expectedID, method == "result" else { throw DropSpaceError.invalid("ModuleResultExpired") }
        if let errorCode { throw DropSpaceError.unavailable("ModuleError:" + errorCode) }
        guard let result = payload["Result"] else { throw DropSpaceError.invalid("ModuleProtocolInvalid") }
        let island: ModuleIslandContent?
        switch payload["Island"] {
        case nil, .null: island = nil
        case .some(let value):
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .custom { decoder in
                let value = try decoder.singleValueContainer().decode(String.self)
                let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                if let date = formatter.date(from: value) { return date }
                formatter.formatOptions = [.withInternetDateTime]
                guard let date = formatter.date(from: value) else { throw DropSpaceError.invalid("ModuleIslandTimestamp") }; return date
            }
            island = try decoder.decode(ModuleIslandContent.self, from: value.encoded())
        }
        return .init(result: result, island: island)
    }
}
public struct ModuleReply: Equatable, Sendable {
    public var result: ModuleJSON
    public var island: ModuleIslandContent?
    public init(result: ModuleJSON, island: ModuleIslandContent? = nil) { self.result = result; self.island = island }
}

public struct ModuleManifest: Codable, Equatable, Sendable {
    public var manifestVersion = 1
    public var id: String
    public var version: String
    public var minimumHostInterface = 1, maximumHostInterface = 1
    public var protocolVersion = 1, uiVersion = 1
    public var system = "macos", architecture = "arm64"
    public var minimumWindowsBuild = 20348
    public var minimumMacOSVersion: String? = "26.0"
    public var entryPoint: String
    public var name: ModuleText
    public var requiredCapabilities: [ModuleCapability] = []
    public var optionalCapabilities: [ModuleCapability] = []
    public var dependencies: [ModuleDependency] = []
    public var data = ModuleDataFormat(version: 1, minimumReadableVersion: 1, maximumReadableVersion: 1)
    public var ui = ModuleUI(icon: "Puzzle", pages: [], settings: [])
    public var resources: [String: [String: String]] = [:]
    public var files: [ModuleFile] = []
    public var publisher = "airanluo-dot/DropSpace"
    public init(id: String, version: String, entryPoint: String, name: ModuleText) {
        self.id = id; self.version = version; self.entryPoint = entryPoint; self.name = name
    }
    enum CodingKeys: String, CodingKey {
        case manifestVersion = "ManifestVersion", id = "Id", version = "Version"
        case minimumHostInterface = "MinimumHostInterface", maximumHostInterface = "MaximumHostInterface"
        case protocolVersion = "ProtocolVersion", uiVersion = "UiVersion"
        case system = "System", architecture = "Architecture", minimumWindowsBuild = "MinimumWindowsBuild"
        case minimumMacOSVersion = "MinimumMacOSVersion", entryPoint = "EntryPoint", name = "Name"
        case requiredCapabilities = "RequiredCapabilities", optionalCapabilities = "OptionalCapabilities"
        case dependencies = "Dependencies", data = "Data", ui = "Ui", resources = "Resources", files = "Files", publisher = "Publisher"
    }
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        func decode<T: Decodable>(_ type: T.Type, _ key: CodingKeys, _ fallback: T) throws -> T {
            try values.contains(key) ? values.decode(type, forKey: key) : fallback
        }
        id = try values.decode(String.self, forKey: .id); version = try values.decode(String.self, forKey: .version)
        entryPoint = try values.decode(String.self, forKey: .entryPoint); name = try values.decode(ModuleText.self, forKey: .name)
        manifestVersion = try decode(Int.self, .manifestVersion, 1)
        minimumHostInterface = try decode(Int.self, .minimumHostInterface, 1)
        maximumHostInterface = try decode(Int.self, .maximumHostInterface, 1)
        protocolVersion = try decode(Int.self, .protocolVersion, 1)
        uiVersion = try decode(Int.self, .uiVersion, 1)
        // Missing fields retain Windows v1 meaning, never silently acquire Mac identity.
        system = try decode(String.self, .system, "windows")
        architecture = try decode(String.self, .architecture, "x64")
        minimumWindowsBuild = try decode(Int.self, .minimumWindowsBuild, 20348)
        minimumMacOSVersion = try values.decodeIfPresent(String.self, forKey: .minimumMacOSVersion)
        requiredCapabilities = try decode([ModuleCapability].self, .requiredCapabilities, [])
        optionalCapabilities = try decode([ModuleCapability].self, .optionalCapabilities, [])
        dependencies = try decode([ModuleDependency].self, .dependencies, [])
        data = try decode(ModuleDataFormat.self, .data, .init(version: 1, minimumReadableVersion: 1, maximumReadableVersion: 1))
        ui = try decode(ModuleUI.self, .ui, .init(icon: "Puzzle", pages: [], settings: []))
        resources = try decode([String: [String: String]].self, .resources, [:])
        files = try decode([ModuleFile].self, .files, [])
        publisher = try decode(String.self, .publisher, "airanluo-dot/DropSpace")
    }
}

public enum ModuleContract {
    public static let hostInterface = 1, protocolVersion = 1, maximumMessageBytes = 65536
    public static let capabilities = ["ui.pages": 1, "ui.settings": 1, "island.content": 1, "module.data": 1]
    public static let languages = AppSettings.languages
    public static func validID(_ value: String) -> Bool {
        let bytes = Array(value.utf8)
        return (1...80).contains(bytes.count) && (97...122).contains(bytes[0]) && bytes.allSatisfy { (97...122).contains($0) || (48...57).contains($0) || $0 == 45 || $0 == 46 }
    }
    public static func version(_ value: String, os: Bool = false) -> [Int]? {
        let pieces = value.split(separator: ".", omittingEmptySubsequences: false)
        guard pieces.count == 3 || os && pieces.count == 2 else { return nil }
        var numbers: [Int] = []
        for piece in pieces {
            guard !piece.isEmpty, piece.utf8.allSatisfy({ (48...57).contains($0) }), piece.count == 1 || piece.first != "0",
                  let value = Int32(piece), value >= 0 else { return nil }
            numbers.append(Int(value))
        }
        if numbers.count == 2 { numbers.append(0) }; return numbers
    }
    public static func text(_ manifest: ModuleManifest, _ text: ModuleText, language: String) -> String {
        manifest.resources[language]?[text.key] ?? manifest.resources["en-US"]?[text.key] ?? text.key
    }
    public static func hasCapability(_ manifest: ModuleManifest, _ id: String) -> Bool {
        (manifest.requiredCapabilities + manifest.optionalCapabilities).contains { capability in
            guard capability.id == id, let available = capabilities[id] else { return false }
            return capability.version <= available
        }
    }
    public static func validate(_ manifest: ModuleManifest, installed: [String: ModuleInstallation], systemVersion: OperatingSystemVersion) throws {
        func require(_ condition: @autoclosure () -> Bool, _ code: String) throws { if !condition() { throw DropSpaceError.invalid(code) } }
        try require(manifest.manifestVersion == 1 && manifest.protocolVersion == protocolVersion && manifest.uiVersion == 1 && manifest.minimumHostInterface <= hostInterface && manifest.maximumHostInterface >= hostInterface, "HostInterfaceRequired")
        guard validID(manifest.id), version(manifest.version) != nil, manifest.publisher == "airanluo-dot/DropSpace",
              manifest.system == "macos", manifest.architecture == "arm64", let minimum = manifest.minimumMacOSVersion.flatMap({ version($0, os: true) }),
              ![systemVersion.majorVersion, systemVersion.minorVersion, systemVersion.patchVersion].lexicographicallyPrecedes(minimum) else { throw DropSpaceError.invalid("ModuleIncompatible") }
        for capability in manifest.requiredCapabilities {
            try require(capability.version >= 1 && (capabilities[capability.id] ?? 0) >= capability.version, "RequiredCapabilityMissing:" + capability.id)
        }
        for capability in manifest.optionalCapabilities where capabilities[capability.id] == nil || capabilities[capability.id]! < capability.version {
            try require(capability.fallback == "omit", "OptionalFallbackRequired")
        }
        for dependency in manifest.dependencies {
            guard dependency.id != manifest.id, validID(dependency.id), let lower = version(dependency.minimumVersion),
                  let upper = version(dependency.maximumVersionExclusive), let current = (installed[dependency.id]?.version).flatMap({ version($0) }),
                  !current.lexicographicallyPrecedes(lower), current.lexicographicallyPrecedes(upper) else { throw DropSpaceError.invalid("DependencyUnavailable:" + dependency.id) }
        }
        try require(manifest.data.version >= 1 && manifest.data.minimumReadableVersion >= 1 && manifest.data.minimumReadableVersion <= manifest.data.version && manifest.data.maximumReadableVersion >= manifest.data.version, "InvalidDataFormat")
        try require((1...128).contains(manifest.files.count) && manifest.files.contains(where: { $0.path == manifest.entryPoint }) && !manifest.entryPoint.lowercased().hasSuffix(".exe") &&
                    manifest.ui.pages.count <= 8 && manifest.ui.settings.count <= 32 && ["Puzzle", "Document", "Tools"].contains(manifest.ui.icon) &&
                    Set(manifest.ui.pages.map(\.id)).count == manifest.ui.pages.count && Set(manifest.ui.settings.map(\.id)).count == manifest.ui.settings.count, "InvalidModuleDeclaration")
        var texts = [manifest.name]
        for page in manifest.ui.pages {
            try require(validID(page.id) && page.actions.count <= 8 && Set(page.actions.map(\.id)).count == page.actions.count, "InvalidPage")
            texts += [page.title, page.description]
            for action in page.actions { try require(validID(action.id), "InvalidAction"); texts.append(action.label) }
        }
        for setting in manifest.ui.settings {
            try require(validID(setting.id) && ["text", "boolean"].contains(setting.kind) && setting.defaultValue.utf16.count <= 4096 &&
                        (setting.kind != "boolean" || ["true", "false"].contains(setting.defaultValue)), "InvalidSetting")
            texts.append(setting.label)
        }
        for language in languages {
            guard let catalog = manifest.resources[language], (1...256).contains(catalog.count),
                  texts.allSatisfy({ catalog[$0.key].map { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } == true }),
                  catalog.allSatisfy({ (1...128).contains($0.key.utf16.count) && $0.value.utf16.count <= 4096 }) else { throw DropSpaceError.invalid("ModuleTranslationMissing:" + language) }
        }
        try require((manifest.ui.pages.isEmpty || hasCapability(manifest, "ui.pages")) && (manifest.ui.settings.isEmpty || hasCapability(manifest, "ui.settings")), "UndeclaredUiCapability")
    }
    public static func validateIsland(_ content: ModuleIslandContent, manifest: ModuleManifest, now: Date) throws {
        let actions = manifest.ui.pages.flatMap(\.actions)
        guard hasCapability(manifest, "island.content"), content.expiresAt > now, content.expiresAt <= now.addingTimeInterval(60), content.actions.count <= 4,
              content.actions.allSatisfy({ action in actions.contains(action) }), manifest.resources["en-US"]?[content.compact.key] != nil,
              manifest.resources["en-US"]?[content.expanded.key] != nil else { throw DropSpaceError.invalid("ModuleIslandInvalid") }
    }
}
