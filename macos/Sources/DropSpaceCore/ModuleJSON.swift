import Foundation

public struct ModuleJSONMember: Equatable, Sendable {
    public var name: String
    public var value: ModuleJSON
    public init(_ name: String, _ value: ModuleJSON) { self.name = name; self.value = value }
}

/// Bounded JSON with lossless number lexemes and ordered properties. JsonElement
/// v1 can carry integers wider than Decimal/Double and resolves duplicates last.
public indirect enum ModuleJSON: Equatable, Sendable {
    case object([ModuleJSONMember]), array([ModuleJSON]), string(String), number(String), boolean(Bool), null
    public subscript(_ name: String) -> ModuleJSON? {
        guard case .object(let values) = self else { return nil }
        return values.last(where: { $0.name == name })?.value
    }
    public var string: String? { if case .string(let value) = self { return value }; return nil }
    public var integer: Int? { if case .number(let value) = self { return Int(value) }; return nil }
    public static func decode(_ bytes: Data, maximumBytes: Int = ModuleContract.maximumMessageBytes) throws -> ModuleJSON {
        guard bytes.count <= maximumBytes else { throw DropSpaceError.limit("ModuleMessageTooLarge") }
        var parser = Parser(bytes: Array(bytes))
        let value = try parser.value(depth: 0); parser.whitespace()
        guard parser.offset == parser.bytes.count else { throw DropSpaceError.invalid("ModuleProtocolInvalid") }; return value
    }
    public func encoded(maximumBytes: Int = ModuleContract.maximumMessageBytes) throws -> Data {
        var output = Data()
        func append(_ bytes: Data) throws {
            guard bytes.count <= maximumBytes - output.count else { throw DropSpaceError.limit("ModuleMessageTooLarge") }
            output.append(bytes)
        }
        func write(_ value: ModuleJSON, depth: Int) throws {
            guard depth <= 64 else { throw DropSpaceError.limit("ModuleJSONDepth") }
            switch value {
            case .null: try append(Data("null".utf8))
            case .boolean(let value): try append(Data((value ? "true" : "false").utf8))
            case .string(let value): try append(JSONEncoder().encode(value))
            case .number(let value):
                let encoded = Data(value.utf8)
                guard case .number = try ModuleJSON.decode(encoded, maximumBytes: maximumBytes) else { throw DropSpaceError.invalid("ModuleJSONNumber") }
                try append(encoded)
            case .array(let values):
                try append(Data([91]))
                for (index, item) in values.enumerated() { if index > 0 { try append(Data([44])) }; try write(item, depth: depth + 1) }
                try append(Data([93]))
            case .object(let values):
                try append(Data([123]))
                for (index, item) in values.enumerated() {
                    if index > 0 { try append(Data([44])) }
                    try append(JSONEncoder().encode(item.name)); try append(Data([58])); try write(item.value, depth: depth + 1)
                }
                try append(Data([125]))
            }
        }
        try write(self, depth: 0); return output
    }
    private struct Parser {
        let bytes: [UInt8]
        var offset = 0
        mutating func whitespace() { while offset < bytes.count, [9, 10, 13, 32].contains(bytes[offset]) { offset += 1 } }
        mutating func take(_ expected: UInt8) -> Bool {
            whitespace(); guard offset < bytes.count, bytes[offset] == expected else { return false }; offset += 1; return true
        }
        mutating func string() throws -> String {
            whitespace(); let start = offset
            guard offset < bytes.count, bytes[offset] == 34 else { throw DropSpaceError.invalid("ModuleJSONString") }
            offset += 1
            while offset < bytes.count {
                let byte = bytes[offset]; offset += 1
                if byte == 34 { return try JSONDecoder().decode(String.self, from: Data(bytes[start..<offset])) }
                if byte == 92 { guard offset < bytes.count else { break }; offset += 1 }
                else if byte < 32 { throw DropSpaceError.invalid("ModuleJSONString") }
            }
            throw DropSpaceError.invalid("ModuleJSONString")
        }
        mutating func value(depth: Int) throws -> ModuleJSON {
            whitespace(); guard offset < bytes.count else { throw DropSpaceError.invalid("ModuleProtocolInvalid") }
            switch bytes[offset] {
            case 34: return .string(try string())
            case 123:
                guard depth < 64 else { throw DropSpaceError.limit("ModuleJSONDepth") }
                offset += 1; var values: [ModuleJSONMember] = []
                if take(125) { return .object(values) }
                while true {
                    let name = try string(); guard take(58) else { throw DropSpaceError.invalid("ModuleJSONObject") }
                    values.append(.init(name, try value(depth: depth + 1)))
                    if take(125) { return .object(values) }; guard take(44) else { throw DropSpaceError.invalid("ModuleJSONObject") }
                }
            case 91:
                guard depth < 64 else { throw DropSpaceError.limit("ModuleJSONDepth") }
                offset += 1; var values: [ModuleJSON] = []
                if take(93) { return .array(values) }
                while true {
                    values.append(try value(depth: depth + 1))
                    if take(93) { return .array(values) }; guard take(44) else { throw DropSpaceError.invalid("ModuleJSONArray") }
                }
            case 116: try literal("true"); return .boolean(true)
            case 102: try literal("false"); return .boolean(false)
            case 110: try literal("null"); return .null
            default: return .number(try number())
            }
        }
        mutating func literal(_ value: String) throws {
            let token = Array(value.utf8)
            guard bytes.count - offset >= token.count, Array(bytes[offset..<(offset + token.count)]) == token else { throw DropSpaceError.invalid("ModuleJSONLiteral") }
            offset += token.count
        }
        mutating func number() throws -> String {
            let start = offset
            if offset < bytes.count, bytes[offset] == 45 { offset += 1 }
            guard offset < bytes.count else { throw DropSpaceError.invalid("ModuleJSONNumber") }
            if bytes[offset] == 48 { offset += 1 }
            else {
                guard (49...57).contains(bytes[offset]) else { throw DropSpaceError.invalid("ModuleJSONNumber") }
                while offset < bytes.count, (48...57).contains(bytes[offset]) { offset += 1 }
            }
            if offset < bytes.count, bytes[offset] == 46 {
                offset += 1; let digits = offset
                while offset < bytes.count, (48...57).contains(bytes[offset]) { offset += 1 }
                guard offset > digits else { throw DropSpaceError.invalid("ModuleJSONNumber") }
            }
            if offset < bytes.count, bytes[offset] == 69 || bytes[offset] == 101 {
                offset += 1
                if offset < bytes.count, bytes[offset] == 43 || bytes[offset] == 45 { offset += 1 }
                let digits = offset
                while offset < bytes.count, (48...57).contains(bytes[offset]) { offset += 1 }
                guard offset > digits else { throw DropSpaceError.invalid("ModuleJSONNumber") }
            }
            return String(decoding: bytes[start..<offset], as: UTF8.self)
        }
    }
}

