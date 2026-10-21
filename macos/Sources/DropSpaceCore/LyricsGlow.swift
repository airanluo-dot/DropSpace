import Foundation

public enum LyricsGlowPolicy {
    public static func eligible(mode: String, playing: Bool, islandVisible: Bool,
                                translationVisible: Bool, origin: LyricsTranslationOrigin, translation: String?) -> Bool {
        guard playing, islandVisible else { return false }
        return mode == "music" || mode == "aiLyrics" && translationVisible && origin == .localAI &&
            !(translation?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
    }
    public static func aiEnabledChanged(_ enabled: Bool) -> String { enabled ? "aiLyrics" : "off" }
    public static func energy(_ spectrum: SpectrumFrame) -> Double {
        guard spectrum.mode == .processLoopback, !spectrum.bands.isEmpty else { return 0 }
        let levels = spectrum.bands.map(unit)
        return 0.55 * sqrt(levels.reduce(0) { $0 + $1 * $1 } / Double(levels.count)) + 0.45 * (levels.max() ?? 0)
    }
}

private func unit(_ value: Double) -> Double { value.isFinite ? min(1, max(0, value)) : 0 }

/// Value-only state; native windows and clocks never travel with an effect.
public struct LyricsGlowVisualState: Sendable, Equatable, Codable {
    public var brightness = 0.0
    public var phase = 0.0
    public var simplification = 0.0
    public var bands = [Double](repeating: 0, count: 6)
    public init() {}
}

/// Frozen Beta 7 envelope. Pausing/off freezes color and contour while fading.
public struct LyricsGlowEnvelope: Sendable {
    public private(set) var state = LyricsGlowVisualState()
    public init() {}
    public mutating func restore(_ value: LyricsGlowVisualState) {
        state.brightness = min(0.46, unit(value.brightness))
        state.phase = value.phase.isFinite ? max(0, value.phase) : 0
        state.simplification = unit(value.simplification)
        state.bands = (0..<6).map { $0 < value.bands.count ? unit(value.bands[$0]) : 0 }
    }
    public mutating func advance(eligible: Bool, energy: Double, seconds: Double,
                                 reducedMotion: Bool = false, bands: [Double] = [], simplified: Bool = false) {
        guard seconds.isFinite, seconds > 0 else { return }
        let shapeBlend = 1 - exp(-min(seconds, 5) / (reducedMotion ? 0.028 : 0.12))
        state.simplification += ((simplified ? 1 : 0) - state.simplification) * shapeBlend
        if eligible && !reducedMotion {
            let motionSeconds = min(seconds, 0.1)
            state.phase += motionSeconds
            for index in 0..<6 {
                let band = index < bands.count ? unit(bands[index]) : 0
                let blend = 1 - exp(-motionSeconds / (band > state.bands[index] ? 0.045 : 0.12))
                state.bands[index] += (band - state.bands[index]) * blend
            }
        }
        let target = eligible ? 0.08 + (reducedMotion ? 0 : 0.38 * sqrt(unit(energy))) : 0
        let timeConstant = !eligible ? 0.28 : target > state.brightness ? 0.055 : 0.18
        state.brightness += (target - state.brightness) * (1 - exp(-min(seconds, 5) / timeConstant))
        if !eligible && state.brightness < 0.002 { state.brightness = 0 }
    }
}

/// Glow-only local contrast. Never feeds modified energy back into the meter.
public struct LyricsGlowAudioResponse: Sendable {
    private var mean = [Double](repeating: 0, count: 7)
    private var initialized = false
    public private(set) var energy = 0.0
    public private(set) var bands = [Double](repeating: 0, count: 6)
    public init() {}
    public mutating func reset() { self = .init() }
    public mutating func advance(energy: Double, bands: [Double], seconds: Double) {
        guard seconds.isFinite, seconds > 0 else { return }
        let blend = 1 - exp(-min(seconds, 0.1) / 1.5)
        self.energy = map(0, energy, blend)
        for index in 0..<6 { self.bands[index] = map(index + 1, index < bands.count ? bands[index] : 0, blend) }
        initialized = true
    }
    private mutating func map(_ index: Int, _ input: Double, _ blend: Double) -> Double {
        let level = unit(input)
        if !initialized { mean[index] = level }
        let response = level <= 0.015 ? 0 : unit(0.55 * level + 4 * (level - mean[index]))
        mean[index] += (level - mean[index]) * blend
        return response
    }
}
