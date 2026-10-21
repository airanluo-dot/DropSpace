import XCTest
import CoreAudio
import DropSpaceCore

@MainActor final class NativeActivityUITests: XCTestCase {
    func testActualVolumeIslandAndRestore() throws {
        continueAfterFailure = false
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-UI-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        var settings = AppSettings()
        settings.privacyChoicesCompleted = true; settings.clipboardPaused = true; settings.startAtLogin = false
        settings.autoCheckForUpdates = false; settings.lyrics.enabled = false; settings.islandActivity.enableMediaActivity = false
        settings.systemActivities.showVolumeChanges = true; settings.islandAppearance.resident = true
        try JSONEncoder().encode(settings).write(to: root.appendingPathComponent("settings.json"))
        let app = XCUIApplication(bundleIdentifier: "io.github.airanluo-dot.DropSpace.macOS")
        app.launchEnvironment["DROPSPACE_DATA_DIRECTORY"] = root.path
        app.launch()
        defer { app.terminate() }
        XCTAssertTrue(app.windows["DropSpace"].waitForExistence(timeout: 10))
        // AppKit exposes a borderless nonactivating NSPanel as an AX dialog.
        // Use its stable identity rather than assuming a standard NSWindow role.
        let island = app.dialogs["dropspace-island"]
        XCTAssertTrue(island.waitForExistence(timeout: 3))
        XCTAssertFalse(island.staticTexts["Volume"].exists, "Initial device attachment must not wake a volume activity")
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice, mScope: kAudioObjectPropertyScopeGlobal, mElement: 0)
        var device = AudioObjectID(0), size = UInt32(MemoryLayout<AudioObjectID>.size)
        XCTAssertEqual(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device), noErr)
        var saved: [(AudioObjectPropertyAddress, Float32)] = []
        for element: AudioObjectPropertyElement in [0, 1, 2] {
            var property = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyVolumeScalar, mScope: kAudioObjectPropertyScopeOutput, mElement: element)
            var value: Float32 = 0, writable = DarwinBoolean(false), length = UInt32(MemoryLayout<Float32>.size)
            if AudioObjectGetPropertyData(device, &property, 0, nil, &length, &value) == noErr,
               AudioObjectIsPropertySettable(device, &property, &writable) == noErr, writable.boolValue {
                saved.append((property, value)); if element == 0 { break }
            }
        }
        guard !saved.isEmpty else { throw XCTSkip("The selected output has no writable scalar volume") }
        defer {
            for (var property, var value) in saved { XCTAssertEqual(AudioObjectSetPropertyData(device, &property, 0, nil, UInt32(MemoryLayout<Float32>.size), &value), noErr) }
            for (var property, value) in saved {
                var actual: Float32 = 0, length = UInt32(MemoryLayout<Float32>.size)
                XCTAssertEqual(AudioObjectGetPropertyData(device, &property, 0, nil, &length, &actual), noErr)
                XCTAssertEqual(actual, value, accuracy: 0.00001)
            }
        }
        // Always reach restoration even if a visual assertion fails.
        continueAfterFailure = true
        for (var property, value) in saved {
            var changed: Float32 = value >= 0.02 ? value - 0.02 : value + 0.02
            XCTAssertEqual(AudioObjectSetPropertyData(device, &property, 0, nil, UInt32(MemoryLayout<Float32>.size), &changed), noErr)
        }
        XCTAssertTrue(island.staticTexts["Volume"].waitForExistence(timeout: 1))
        let evidence = XCTAttachment(screenshot: island.screenshot()); evidence.name = "Actual Core Audio volume in native Island"; evidence.lifetime = .keepAlways; add(evidence)
    }
}
