import XCTest
import CoreGraphics
import DropSpaceCore

@MainActor final class NativeMediaUITests: XCTestCase {
    func testOwnPausedMusicPointerSeekAndRestore() throws {
        continueAfterFailure = false
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpace-Media-UI-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        var settings = AppSettings()
        settings.privacyChoicesCompleted = true; settings.clipboardPaused = true; settings.startAtLogin = false
        settings.autoCheckForUpdates = false; settings.lyrics.enabled = false
        settings.islandActivity.enableMediaActivity = true; settings.islandActivity.showArtwork = true
        settings.islandActivity.useMediaSourceAllowList = true; settings.islandActivity.allowedMediaSourceAppIDs = ["com.apple.Music"]
        settings.language = "en-US"
        try JSONEncoder().encode(settings).write(to: root.appendingPathComponent("settings.json"))
        let app = XCUIApplication(bundleIdentifier: "io.github.airanluo-dot.DropSpace.macOS")
        app.launchEnvironment["DROPSPACE_DATA_DIRECTORY"] = root.path
        app.launch(); defer { app.terminate() }
        try exercisePointerSeek(app)
    }
    func testAlreadyRunningOwnPausedMusicPointerSeekAndRestore() throws {
        continueAfterFailure = false
        let app = XCUIApplication(bundleIdentifier: "io.github.airanluo-dot.DropSpace.macOS")
        guard app.state != .notRunning else { throw XCTSkip("Launch the isolated MediaSmoke application through its normal authorized path first") }
        app.activate()
        try exercisePointerSeek(app)
    }
    func testPublicPointerEventsOnOwnPausedMusicAndRestore() throws {
        continueAfterFailure = false
        guard CGPreflightPostEventAccess() else { throw XCTSkip("This test process has no existing permission to post pointer events") }
        let app = XCUIApplication(bundleIdentifier: "io.github.airanluo-dot.DropSpace.macOS")
        guard app.state != .notRunning else { throw XCTSkip("The isolated authorized MediaSmoke instance must already be running") }
        app.activate()
        try exercisePointerSeek(app, publicPointerEvents: true)
    }
    private func exercisePointerSeek(_ app: XCUIApplication, publicPointerEvents: Bool = false) throws {
        let window = app.windows["DropSpace"]
        XCTAssertTrue(window.waitForExistence(timeout: 10))
        window.staticTexts["Music"].firstMatch.click()
        // Only the existing, deliberately silent validation track may be moved.
        XCTAssertTrue(window.staticTexts["DropSpace Validation"].waitForExistence(timeout: 10))
        let play = window.buttons["media-playpause"]
        XCTAssertEqual(play.label, "Play", "The owned silent fixture must remain paused")
        let slider = window.sliders["playback-position"]
        XCTAssertTrue(slider.exists)
        func position() throws -> Double {
            Double(slider.normalizedSliderPosition) * 60
        }
        let saved = try position()
        XCTAssertEqual(saved, 10, accuracy: 1, "Unexpected current fixture position; do not alter another playback session")
        continueAfterFailure = true
        defer {
            slider.adjust(toNormalizedSliderPosition: saved / 60)
            RunLoop.current.run(until: Date().addingTimeInterval(2.5))
            XCTAssertEqual(try position(), saved, accuracy: 0.5)
            XCTAssertEqual(play.label, "Play")
        }
        let start = slider.coordinate(withNormalizedOffset: CGVector(dx: saved / 60, dy: 0.5))
        let end = slider.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        if publicPointerEvents {
            let frame = slider.frame, from = CGPoint(x: frame.minX + 9 + (frame.width - 18) * saved / 60, y: frame.midY)
            let to = CGPoint(x: frame.midX, y: frame.midY)
            let source = try XCTUnwrap(CGEventSource(stateID: .hidSystemState))
            func post(_ type: CGEventType, _ point: CGPoint) throws {
                let event = try XCTUnwrap(CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: .left))
                event.post(tap: .cghidEventTap)
            }
            try post(.mouseMoved, from); usleep(100_000); try post(.leftMouseDown, from)
            defer { try? post(.leftMouseUp, to) }
            for step in 1...30 {
                usleep(30_000)
                try post(.leftMouseDragged, CGPoint(x: from.x + (to.x - from.x) * Double(step) / 30, y: from.y))
            }
            try post(.leftMouseUp, to)
        } else { start.press(forDuration: 0.2, thenDragTo: end, withVelocity: .slow, thenHoldForDuration: 0.5) }
        // Observe more than two production polling intervals; the remote player
        // position must persist after the local NSSlider tracking value expires.
        RunLoop.current.run(until: Date().addingTimeInterval(3))
        XCTAssertEqual(try position(), 30, accuracy: 2)
        XCTAssertEqual(play.label, "Play")
        XCTAssertFalse(window.otherElements["statusError"].exists)
        let evidence = XCTAttachment(screenshot: window.screenshot()); evidence.name = "Native Music pointer seek, paused owned fixture"; evidence.lifetime = .keepAlways; add(evidence)
    }
}
