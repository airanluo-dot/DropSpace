import XCTest

@MainActor final class NativeIslandUITests: XCTestCase {
    func testOwnedIslandPageRailsGeometryAndSettings() throws {
        continueAfterFailure = false
        let app = XCUIApplication(bundleIdentifier: "io.github.airanluo-dot.DropSpace.macOS")
        guard app.state != .notRunning else { throw XCTSkip("Launch the explicit IslandExperienceSmoke profile before this acceptance check") }
        attachText(app.debugDescription, name: "initial-accessibility")
        app.activate()
        let main = app.windows["DropSpace"]
        XCTAssertTrue(main.exists && main.debugDescription.contains("reference.txt"), "Only operate on the owned validation reference")
        attachScreenshot(main, name: "workspace-reference-layout")
        app.typeKey(.space, modifierFlags: [.command, .shift])
        let island = app.dialogs["dropspace-island"]
        XCTAssertTrue(island.waitForExistence(timeout: 5))
        attachText(app.debugDescription, name: "opened-accessibility")
        XCTAssertTrue(island.buttons["island-next-page"].waitForExistence(timeout: 5))
        RunLoop.current.run(until: Date().addingTimeInterval(0.8))
        XCTAssertEqual(island.frame.width, 560, accuracy: 1)
        XCTAssertEqual(island.frame.height, 340, accuracy: 1)
        attachScreenshot(island, name: "files-expanded")
        island.buttons["island-next-page"].click()
        XCTAssertTrue(island.staticTexts["Clipboard"].waitForExistence(timeout: 3))
        XCTAssertFalse(island.buttons["island-next-page"].exists)
        attachScreenshot(island, name: "clipboard")
        for _ in 0..<3 { island.buttons["island-previous-page"].click(); RunLoop.current.run(until: Date().addingTimeInterval(0.4)) }
        XCTAssertFalse(island.buttons["island-previous-page"].exists)
        attachScreenshot(island, name: "widgets")
        island.buttons["island-next-page"].click()
        XCTAssertTrue(island.staticTexts["Play music in a supported player to see it here."].waitForExistence(timeout: 3))
        attachScreenshot(island, name: "music-empty")
        island.buttons["island-next-page"].click()
        island.buttons["island-collapse"].click()
        XCTAssertTrue(island.buttons["island-compact"].waitForExistence(timeout: 3))
        RunLoop.current.run(until: Date().addingTimeInterval(0.8))
        XCTAssertEqual(island.frame.width, 340, accuracy: 1)
        XCTAssertEqual(island.frame.height, 64, accuracy: 1)
        attachScreenshot(island, name: "single-file-compact")
        island.buttons["island-compact"].click()
        XCTAssertTrue(island.buttons["island-collapse"].waitForExistence(timeout: 3))
        island.buttons["island-collapse"].click()
        RunLoop.current.run(until: Date().addingTimeInterval(0.8))
        // Test the actual built-in navigation entry here. Keyboard commands have
        // their own check; failed nonactivating-panel typing is not a navigation pass.
        main.buttons["navigation-settings"].click()
        let settings = app.windows["DropSpace Settings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 5))
        settings.buttons["settings-tab-Island"].click()
        attachText(settings.debugDescription, name: "island-settings-accessibility")
        attachScreenshot(settings, name: "island-settings")
        XCTAssertTrue(settings.descendants(matching: .any)["IslandResident"].exists)
    }
    func testOwnedWindowSettingsKeyboardRoute() throws {
        continueAfterFailure = false
        let app = XCUIApplication(bundleIdentifier: "io.github.airanluo-dot.DropSpace.macOS")
        guard app.state != .notRunning else { throw XCTSkip("Requires the owned IslandExperienceSmoke app") }
        let main = app.windows["DropSpace"]
        XCTAssertTrue(main.debugDescription.contains("reference.txt"))
        let existing = app.windows["DropSpace Settings"]
        if existing.exists { existing.buttons[XCUIIdentifierCloseWindow].click() }
        XCTAssertFalse(existing.exists)
        app.activate(); main.buttons["navigation-space"].click()
        // Clicking the window itself can hit macOS's screen-sharing titlebar
        // control. Give a real app text control focus before sending the command.
        main.textFields["workspace-search"].click()
        attachText(app.debugDescription, name: "before-window-settings-keyboard")
        main.typeKey(",", modifierFlags: .command)
        XCTAssertTrue(app.windows["DropSpace Settings"].waitForExistence(timeout: 5))
        attachText(app.debugDescription, name: "after-window-settings-keyboard")
    }
    func testOwnedWindowKeyboardDeliveryProbe() throws {
        continueAfterFailure = false
        let app = XCUIApplication(bundleIdentifier: "io.github.airanluo-dot.DropSpace.macOS")
        guard app.state != .notRunning else { throw XCTSkip("Requires owned IslandExperienceSmoke") }
        let main = app.windows["DropSpace"]
        XCTAssertTrue(main.debugDescription.contains("reference.txt"))
        app.activate(); main.buttons["navigation-space"].click()
        let search = main.textFields["workspace-search"]; search.click()
        main.typeText("keyboard-probe")
        attachText(app.debugDescription, name: "keyboard-plain-input")
        XCTAssertEqual(search.value as? String, "keyboard-probe")
        main.typeKey("a", modifierFlags: .command); main.typeKey(.delete, modifierFlags: [])
        XCTAssertEqual(search.value as? String, "")
        main.typeKey("s", modifierFlags: [.command, .shift])
        XCTAssertTrue(app.windows["DropSpace Settings"].waitForExistence(timeout: 5))
    }
    private func attachText(_ text: String, name: String) {
        let value = XCTAttachment(string: text); value.name = name; value.lifetime = .keepAlways; add(value)
    }
    private func attachScreenshot(_ element: XCUIElement, name: String) {
        let value = XCTAttachment(screenshot: element.screenshot()); value.name = name; value.lifetime = .keepAlways; add(value)
    }

}
