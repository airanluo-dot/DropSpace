import Foundation

/// A deliberately narrow host-cache grammar. JSONDecoder alone accepts repeated
/// keys, which would differ from the frozen id/text output validation contract.
public enum PlainHyOutputDecoder {
    public static func decode(_ data: Data) throws -> [PlainHyOutput] {
        guard !data.isEmpty, data.count <= PlainHyLyrics.maximumSongOutputBytes else { throw DropSpaceError.limit("Translation cache output size") }
        var parser = Parser(bytes: Array(data)); return try parser.parse()
    }
    private struct Parser {
        let bytes: [UInt8]
        var offset = 0
        mutating func whitespace() { while offset < bytes.count && [9, 10, 13, 32].contains(bytes[offset]) { offset += 1 } }
        mutating func take(_ byte: UInt8) -> Bool { whitespace(); guard offset < bytes.count, bytes[offset] == byte else { return false }; offset += 1; return true }
        mutating func require(_ byte: UInt8) throws { guard take(byte) else { throw invalid() } }
        func invalid() -> DropSpaceError { .invalid("Malformed mapped translation output") }
        mutating func string() throws -> String {
            whitespace(); let start = offset; try require(34)
            while offset < bytes.count {
                let byte = bytes[offset]; offset += 1
                if byte == 34 { return try JSONDecoder().decode(String.self, from: Data(bytes[start..<offset])) }
                if byte == 92 { guard offset < bytes.count else { throw invalid() }; offset += 1 }
            }
            throw invalid()
        }
        mutating func integer() throws -> Int {
            whitespace(); let start = offset
            if offset < bytes.count, bytes[offset] == 45 { offset += 1 }
            guard offset < bytes.count else { throw invalid() }
            if bytes[offset] == 48 { offset += 1 }
            else {
                guard (49...57).contains(bytes[offset]) else { throw invalid() }
                while offset < bytes.count, (48...57).contains(bytes[offset]) { offset += 1 }
            }
            guard let value = Int32(String(decoding: bytes[start..<offset], as: UTF8.self)) else { throw invalid() }; return Int(value)
        }
        mutating func parse() throws -> [PlainHyOutput] {
            try require(91); var result: [PlainHyOutput] = []
            if take(93) { whitespace(); guard offset == bytes.count else { throw invalid() }; return [] }
            repeat {
                guard result.count < 500 else { throw invalid() }
                try require(123); var id: Int?, text: String?
                repeat {
                    let key = try string(); try require(58)
                    if key == "id", id == nil { id = try integer() }
                    else if key == "text", text == nil { text = try string() }
                    else { throw invalid() }
                } while take(44)
                try require(125)
                guard let id, let text else { throw invalid() }; result.append(.init(id: id, text: text))
            } while take(44)
            try require(93); whitespace(); guard offset == bytes.count else { throw invalid() }; return result
        }
    }
}