public enum ModuleCodec {
    private static let declarationNames = ["ManifestVersion", "Id", "Version", "MinimumHostInterface", "MaximumHostInterface", "ProtocolVersion", "UiVersion", "System", "Architecture", "MinimumWindowsBuild", "MinimumMacOSVersion", "EntryPoint", "Name", "RequiredCapabilities", "OptionalCapabilities", "Dependencies", "Data", "Ui", "Resources", "Files", "Publisher", "Fallback", "MinimumVersion", "MaximumVersionExclusive", "Path", "Bytes", "Sha256", "Key", "Label", "Kind", "DefaultValue", "Title", "Description", "Actions", "Icon", "Pages", "Settings", "MinimumReadableVersion", "MaximumReadableVersion"]
    private static let declarationKeys = Dictionary(uniqueKeysWithValues: declarationNames.map { ($0.lowercased(), $0) })
    public static func manifest(_ bytes: Data) throws -> ModuleManifest {
        let source = try ModuleJSON.decode(bytes, maximumBytes: 512 * 1024)
        // Manifest dictionaries have case-sensitive user resource keys; only the
        // record's declared property names inherit Windows case-insensitive decode.
        func normalize(_ value: ModuleJSON, declaration: Bool) -> ModuleJSON {
            switch value {
            case .object(let pairs):
                var values: [ModuleJSONMember] = [], indices: [String: Int] = [:]
                for pair in pairs {
                    let name = declaration ? declarationKeys[pair.name.lowercased()] ?? pair.name : pair.name
                    let item = ModuleJSONMember(name, normalize(pair.value, declaration: declaration && name != "Resources"))
                    if let index = indices[name] { values[index] = item }
                    else { indices[name] = values.count; values.append(item) }
                }
                return .object(values)
            case .array(let values): return .array(values.map { normalize($0, declaration: declaration) })
            default: return value
            }
        }
        let manifest = try JSONDecoder().decode(ModuleManifest.self, from: normalize(source, declaration: true).encoded(maximumBytes: 512 * 1024))
        let integers = [manifest.manifestVersion, manifest.minimumHostInterface, manifest.maximumHostInterface,
            manifest.protocolVersion, manifest.uiVersion, manifest.minimumWindowsBuild,
            manifest.data.version, manifest.data.minimumReadableVersion, manifest.data.maximumReadableVersion]
            + (manifest.requiredCapabilities + manifest.optionalCapabilities).map(\.version)
        guard integers.allSatisfy({ Int32(exactly: $0) != nil }) else { throw DropSpaceError.invalid("ModuleIntegerOutOfRange") }
        return manifest
    }
}
