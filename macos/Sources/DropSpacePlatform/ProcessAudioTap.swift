import CoreAudio
import CSpectrumBuffer
import DropSpaceCore
import Foundation

public struct AudioProcessIdentity: Sendable, Equatable {
    public let pid: Int32
    public let bundleID: String
    public let launchedAt: Date
    public init(pid: Int32, bundleID: String, launchedAt: Date) {
        self.pid = pid; self.bundleID = bundleID; self.launchedAt = launchedAt
    }
}
public enum ProcessAudioError: Error, Sendable {
    case sourceUnavailable, unsupportedFormat
    case system(operation: String, code: OSStatus)
}
public struct ProcessAudioReading: Sendable {
    public let frame: SpectrumFrame
    public let sampleAge: TimeInterval
    public let droppedSamples: UInt64
}

/// The pointer is accessed by one HAL writer and this actor's consumer only.
/// HAL retains the IO block (and therefore this box) until its IOProc is destroyed.
private final class AudioSampleBuffer: @unchecked Sendable {
    let pointer: OpaquePointer
    init() throws {
        guard let pointer = DSSpectrumBufferCreate() else { throw ProcessAudioError.system(operation: "buffer", code: -108) }
        self.pointer = pointer
    }
    deinit { DSSpectrumBufferDestroy(pointer) }
}

