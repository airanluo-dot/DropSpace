import Foundation
import Testing
@testable import DropSpaceCore

@Test func spectrumMatchesFrozenWindowsPCMAndFreshness() throws {
    let url = try #require(Bundle.module.url(forResource: "spectrum-baseline", withExtension: "json", subdirectory: "Fixtures"))
    let root = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    #expect(root["baseline"] as? String == "1cd4620dd58a67b0963b73487743137813b8864a")
    for input in try #require(root["cases"] as? [[String: Any]]) {
        let rate = try #require(input["rate"] as? Double), kind = try #require(input["kind"] as? String)
        let expected = try #require(input["frames"] as? [[String: Any]])
        var analyzer = SpectrumAnalyzer(sampleRate: rate), frames: [SpectrumFrame] = []
        for i in 0..<8_192 {
            let sample: Double
            switch kind {
            case "tone": sample = 0.2 * sin(2 * .pi * 1000 * Double(i) / rate)
            case "mixed": sample = i < 4096 ? 0.3 * sin(2 * .pi * 80 * Double(i) / rate) + 0.1 * cos(2 * .pi * 4000 * Double(i) / rate) : 0
            case "invalid": sample = [Double.nan, .infinity, 2, -2][i % 4]
            default: sample = 0
            }
            if let frame = analyzer.add(sample) { frames.append(frame) }
        }
        #expect(frames.count == expected.count)
        for (frame, reference) in zip(frames, expected) {
            #expect(frame.revision == (reference["revision"] as? NSNumber)?.uint64Value)
            for (value, other) in zip(frame.bands, try #require(reference["bands"] as? [Double])) { #expect(abs(value - other) < 1e-9) }
        }
    }
    let frame = SpectrumFrame(mode: .processLoopback, bands: [0,0.2,0.4,0.6,0.8,1], revision: 42)
    for input in try #require(root["freshness"] as? [[String: Any]]) {
        let actual = SpectrumFreshness.apply(frame, age: try #require(input["age"] as? Double))
        for (value, other) in zip(actual.bands, try #require(input["bands"] as? [Double])) { #expect(abs(value - other) < 1e-9) }
    }
    #expect(SpectrumFreshness.apply(frame, age: .nan).bands == Array(repeating: 0, count: 6))
    for input in try #require(root["recovery"] as? [[String: Any]]) {
        let mode: AudioCaptureMode = input["mode"] as? Int == 1 ? .processLoopback : input["mode"] as? Int == 0 ? .stopped : .unavailable
        #expect(SpectrumFreshness.shouldRecover(wanted: try #require(input["wanted"] as? Bool), mode: mode, sampleAge: try #require(input["age"] as? Double), sinceAttempt: try #require(input["attempt"] as? Double)) == input["recover"] as? Bool)
    }
}
