import Foundation
import CoreAudio
import Testing
import DropSpaceCore
import DropSpacePlatform

@Test @MainActor func nativeVolumeAttachmentDoesNotWakeAndDisableRetiresOwnership() async throws {
    let activities = NativeSystemActivities()
    var settings = SystemActivitySettings(); settings.showVolumeChanges = true
    await activities.configure(settings)
    // Initial device attachment is state, never a fabricated transient.
    #expect(activities.current == nil)
    #expect(activities.volume.current != nil || activities.volume.unavailableReason != nil)
    settings.showVolumeChanges = false
    await activities.configure(settings)
    #expect(activities.volume.current == nil)
    #expect(activities.current == nil)
    activities.stop()
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_VOLUME_INTEGRATION"] == "1"))
@MainActor func actualDefaultOutputChangeIsObservedAndRestored() async throws {
    var device = AudioObjectID(0), size = UInt32(MemoryLayout<AudioObjectID>.size)
    var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    #expect(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device) == noErr)
    var properties: [(AudioObjectPropertyAddress, Float32)] = []
    for element: UInt32 in [kAudioObjectPropertyElementMain, 1, 2] {
        var property = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyVolumeScalar, mScope: kAudioObjectPropertyScopeOutput, mElement: element)
        var value: Float32 = 0; var scalarSize = UInt32(MemoryLayout<Float32>.size); var writable = DarwinBoolean(false)
        if AudioObjectGetPropertyData(device, &property, 0, nil, &scalarSize, &value) == noErr,
           AudioObjectIsPropertySettable(device, &property, &writable) == noErr, writable.boolValue {
            properties.append((property, value))
            if element == kAudioObjectPropertyElementMain { break }
        }
    }
    try #require(!properties.isEmpty, "Current device exposes no writable volume controls")
    let observer = NativeVolumeObserver()
    var observed: [VolumeSnapshot] = []
    observer.onChange = { observed.append($0) }; observer.setEnabled(true)
    defer {
        for (var address, var value) in properties { _ = AudioObjectSetPropertyData(device, &address, 0, nil, UInt32(MemoryLayout<Float32>.size), &value) }
        observer.stop()
    }
    var expected: [Float32] = []
    for (var property, value) in properties {
        var changed: Float32 = value >= 0.02 ? value - 0.02 : value + 0.02
        try #require(AudioObjectSetPropertyData(device, &property, 0, nil, UInt32(MemoryLayout<Float32>.size), &changed) == noErr)
        expected.append(changed)
    }
    let percent = Int((Double(expected.reduce(0, +) / Float32(expected.count)) * 100).rounded(.toNearestOrEven))
    let deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while observer.current?.percent != percent && ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(20)) }
    #expect(!observed.isEmpty)
    #expect(observer.current?.percent == percent)
}
