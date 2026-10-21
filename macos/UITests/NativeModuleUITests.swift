import XCTest

@MainActor final class NativeModuleUITests: XCTestCase {
    func testOwnedNativeModulePageSettingsAndIslandContribution() throws {
        continueAfterFailure = false
        let app = XCUIApplication(bundleIdentifier: "io.github.airanluo-dot.DropSpace.macOS")
        guard app.state != .notRunning else { throw XCTSkip("Launch the explicitly prepared ModuleExperienceSmoke profile") }
        app.activate()
        let main = app.windows["DropSpace"], navigation = main.buttons["navigation-module-dropspace.sample-sample"]
        XCTAssertTrue(navigation.waitForExistence(timeout: 5), "The isolated native sample must already be installed and enabled")
        navigation.click()
        let action = main.buttons["ModuleAction-dropspace.sample-show"]
        XCTAssertTrue(action.waitForExistence(timeout: 3))
        let toggle = main.switches["ModuleSetting-dropspace.sample-show-island"]
        if isOn(toggle) == false { toggle.click(); save(main) }
        action.click()
        XCTAssertTrue(main.staticTexts["The sample is ready."].waitForExistence(timeout: 3))
        attach(main, "module-page-action")
        app.typeKey(.space, modifierFlags: [.command, .shift])
        let island = app.dialogs["dropspace-island"]
        XCTAssertTrue(island.waitForExistence(timeout: 3))
        // Manual page choice persists across reopen; reach Widgets through the
        // actual available rails instead of assuming every open resets to Files.
        for _ in 0..<3 {
            let previous = island.buttons["island-previous-page"]
            if !previous.exists { break }; previous.click()
            RunLoop.current.run(until: Date().addingTimeInterval(0.4))
        }
        let chip = island.buttons["Module sample: This message expires after 30 seconds."]
        XCTAssertTrue(chip.waitForExistence(timeout: 3))
        attach(island, "module-widgets-chip")
        island.buttons["island-collapse"].click()
        XCTAssertTrue(island.buttons["island-compact"].waitForExistence(timeout: 3))
        attach(island, "module-resident-compact")
        navigation.click()
        XCTAssertTrue(toggle.exists)
        toggle.click(); save(main)
        // Leave and re-enter to read durable settings through the normal view lifecycle.
        main.buttons["Close"].click(); navigation.click()
        XCTAssertEqual(isOn(toggle), false)
        toggle.click(); save(main)
        main.buttons["Close"].click(); navigation.click()
        XCTAssertEqual(isOn(toggle), true)
        attach(main, "module-settings-restored")
        main.buttons["Close"].click()
        XCTAssertTrue(main.buttons["workspace-add"].exists || main.menuButtons["workspace-add"].exists)
    }
    private func isOn(_ element: XCUIElement) -> Bool? {
        if let number = element.value as? NSNumber { return number.boolValue }
        if let value = element.value as? String { if ["1", "on", "true"].contains(value) { return true }; if ["0", "off", "false"].contains(value) { return false } }
        return nil
    }
    private func save(_ main: XCUIElement) {
        let button = main.buttons["ModuleSave-dropspace.sample"]; button.click()
        let ready = XCTNSPredicateExpectation(predicate: NSPredicate(format: "enabled == true"), object: button)
        XCTAssertEqual(XCTWaiter.wait(for: [ready], timeout: 3), .completed)
    }
    private func attach(_ element: XCUIElement, _ name: String) {
        let screenshot = XCTAttachment(screenshot: element.screenshot()); screenshot.name = name; screenshot.lifetime = .keepAlways; add(screenshot)
        let tree = XCTAttachment(string: element.debugDescription); tree.name = name + "-accessibility"; tree.lifetime = .keepAlways; add(tree)
    }
}