/// Owns a private, unmuted, process-only tap. No endpoint/global fallback.
/// Failed cleanup retains handles for retry; a new tap cannot bypass that state.
public actor ProcessAudioTap {
    private var tap: AudioObjectID = 0
    private var device: AudioObjectID = 0
    private var io: AudioDeviceIOProcID?
    private var running = false
    private var samples: AudioSampleBuffer?
    private var analyzer = SpectrumAnalyzer(sampleRate: 44_100)
    private var sampleRate = 44_100.0
    private var scratch = [Float](repeating: 0, count: 16_384)
    private var last = SpectrumFrame()
    private var dropped: UInt64 = 0
    private var lastTrace = 0.0
    private var drainTask: Task<Void, Never>?
    private var epoch: UInt64 = 0
    private var packetTime = 0.0
    private var formatListener: AudioObjectPropertyListenerBlock?
    private let formatQueue = DispatchQueue(label: "DropSpace.spectrum.format")
    public init() {}

    public func start(_ source: AudioProcessIdentity) throws {
        try stop()
        try Task.checkCancellation()
        let process = try Self.processObject(source)
        let description = CATapDescription(monoMixdownOfProcesses: [process])
        description.name = "DropSpace player spectrum"
        description.uuid = UUID()
        description.isPrivate = true
        description.isExclusive = false
        description.isProcessRestoreEnabled = false
        description.muteBehavior = .unmuted
        do {
            try Self.check(AudioHardwareCreateProcessTap(description, &tap), "create-tap")
            try Task.checkCancellation()
            let format: AudioStreamBasicDescription = try Self.property(tap, kAudioTapPropertyFormat, initial: AudioStreamBasicDescription())
            try Self.validate(format)
            sampleRate = format.mSampleRate
            let tapUID = try Self.stringProperty(tap, kAudioTapPropertyUID)
            let composition: [String: Any] = [
                kAudioAggregateDeviceNameKey: "DropSpace spectrum input",
                kAudioAggregateDeviceUIDKey: "io.github.airanluo-dot.DropSpace.spectrum." + UUID().uuidString,
                kAudioAggregateDeviceIsPrivateKey: true,
                kAudioAggregateDeviceTapAutoStartKey: false,
                kAudioAggregateDeviceTapListKey: [[kAudioSubTapUIDKey: tapUID, kAudioSubTapDriftCompensationKey: true]]
            ]
            try Self.check(AudioHardwareCreateAggregateDevice(composition as CFDictionary, &device), "create-device")
            var taps: CFArray?
            var tapAddress = AudioObjectPropertyAddress(mSelector: kAudioAggregateDevicePropertyTapList, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            var tapSize = UInt32(MemoryLayout<CFArray?>.size)
            try Self.check(withUnsafeMutablePointer(to: &taps) { AudioObjectGetPropertyData(device, &tapAddress, 0, nil, &tapSize, $0) }, "read-tap-binding")
            guard taps as? [String] == [tapUID] else { throw ProcessAudioError.system(operation: "tap-binding", code: -1) }
            trace("bound process=\(process) requested=\(description.uuid.uuidString) actual=\(tapUID)")
            let input: AudioStreamBasicDescription = try Self.property(device, kAudioDevicePropertyStreamFormat, scope: kAudioObjectPropertyScopeInput, initial: AudioStreamBasicDescription())
            try Self.validate(input)
            guard input.mSampleRate == sampleRate else { throw ProcessAudioError.unsupportedFormat }
            let buffer = try AudioSampleBuffer()
            samples = buffer
            let listener: AudioObjectPropertyListenerBlock = { _, _ in DSSpectrumBufferInvalidate(buffer.pointer) }
            var formatAddress = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            try Self.check(AudioObjectAddPropertyListenerBlock(tap, &formatAddress, formatQueue, listener), "observe-format")
            formatListener = listener
            try Self.check(AudioDeviceCreateIOProcIDWithBlock(&io, device, nil) { _, input, _, _, _ in
                DSSpectrumBufferWriteAudio(buffer.pointer, input)
            }, "create-io")
            try Task.checkCancellation()
            // Reconfirm the HAL identity immediately before beginning capture.
            guard try Self.processObject(source) == process else { throw ProcessAudioError.sourceUnavailable }
            try Self.check(AudioDeviceStart(device, io), "start")
            running = true; analyzer = SpectrumAnalyzer(sampleRate: sampleRate)
            last = .init(mode: .processLoopback); dropped = 0; packetTime = 0
            let token = epoch
            drainTask = Task { [weak self] in
                while !Task.isCancelled {
                    guard await self?.drain(epoch: token) == true else { return }
                    do { try await Task.sleep(for: .milliseconds(20)) } catch { return }
                }
            }
            trace("started pid=\(source.pid) tap=\(tap) device=\(device) rate=\(sampleRate)")
        } catch {
            // A cleanup failure takes precedence: keeping ownership is necessary
            // before any subsequent restart can be attempted.
            try stop()
            throw error
        }
    }
    public func read() throws -> ProcessAudioReading {
        guard let samples, running else { return .init(frame: .init(), sampleAge: .infinity, droppedSamples: dropped) }
        guard DSSpectrumBufferIsValid(samples.pointer) != 0 else { throw ProcessAudioError.unsupportedFormat }
        let age = packetTime == 0 ? TimeInterval.infinity : max(0, DSSpectrumMonotonicTime() - packetTime)
        return .init(frame: SpectrumFreshness.apply(last, age: age), sampleAge: age, droppedSamples: dropped)
    }
    private func drain(epoch token: UInt64) -> Bool {
        guard epoch == token, let samples, running, DSSpectrumBufferIsValid(samples.pointer) != 0 else { return false }
        let count = scratch.withUnsafeMutableBufferPointer { DSSpectrumBufferRead(samples.pointer, $0.baseAddress, $0.count) }
        let totalDropped = DSSpectrumBufferDropped(samples.pointer)
        if totalDropped != dropped {
            // Do not join FFT windows across missing packets.
            dropped = totalDropped; analyzer = SpectrumAnalyzer(sampleRate: sampleRate); last = .init(mode: .processLoopback)
        } else {
            for index in 0..<count { if let frame = analyzer.add(Double(scratch[index])) { last = frame } }
        }
        packetTime = DSSpectrumBufferLastPacketTime(samples.pointer)
        #if DEBUG
        if DSSpectrumMonotonicTime() - lastTrace >= 1 {
            lastTrace = DSSpectrumMonotonicTime()
            let peak = scratch.prefix(count).map { abs($0) }.max() ?? 0
            let age = packetTime == 0 ? TimeInterval.infinity : max(0, DSSpectrumMonotonicTime() - packetTime)
            trace("packet frames=\(count) revision=\(last.revision) age=\(age) dropped=\(dropped) peak=\(peak) bands=\(last.bands)")
        }
        #endif
        return true
    }
    public func stop() throws {
        epoch &+= 1; drainTask?.cancel(); drainTask = nil
        if running {
            let code = AudioDeviceStop(device, io)
            try Self.check(code, "stop")
            running = false
        }
        if let io {
            try Self.check(AudioDeviceDestroyIOProcID(device, io), "destroy-io")
            self.io = nil
        }
        if device != 0 {
            try Self.check(AudioHardwareDestroyAggregateDevice(device), "destroy-device")
            device = 0
        }
        if tap != 0 {
            if let formatListener {
                var address = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
                try Self.check(AudioObjectRemovePropertyListenerBlock(tap, &address, formatQueue, formatListener), "remove-format-observer")
                self.formatListener = nil
            }
            try Self.check(AudioHardwareDestroyProcessTap(tap), "destroy-tap")
            tap = 0
        }
        if samples != nil { trace("stopped tap=0 device=0 io=nil") }
        samples = nil; last = .init()
    }
    static func processObject(_ source: AudioProcessIdentity) throws -> AudioObjectID {
        guard source.pid > 0, !source.bundleID.isEmpty else { throw ProcessAudioError.sourceUnavailable }
        var pid = source.pid, object = AudioObjectID(0)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        try check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, UInt32(MemoryLayout<pid_t>.size), &pid, &size, &object), "find-process")
        guard object != 0 else { throw ProcessAudioError.sourceUnavailable }
        let actualPID = try property(object, kAudioProcessPropertyPID, initial: Int32(0))
        var bundle: CFString?
        address.mSelector = kAudioProcessPropertyBundleID; size = UInt32(MemoryLayout<CFString?>.size)
        let code = withUnsafeMutablePointer(to: &bundle) { AudioObjectGetPropertyData(object, &address, 0, nil, &size, $0) }
        try check(code, "process-identity")
        guard actualPID == source.pid, bundle as String? == source.bundleID else { throw ProcessAudioError.sourceUnavailable }
        return object
    }
    private static func validate(_ format: AudioStreamBasicDescription) throws {
        guard format.mFormatID == kAudioFormatLinearPCM, format.mChannelsPerFrame == 1,
              format.mBitsPerChannel == 32, format.mBytesPerFrame == 4,
              format.mFormatFlags & kAudioFormatFlagIsFloat != 0,
              format.mFormatFlags & kAudioFormatFlagIsPacked != 0,
              format.mFormatFlags & kAudioFormatFlagIsBigEndian == 0,
              format.mSampleRate.isFinite, (8_000...384_000).contains(format.mSampleRate) else { throw ProcessAudioError.unsupportedFormat }
    }
    private static func property<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal, initial: T) throws -> T {
        var value = initial, size = UInt32(MemoryLayout<T>.size)
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        let code = withUnsafeMutablePointer(to: &value) { AudioObjectGetPropertyData(object, &address, 0, nil, &size, $0) }
        try check(code, "property-\(selector)")
        return value
    }
    private static func stringProperty(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) throws -> String {
        var value: CFString?
        var size = UInt32(MemoryLayout<CFString?>.size)
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        try check(withUnsafeMutablePointer(to: &value) { AudioObjectGetPropertyData(object, &address, 0, nil, &size, $0) }, "string-property-\(selector)")
        guard let value else { throw ProcessAudioError.sourceUnavailable }
        return value as String
    }
    private static func check(_ status: OSStatus, _ operation: String) throws {
        guard status == noErr else { throw ProcessAudioError.system(operation: operation, code: status) }
    }
    private func trace(_ message: String) {
        #if DEBUG
        if ProcessInfo.processInfo.environment["DROPSPACE_VALIDATION_SPECTRUM_TRACE"] == "1" { FileHandle.standardError.write(Data(("Spectrum \(message)\n").utf8)) }
        #endif
    }
}
