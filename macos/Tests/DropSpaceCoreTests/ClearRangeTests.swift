import Foundation
import Testing
import DropSpaceCore

@Test func clipboardClearUsesLocalMidnightAcrossDaylightSaving() throws {
    var calendar = Calendar(identifier:.gregorian); calendar.timeZone = try #require(TimeZone(identifier:"America/Los_Angeles"))
    let now = try #require(ISO8601DateFormatter().date(from:"2026-03-08T12:00:00Z"))
    let midnight = try #require(ISO8601DateFormatter().date(from:"2026-03-08T08:00:00Z"))
    #expect(ClipboardClearRange.today.from(now:now,calendar:calendar) == midnight)
    #expect(ClipboardClearRange.lastHour.from(now:now,calendar:calendar) == now.addingTimeInterval(-3600))
    #expect(ClipboardClearRange.all.from(now:now) == nil)
}
