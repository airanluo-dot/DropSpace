import Foundation
import CZlib
import DropSpaceCore

public enum KugouKRCParser {
    public static func parse(_ content: String) throws -> LyricsDocument {
        let deadline = ContinuousClock.now.advanced(by: .milliseconds(500))
        func check() throws { try Task.checkCancellation(); if ContinuousClock.now > deadline { throw DropSpaceError.limit("KRC parsing exceeded its processing budget") } }
        try check()
        guard content.utf16.count <= (256 * 1024 + 2) / 3 * 4,
              let data = Data(base64Encoded: content.filter { !$0.isWhitespace }), data.count <= 256 * 1024 else { throw DropSpaceError.limit("Invalid or oversized KRC packed content") }
        if !data.starts(with: Data("krc1".utf8)) {
            guard let text = String(data: data, encoding: .utf8), text.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("[") else { throw DropSpaceError.invalid("Unknown Kugou lyric format") }
            let document = try LyricsParser.parse(text, provider: .kugou); try check(); return document
        }
        let key: [UInt8] = [64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105]
        var packed = Array(data.dropFirst(4))
        for index in packed.indices { if index & 4095 == 0 { try check() }; packed[index] ^= key[index % key.count] }
        let maximum = min(1024 * 1024, max(64 * 1024, packed.count * 128))
        var output = Data(), stream = z_stream()
        guard inflateInit_(&stream, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { throw DropSpaceError.unavailable("Cannot initialize KRC decompressor") }
        defer { inflateEnd(&stream) }
        try packed.withUnsafeMutableBufferPointer { input in
            stream.next_in = input.baseAddress; stream.avail_in = UInt32(input.count)
            var buffer = [UInt8](repeating: 0, count: 4096)
            while true {
                try check()
                let code = buffer.withUnsafeMutableBufferPointer { chunk -> Int32 in
                    stream.next_out = chunk.baseAddress; stream.avail_out = UInt32(chunk.count); return inflate(&stream, Z_NO_FLUSH)
                }
                let count = buffer.count - Int(stream.avail_out)
                guard output.count + count <= maximum else { throw DropSpaceError.limit("KRC inflation exceeds limit") }
                output.append(contentsOf: buffer.prefix(count))
                if code == Z_STREAM_END { break }
                guard code == Z_OK, count > 0 || stream.avail_in > 0 else { throw DropSpaceError.invalid("Invalid KRC compressed content") }
            }
        }
        try check()
        guard let decoded = String(data: output, encoding: .utf8) else { throw DropSpaceError.invalid("Invalid KRC text encoding") }
        var rows: [LyricLine] = [], language: String?
        for raw in decoded.components(separatedBy: "\n") {
            try check()
            let line = String(raw.trimmingCharacters(in: .whitespacesAndNewlines).drop(while: { $0 == "\u{FEFF}" }))
            if line.hasPrefix("[language:"), line.hasSuffix("]") { language = String(line.dropFirst(10).dropLast()); continue }
            guard line.hasPrefix("[") else { continue }
            guard let close = line.firstIndex(of: "]") else { throw DropSpaceError.invalid("Incomplete KRC line header") }
            let header = String(line[line.index(after: line.startIndex)..<close])
            if !header.contains(",") { continue }
            let pair = header.components(separatedBy: ",")
            guard pair.count == 2 else { throw DropSpaceError.invalid("Invalid KRC line header") }
            let start = try milliseconds(pair[0]), end = try start + milliseconds(pair[1]), body = String(line[line.index(after: close)...])
            guard end <= 86400, body.isEmpty || end > start else { throw DropSpaceError.invalid("Invalid KRC line duration") }
            if !body.hasPrefix("<") { rows.append(.init(start: start, end: end, text: body)) }
            else {
                var words: [LyricWord] = [], position = body.startIndex
                while position < body.endIndex {
                    try check()
                    guard words.count < 2048, body[position] == "<", let close = body[position...].firstIndex(of: ">") else { throw DropSpaceError.invalid("Invalid KRC word sequence") }
                    let timing = body[body.index(after: position)..<close].components(separatedBy: ",")
                    guard timing.count == 3, timing[2].allSatisfy({ $0.isASCII && $0.isNumber }), Int32(timing[2]) != nil else { throw DropSpaceError.invalid("Invalid KRC word header") }
                    let wordStart = try start + milliseconds(timing[0]), wordEnd = try wordStart + milliseconds(timing[1])
                    guard wordStart >= start, wordStart <= end, wordEnd <= 86400, wordStart >= (words.last?.start ?? start) else { throw DropSpaceError.invalid("Invalid KRC word timing") }
                    let textStart = body.index(after: close), next = body[textStart...].firstIndex(of: "<") ?? body.endIndex
                    words.append(.init(String(body[textStart..<next]), start: wordStart, end: min(end, wordEnd))); position = next
                }
                rows.append(.init(start: start, end: end, text: words.map(\.text).joined(), words: words.filter { $0.end > $0.start }))
            }
            if rows.count > 10000 { throw DropSpaceError.limit("KRC row count exceeds limit") }
        }
        if let language, !language.isEmpty {
            do {
                guard language.utf16.count <= 256 * 1024, let bytes = Data(base64Encoded: language.filter { !$0.isWhitespace }) else { throw DropSpaceError.invalid("Invalid KRC language data") }
                let decoder = JSONDecoder(); decoder.userInfo[CodingUserInfoKey(rawValue: "lyricsMaximumDepth")!] = 8
                let json = try decoder.decode(LyricsJSON.self, from: bytes)
                for value in json["content"].array.prefix(8) {
                    try check()
                    guard value.isObject, value["type"].number == 1, let translations = value["lyricContent"].allArray, translations.count == rows.count else { continue }
                    var translated: [String] = []
                    for row in translations {
                        try check()
                        guard let elements = row.allArray, elements.count == 1, case .string(let text) = elements[0] else { translated = []; break }
                        translated.append(text)
                    }
                    guard translated.count == rows.count else { continue }
                    // No invented language tag: whole-track fastText admission must
                    // independently establish the translated target before AI decisions.
                    for index in rows.indices { rows[index].secondary = translated[index]; rows[index].translationOrigin = .provider }
                    break
                }
            } catch is CancellationError { throw CancellationError() }
            catch { } // Optional translation metadata never destroys valid original words.
        }
        try check()
        rows = rows.filter { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.enumerated().sorted { $0.element.start == $1.element.start ? $0.offset < $1.offset : $0.element.start < $1.element.start }.map(\.element)
        return LyricsDocument(lines: rows, provider: .kugou)
    }
    private static func milliseconds(_ text: String) throws -> Double {
        guard !text.isEmpty, text.allSatisfy({ $0.isASCII && $0.isNumber }), let value = UInt64(text), value <= 86400000 else { throw DropSpaceError.invalid("Invalid KRC timestamp") }
        return Double(value) / 1000
    }
}
