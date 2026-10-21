import Foundation

public enum LyricsDisplay {
    public struct Presentation: Sendable, Equatable {
        public var lineIndex: Int?
        public var waiting: Bool
        public var interlude: Bool
        public init(lineIndex: Int?, waiting: Bool, interlude: Bool) {
            self.lineIndex = lineIndex; self.waiting = waiting; self.interlude = interlude
        }
    }
    public static func presentation(_ document: LyricsDocument, position: Double, delay: Int,
                                    frame supplied: LyricsFrame? = nil) -> Presentation {
        guard position.isFinite, !document.lines.isEmpty else { return .init(lineIndex: nil, waiting: false, interlude: false) }
        let frame = supplied ?? LyricsTimeline.frame(document, position: position, delayMilliseconds: delay)
        if let index = frame.lineIndex, document.lines.indices.contains(index) {
            return .init(lineIndex: index, waiting: false, interlude: false)
        }
        let effective = position + Double(min(30000, max(-30000, delay))) / 1000
        var low = 0, high = document.lines.count - 1, previous = -1
        while low <= high {
            let middle = low + (high - low) / 2
            if document.lines[middle].start <= effective { previous = middle; low = middle + 1 } else { high = middle - 1 }
        }
        let index = max(0, previous), anchor = document.lines[index]
        let gapStart = previous < 0 ? 0 : max(anchor.start, anchor.end)
        return .init(lineIndex: index, waiting: true, interlude: effective - gapStart >= 3)
    }
    public static func nextLine(_ document: LyricsDocument, position: Double, delay: Int) -> Int? {
        guard let current = presentation(document, position: position, delay: delay).lineIndex else { return nil }
        let lines = document.lines, start = lines[current].start
        var low = current + 1
        while low < lines.count, lines[low].start <= start { low += 1 }
        while low < lines.count {
            let next = lines[low].start
            var result: Int?
            repeat {
                if !lines[low].text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { result = low }
                low += 1
            } while low < lines.count && lines[low].start == next
            if let result { return result }
        }
        return nil
    }
    public static func primaryOffset(line: LyricLine, position: Double, delay: Int, wordSynced: Bool,
                                     highlightWidth: Double, textWidth: Double, viewportWidth: Double) -> Double {
        guard [position, highlightWidth, textWidth, viewportWidth].allSatisfy(\.isFinite),
              textWidth > 0, viewportWidth > 0 else { return 0 }
        let effective = position + Double(min(30000, max(-30000, delay))) / 1000
        let offset = wordSynced && !line.words.isEmpty ? highlightWidth - viewportWidth * 0.6 :
            (effective - line.start) * 24 - viewportWidth / 3
        return min(max(0, textWidth - viewportWidth), max(0, offset))
    }
    public static func compact(_ text: String) -> String {
        text.replacingOccurrences(of: "\r\n", with: " ")
            .unicodeScalars.map { [0x0d, 0x0a, 0x85, 0x2028, 0x2029].contains($0.value) ? " " : String($0) }.joined()
    }
    public static func secondary(_ line: LyricLine, target: String, enabled: Bool, showAI: Bool) -> String? {
        guard enabled, let text = line.secondary, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        if let language = LyricsTextPolicy.normalizedLanguage(line.translationLanguage) {
            guard language == LyricsTextPolicy.normalizedLanguage(target) else { return nil }
        } else {
            guard line.translationOrigin != .localAI else { return nil }
            if target.lowercased().hasPrefix("en"), text.unicodeScalars.contains(where: {
                (0x3400...0x9fff).contains($0.value) || (0x20000...0x323af).contains($0.value)
            }) { return nil }
        }
        return showAI && line.translationOrigin == .localAI ? "AI · " + text : text
    }
}

public struct LyricsMarqueeLayout: Equatable, Sendable {
    public var track: String, text: String
    public var start: Double, fontSize: Double, scale: Double, textWidth: Double, viewportWidth: Double
    public var enabled: Bool, reduced: Bool
    public init(track: String, text: String, start: Double, fontSize: Double, scale: Double,
                textWidth: Double, viewportWidth: Double, enabled: Bool, reduced: Bool) {
        self.track = track; self.text = text; self.start = start; self.fontSize = fontSize; self.scale = scale
        self.textWidth = textWidth; self.viewportWidth = viewportWidth; self.enabled = enabled; self.reduced = reduced
    }
}
public struct LyricsMarqueeSession: Sendable {
    private var layout: LyricsMarqueeLayout?
    private var started = 0.0, last = 0.0
    public init() {}
    public mutating func reset() { layout = nil }
    public mutating func update(_ value: LyricsMarqueeLayout, position: Double) -> Double {
        // Match the baseline TimeSpan clock. Subtracting raw binary seconds can
        // leave an exact cycle boundary just below the leading-hold reset.
        let ticks = (position * 10_000_000).rounded()
        guard ticks.isFinite else { return 0 }
        if layout != value || ticks < last { layout = value; started = ticks }
        last = ticks
        return Self.offset(textWidth: value.textWidth, viewportWidth: value.viewportWidth, elapsed: (ticks - started) / 10_000_000,
                           enabled: value.enabled, reduced: value.reduced)
    }
    public static func offset(textWidth: Double, viewportWidth: Double, elapsed: Double, enabled: Bool, reduced: Bool) -> Double {
        guard enabled, !reduced, [textWidth, viewportWidth, elapsed].allSatisfy(\.isFinite),
              viewportWidth > 0, textWidth > viewportWidth else { return 0 }
        let overflow = textWidth - viewportWidth
        let time = max(0, elapsed).truncatingRemainder(dividingBy: overflow / 24 + 2.4)
        return min(overflow, max(0, (time - 1.2) * 24))
    }
}
