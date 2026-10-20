import Foundation

public enum AudioCaptureMode: Sendable { case stopped, processLoopback, unavailable }
public struct SpectrumFrame: Sendable, Equatable {
    public var mode: AudioCaptureMode
    public var bands: [Double]
    public var revision: UInt64
    public init(mode: AudioCaptureMode = .stopped, bands: [Double] = Array(repeating: 0, count: 6), revision: UInt64 = 0) {
        self.mode = mode; self.bands = bands; self.revision = revision
    }
}

/// Frozen Beta 7 Hann window, FFT, logarithmic bands and asymmetric smoothing.
/// The native capture rate is supplied by HAL instead of forcing a resample.
public struct SpectrumAnalyzer: Sendable {
    public static let sampleCount = 2_048
    public static let bandCount = 6
    private var real = [Double](repeating: 0, count: sampleCount)
    private var imaginary = [Double](repeating: 0, count: sampleCount)
    private var smoothed = [Double](repeating: 0, count: bandCount)
    private var count = 0
    private var revision: UInt64 = 0
    private let sampleRate: Double
    public init(sampleRate: Double) {
        precondition(sampleRate.isFinite && sampleRate > 0)
        self.sampleRate = sampleRate
    }
    public mutating func add(_ sample: Double) -> SpectrumFrame? {
        let n = Self.sampleCount
        real[count] = min(1, max(-1, sample.isFinite ? sample : 0)) * (0.5 - 0.5 * cos(2 * .pi * Double(count) / Double(n - 1)))
        imaginary[count] = 0
        count += 1
        guard count == n else { return nil }
        count = 0
        var reverse = 0
        for index in 1..<n {
            var bit = n >> 1
            while reverse & bit != 0 { reverse ^= bit; bit >>= 1 }
            reverse ^= bit
            if index < reverse { real.swapAt(index, reverse); imaginary.swapAt(index, reverse) }
        }
        var length = 2
        while length <= n {
            let angle = -2 * Double.pi / Double(length)
            let stepReal = cos(angle), stepImaginary = sin(angle)
            for offset in stride(from: 0, to: n, by: length) {
                var phaseReal = 1.0, phaseImaginary = 0.0
                for index in 0..<(length / 2) {
                    let a = offset + index, b = a + length / 2
                    let oddReal = real[b] * phaseReal - imaginary[b] * phaseImaginary
                    let oddImaginary = real[b] * phaseImaginary + imaginary[b] * phaseReal
                    real[b] = real[a] - oddReal; imaginary[b] = imaginary[a] - oddImaginary
                    real[a] += oddReal; imaginary[a] += oddImaginary
                    let nextReal = phaseReal * stepReal - phaseImaginary * stepImaginary
                    phaseImaginary = phaseReal * stepImaginary + phaseImaginary * stepReal
                    phaseReal = nextReal
                }
            }
            length <<= 1
        }
        for band in 0..<Self.bandCount {
            let low = 60 * pow(250, Double(band) / 6)
            let high = 60 * pow(250, Double(band + 1) / 6)
            let begin = min(n / 2 - 1, max(1, Int(low * Double(n) / sampleRate)))
            let end = min(n / 2, max(begin + 1, Int(ceil(high * Double(n) / sampleRate))))
            var peak = 0.0
            for bin in begin..<end { peak = max(peak, hypot(real[bin], imaginary[bin]) * 4 / Double(n)) }
            let level = min(1, max(0, (20 * log10(max(peak, 0.00001)) + 70) / 70))
            smoothed[band] += (level - smoothed[band]) * (level > smoothed[band] ? 0.8 : 0.25)
        }
        revision &+= 1
        return .init(mode: .processLoopback, bands: smoothed, revision: revision)
    }
}

public enum SpectrumFreshness {
    public static func apply(_ frame: SpectrumFrame, age: TimeInterval) -> SpectrumFrame {
        guard frame.mode == .processLoopback else { return frame }
        if age >= 0 && age <= 0.15 { return frame }
        var result = frame
        guard age.isFinite, age >= 0, age < 0.6 else { result.bands = Array(repeating: 0, count: 6); return result }
        let t = (age - 0.15) / 0.45, gain = 1 - t * t * (3 - 2 * t)
        result.bands = frame.bands.map { $0.isFinite ? min(1, max(0, $0)) * gain : 0 }
        return result
    }
    public static func shouldRecover(wanted: Bool, mode: AudioCaptureMode, sampleAge: TimeInterval, sinceAttempt: TimeInterval) -> Bool {
        wanted && sinceAttempt >= 5 && (mode != .processLoopback || sampleAge >= 5)
    }
}
