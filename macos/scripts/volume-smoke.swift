// Explicit real-device probe; no playback and no output-device switching.
import Foundation
import CoreAudio
var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
var device = AudioObjectID(0), size = UInt32(MemoryLayout<AudioObjectID>.size)
guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device) == noErr else { fatalError("Cannot read default output") }
var saved: [(AudioObjectPropertyAddress, Float32)] = []
for element: AudioObjectPropertyElement in [0, 1, 2] {
    var property = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyVolumeScalar, mScope: kAudioObjectPropertyScopeOutput, mElement: element)
    var value: Float32 = 0, writable = DarwinBoolean(false), size = UInt32(MemoryLayout<Float32>.size)
    if AudioObjectGetPropertyData(device, &property, 0, nil, &size, &value) == noErr,
       AudioObjectIsPropertySettable(device, &property, &writable) == noErr, writable.boolValue {
        saved.append((property, value)); if element == 0 { break }
    }
}
guard !saved.isEmpty else { fatalError("No writable output volume") }
defer {
    var restored = true
    for (var property, var value) in saved { restored = AudioObjectSetPropertyData(device, &property, 0, nil, UInt32(MemoryLayout<Float32>.size), &value) == noErr && restored }
    for (var property, value) in saved {
        var actual: Float32 = 0, size = UInt32(MemoryLayout<Float32>.size)
        restored = AudioObjectGetPropertyData(device, &property, 0, nil, &size, &actual) == noErr && abs(actual - value) < 0.00001 && restored
    }
    print("restored=\(restored)")
}
for (var property, value) in saved {
    var changed: Float32 = value >= 0.02 ? value - 0.02 : value + 0.02
    guard AudioObjectSetPropertyData(device, &property, 0, nil, UInt32(MemoryLayout<Float32>.size), &changed) == noErr else { throw NSError(domain: "CoreAudioProbe", code: 1) }
    print("device=\(device) element=\(property.mElement) saved=\(value) changed=\(changed)")
}
fflush(stdout)
Thread.sleep(forTimeInterval: 5)
