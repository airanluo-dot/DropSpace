import Foundation

public enum TtmlTimingMode: Sendable { case automatic, absolute, parentRelative }

/// Bounded native implementation of the frozen LyricsParser's three input dialects.
public enum LyricsParser {
    public static let maximumCharacters = 2 * 1024 * 1024
    private static let maximumLines = 10000
    private static let stamp = #"((?:\d{1,3}:)?\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?)"#
    private static let lineStamp = try! NSRegularExpression(pattern: #"\["# + stamp + #"\]"#)
    private static let wordStamp = try! NSRegularExpression(pattern: "<" + stamp + ">")
    private static let metadata = try! NSRegularExpression(pattern: #"^\[(?:ti|ar|al|by|offset|re|ve|length|id|la|lr|km|us|au):"#, options: .caseInsensitive)
    private static let yrcHeader = try! NSRegularExpression(pattern: #"^\[(\d+),(\d+)\]"#)
    private static let yrcWord = try! NSRegularExpression(pattern: #"\((\d+),(\d+),\d+\)"#)

    public static func parse(_ text: String, provider: LyricsProvider, secondary: String? = nil,
                             timing: TtmlTimingMode = .automatic) throws -> LyricsDocument {
        func read(_ value: String) throws -> [LyricLine] {
            guard value.utf16.count <= maximumCharacters else { throw DropSpaceError.limit("Lyrics exceed the content limit") }
            try Task.checkCancellation()
            if value.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("<"), value.range(of: "<tt", options: .caseInsensitive) != nil {
                return try ttml(value, provider: provider, timing: timing)
            }
            return try timedText(value)
        }
        func ordered(_ lines: [LyricLine]) -> [LyricLine] {
            lines.enumerated().sorted { $0.element.start == $1.element.start ? $0.offset < $1.offset : $0.element.start < $1.element.start }.map(\.element)
        }
        var lines = Array(ordered(try read(text)).prefix(maximumLines))
        let translations = try (secondary?.isEmpty == false ? ordered(read(secondary!)) : [])
        var matches = Array(repeating: -1, count: lines.count), original = 0
        if !lines.isEmpty {
            for index in translations.indices {
                let start = translations[index].start
                while original + 1 < lines.count && abs(lines[original + 1].start - start) <= abs(lines[original].start - start) { original += 1 }
                let distance = abs(lines[original].start - start), previous = matches[original]
                if distance <= 0.250 && (previous < 0 || distance < abs(lines[original].start - translations[previous].start)) { matches[original] = index }
            }
        }
        for index in lines.indices {
            var line = lines[index]
            if index + 1 < lines.count, lines[index + 1].start > line.start,
               line.end <= line.start || lines[index + 1].start < line.end {
                line.end = lines[index + 1].start
                if line.timeBasis != .untimed { line.timeBasis = .timed }
            }
            if line.end <= line.start { line.end = line.start + 5; line.timeBasis = .estimatedEnd }
            line.words = line.words.compactMap { word in
                var value = word; value.end = word.end > word.start ? min(word.end, line.end) : line.end
                return value.end > value.start ? value : nil
            }
            if matches[index] >= 0 {
                let translation = translations[matches[index]]
                line.secondary = translation.text
                if !translation.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { line.translationOrigin = .provider }
                line.translationLanguage = translation.sourceLanguage; line.translationLanguageIsExplicit = translation.sourceLanguage != nil
            }
            lines[index] = line
        }
        return LyricsDocument(lines: lines, provider: provider)
    }

    private static func timedText(_ text: String) throws -> [LyricLine] {
        var output: [LyricLine] = [], plainLines: [String] = [], boundaries: [Double] = []
        var offset: Double = 0
        for raw in text.components(separatedBy: "\n").prefix(maximumLines) {
            try Task.checkCancellation()
            let value = raw.trimmingCharacters(in: .whitespacesAndNewlines).drop(while: { $0 == "\u{FEFF}" }).description
            if value.isEmpty { if !plainLines.isEmpty { plainLines.append("") }; continue }
            if value.lowercased().hasPrefix("[offset:"), value.hasSuffix("]") {
                if let milliseconds = Double(value.dropFirst(8).dropLast()), milliseconds.isFinite { offset = min(30000, max(-30000, milliseconds)) / 1000 }
                continue
            }
            if let yrc = first(yrcHeader, value) {
                let stamps = matches(yrcWord, value)
                let words = stamps.prefix(2048).enumerated().map { index, stamp in
                    let start = milliseconds(group(stamp, 1, value)), duration = milliseconds(group(stamp, 2, value))
                    return LyricWord(slice(value, stamp.range.location + stamp.range.length, index + 1 < stamps.count ? stamps[index + 1].range.location : value.utf16.count), start: start, end: start + duration)
                }
                if !words.isEmpty {
                    let start = milliseconds(group(yrc, 1, value))
                    output.append(LyricLine(start: start, end: start + milliseconds(group(yrc, 2, value)), text: words.map(\.text).joined(), words: words))
                }
                continue
            }
            let times = matches(lineStamp, value)
            guard let firstTime = times.first, firstTime.range.location == 0, let last = times.last else {
                if first(metadata, value) == nil { plainLines.append(value) }; continue
            }
            let content = slice(value, last.range.location + last.range.length, value.utf16.count)
            let wordTimes = matches(wordStamp, content)
            let words = wordTimes.prefix(2048).enumerated().map { index, stamp in
                LyricWord(slice(content, stamp.range.location + stamp.range.length, index + 1 < wordTimes.count ? wordTimes[index + 1].range.location : content.utf16.count),
                          start: timestamp(group(stamp, 1, content)), end: index + 1 < wordTimes.count ? timestamp(group(wordTimes[index + 1], 1, content)) : 0)
            }
            let plain = wordStamp.stringByReplacingMatches(in: content, range: NSRange(content.startIndex..., in: content), withTemplate: "").trimmingCharacters(in: .whitespacesAndNewlines)
            if plain.isEmpty { boundaries += times.prefix(64).map { timestamp(group($0, 1, value)) }; continue }
            for time in times.prefix(64) {
                let start = timestamp(group(time, 1, value)), shift = start - timestamp(group(firstTime, 1, value))
                let occurrence = words.map { word in LyricWord(word.text, start: word.start + shift, end: word.end > word.start ? word.end + shift : 0) }
                output.append(LyricLine(start: start, end: start, text: plain, words: occurrence))
            }
            if output.count >= maximumLines { break }
        }
        if output.isEmpty, !plainLines.isEmpty {
            var line = LyricLine(start: 0, end: 86400, text: plainLines.joined(separator: "\n").replacingOccurrences(of: #"\s+$"#, with: "", options: .regularExpression))
            line.timeBasis = .untimed; output.append(line)
        }
        boundaries.sort()
        for index in output.indices {
            var low = 0, high = boundaries.count
            while low < high { let mid = (low + high) / 2; if boundaries[mid] <= output[index].start { low = mid + 1 } else { high = mid } }
            if low < boundaries.count, output[index].end <= output[index].start || boundaries[low] < output[index].end { output[index].end = boundaries[low] }
        }
        if offset < 0 { output.removeAll { $0.end > $0.start && $0.end + offset <= 0 } }
        if offset != 0 {
            for index in output.indices {
                output[index].start = max(0, output[index].start + offset); output[index].end = max(0, output[index].end + offset)
                output[index].words = output[index].words.map { LyricWord($0.text, start: max(0, $0.start + offset), end: max(0, $0.end + offset)) }
            }
        }
        return output
    }

    private static func ttml(_ text: String, provider: LyricsProvider, timing: TtmlTimingMode) throws -> [LyricLine] {
        let tree = try LyricXMLTree(text)
        let absolute = timing == .absolute || (timing == .automatic && (provider == .amll || tree.root?.attributes.contains { $0.key.hasPrefix("xmlns") && $0.value.lowercased().contains("itunes.apple.com") } == true))
        var result: [LyricLine] = []
        for paragraph in tree.paragraphs.prefix(maximumLines) {
            try Task.checkCancellation()
            let parentStart = absolute ? 0 : paragraph.ancestors.reduce(0) { $0 + timestamp($1.attributes["begin"] ?? "0s") }
            let start = parentStart + timestamp(paragraph.attributes["begin"] ?? "0s")
            let end = resolvedEnd(paragraph, start: start, origin: parentStart, fallback: start + 5)
            var primary = "", romanization = "", translations: [(String, String?)] = [], words: [LyricWord] = []
            func read(_ child: LyricXMLNode.Child, _ parentStart: Double, _ parentEnd: Double) {
                switch child {
                case .text(let value): primary += value
                case .element(let node):
                    if node.name.lowercased() == "br" { primary += "\n"; return }
                    if node.isRuby { return }
                    if node.isRomanization { romanization += node.text; return }
                    if node.isTranslation {
                        if !node.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { translations.append((node.text, translationLanguage(node))) }
                        return
                    }
                    let start = node.attributes["begin"].map { timestamp($0) + (absolute ? 0 : parentStart) } ?? parentStart
                    let end = resolvedEnd(node, start: start, origin: absolute ? 0 : parentStart, fallback: parentEnd)
                    if node.children.allSatisfy({ if case .text = $0 { return true }; return false }) {
                        let value = node.text; primary += value
                        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
                        if node.attributes["begin"] != nil, !trimmed.isEmpty, end > start { words.append(LyricWord(trimmed, start: start, end: end)) }
                    } else { for child in node.children { read(child, start, end) } }
                }
            }
            for child in paragraph.children { read(child, start, end) }
            let value = primary.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !value.isEmpty else { continue }
            let translation = translations.map(\.0).joined().trimmingCharacters(in: .whitespacesAndNewlines)
            let second = translation.isEmpty ? romanization.trimmingCharacters(in: .whitespacesAndNewlines) : translation
            var line = LyricLine(start: start, end: end, text: value, secondary: second.isEmpty ? nil : second, words: words)
            if paragraph.attributes["end"] == nil && paragraph.attributes["dur"] == nil { line.timeBasis = .estimatedEnd }
            line.sourceLanguage = sourceLanguage(paragraph)
            let languages = Set(translations.map(\.1))
            line.translationOrigin = translation.isEmpty ? .none : .provider
            line.translationLanguage = !translation.isEmpty && languages.count == 1 ? languages.first! : nil
            line.translationLanguageIsExplicit = !translation.isEmpty && line.translationLanguage != nil
            result.append(line)
        }
        return result
    }
    private static func sourceLanguage(_ paragraph: LyricXMLNode) -> String? {
        var languages = Set<String>(), unknown = false
        paragraph.visitText { text, parent in
            guard text.unicodeScalars.contains(where: CharacterSet.letters.contains) else { return }
            let chain = [parent] + parent.ancestors
            if chain.prefix(while: { $0 !== paragraph }).contains(where: { $0.isTranslation || $0.isRomanization || $0.isRuby }) { return }
            guard let language = LyricsTextPolicy.normalizedLanguage(chain.compactMap { $0.attributes["xml:lang"] }.first) else { unknown = true; return }
            languages.insert(language)
        }
        return unknown || languages.isEmpty ? nil : languages.count == 1 ? languages.first : "mul"
    }
    private static func translationLanguage(_ node: LyricXMLNode) -> String? {
        var languages = Set<String>(), unknown = false
        node.visitText { text, parent in
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
            var current: LyricXMLNode? = parent, declaration: String?
            while let value = current {
                if let language = value.attributes["xml:lang"] { declaration = language; break }
                if value === node { break }; current = value.parent
            }
            if let language = LyricsTextPolicy.normalizedLanguage(declaration) { languages.insert(language) } else { unknown = true }
        }
        return !unknown && languages.count == 1 ? languages.first : nil
    }
    private static func resolvedEnd(_ node: LyricXMLNode, start: Double, origin: Double, fallback: Double) -> Double {
        if let end = node.attributes["end"] { return max(start, origin + timestamp(end)) }
        if let duration = node.attributes["dur"] { return start + timestamp(duration) }
        return fallback
    }
    private static func matches(_ regex: NSRegularExpression, _ value: String) -> [NSTextCheckingResult] { regex.matches(in: value, range: NSRange(value.startIndex..., in: value)) }
    private static func first(_ regex: NSRegularExpression, _ value: String) -> NSTextCheckingResult? { regex.firstMatch(in: value, range: NSRange(value.startIndex..., in: value)) }
    private static func slice(_ text: String, _ start: Int, _ end: Int) -> String { (text as NSString).substring(with: NSRange(location: start, length: max(0, end - start))) }
    private static func group(_ match: NSTextCheckingResult, _ index: Int, _ text: String) -> String { (text as NSString).substring(with: match.range(at: index)) }
    private static func milliseconds(_ value: String) -> Double { guard let number = Double(value), number.isFinite else { return 0 }; return min(86400000, max(0, number)) / 1000 }
    private static func timestamp(_ input: String) -> Double {
        let value = input.trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: ",", with: ".").lowercased()
        if value.hasSuffix("ms") { return milliseconds(String(value.dropLast(2))) }
        if value.hasSuffix("s"), let number = Double(value.dropLast()), number.isFinite { return min(86400, max(0, number)) }
        if value.hasSuffix("min"), let number = Double(value.dropLast(3)), number.isFinite { return min(86400, max(0, number * 60)) }
        var total: Double = 0
        for part in value.components(separatedBy: ":").prefix(3) { guard let number = Double(part), number.isFinite, number >= 0 else { return 0 }; total = total * 60 + number }
        return min(86400, max(0, total))
    }
}

private final class LyricXMLNode {
    enum Child { case text(String), element(LyricXMLNode) }
    let name: String
    let attributes: [String: String]
    weak var parent: LyricXMLNode?
    var children: [Child] = []
    init(name: String, attributes: [String: String], parent: LyricXMLNode?) { self.name = name.components(separatedBy: ":").last!; self.attributes = attributes; self.parent = parent }
    var ancestors: [LyricXMLNode] { var result: [LyricXMLNode] = []; var node = parent; while let current = node { result.append(current); node = current.parent }; return result }
    var text: String { children.map { switch $0 { case .text(let text): text; case .element(let node): node.text } }.joined() }
    func localAttribute(_ name: String) -> String? { attributes.first { $0.key.components(separatedBy: ":").last?.lowercased() == name }?.value }
    var roles: [String] { (localAttribute("role") ?? "").lowercased().replacingOccurrences(of: "-", with: "").replacingOccurrences(of: "_", with: "").split(whereSeparator: \.isWhitespace).map(String.init) }
    var isRomanization: Bool { roles.contains { $0 == "xroman" || $0 == "pinyin" || $0.contains("transliteration") || $0.contains("romanization") } }
    var isTranslation: Bool { roles.contains { $0.contains("translation") } }
    var isRuby: Bool { ["text", "textcontainer"].contains(localAttribute("ruby")?.lowercased() ?? "") }
    func visitText(_ body: (String, LyricXMLNode) -> Void) { for child in children { switch child { case .text(let text): body(text, self); case .element(let node): node.visitText(body) } } }
}
private final class LyricXMLTree: NSObject, XMLParserDelegate {
    var root: LyricXMLNode?
    var paragraphs: [LyricXMLNode] = []
    private var stack: [LyricXMLNode] = []
    private var failure: Error?
    init(_ text: String) throws {
        super.init()
        // Reject declarations before the parser may expand internal entities. External
        // entity loading is also disabled on the parser, including its delegate path.
        guard text.range(of: "<!DOCTYPE", options: .caseInsensitive) == nil,
              text.range(of: "<!ENTITY", options: .caseInsensitive) == nil else { throw DropSpaceError.invalid("Lyrics XML declarations are not allowed") }
        let parser = XMLParser(data: Data(text.utf8)); parser.shouldResolveExternalEntities = false; parser.delegate = self
        guard parser.parse(), failure == nil, root != nil else { throw failure ?? parser.parserError ?? DropSpaceError.invalid("Invalid lyrics XML") }
    }
    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String]) {
        guard stack.count <= 64 else { failure = DropSpaceError.limit("Lyrics XML nesting exceeds the supported depth"); parser.abortParsing(); return }
        if Task.isCancelled { failure = CancellationError(); parser.abortParsing(); return }
        let node = LyricXMLNode(name: elementName, attributes: attributeDict, parent: stack.last)
        if let parent = stack.last { parent.children.append(.element(node)) } else { root = node }
        if node.name == "p", paragraphs.count < 10000 { paragraphs.append(node) }; stack.append(node)
    }
    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) { if !stack.isEmpty { stack.removeLast() } }
    func parser(_ parser: XMLParser, foundCharacters string: String) {
        guard let node = stack.last else { return }
        if case .text(let previous) = node.children.last { node.children[node.children.count - 1] = .text(previous + string) }
        else { node.children.append(.text(string)) }
    }
    func parser(_ parser: XMLParser, foundCDATA CDATABlock: Data) { if let value = String(data: CDATABlock, encoding: .utf8) { self.parser(parser, foundCharacters: value) } }
    func parser(_ parser: XMLParser, resolveExternalEntityName name: String, systemID: String?) -> Data? { failure = DropSpaceError.invalid("External lyrics entities are not allowed"); parser.abortParsing(); return nil }
}
