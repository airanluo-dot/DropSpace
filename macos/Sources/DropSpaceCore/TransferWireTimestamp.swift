import Foundation

/// System.Text.Json's extended ISO 8601 DateTimeOffset subset. Foundation's
/// ISO8601 formatter accepts invalid calendar dates and trailing input, while
/// refusing legal date-only/local-time inputs. Keep wire validation explicit.
enum TransferWireTimestamp {
    static func parse(_ text: String) -> Date? {
        guard text.utf8.count <= 42, let expression = try? NSRegularExpression(pattern: #"\A([0-9]{4})-([0-9]{2})-([0-9]{2})(?:T([0-9]{2}):([0-9]{2})(?::([0-9]{2})(?:\.([0-9]{1,16}))?)?(Z|[+-][0-9]{2}:[0-9]{2})?)?\z"#),
              let match = expression.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { return nil }
        func field(_ index: Int) -> String? { Range(match.range(at: index), in: text).map { String(text[$0]) } }
        guard let year = Int(field(1) ?? ""), let month = Int(field(2) ?? ""), let day = Int(field(3) ?? ""), (1...9999).contains(year), (1...12).contains(month) else { return nil }
        let leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)
        let monthDays = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
        let hour = Int(field(4) ?? "0")!, minute = Int(field(5) ?? "0")!, second = Int(field(6) ?? "0")!
        guard (1...monthDays[month - 1]).contains(day), hour < 24, minute < 60, second < 60 else { return nil }
        let localSeconds = days(year, month, day) * 86400 + Int64(hour * 3600 + minute * 60 + second)
        let offset: Int
        if let zone = field(8) {
            if zone == "Z" { offset = 0 }
            else {
                let parts = zone.dropFirst().split(separator: ":"), hours = Int(parts[0])!, minutes = Int(parts[1])!
                guard hours <= 14, minutes < 60, hours != 14 || minutes == 0 else { return nil }
                offset = (hours * 3600 + minutes * 60) * (zone.hasPrefix("-") ? -1 : 1)
            }
        } else {
            // DateTimeOffset treats an absent offset as the local time zone.
            // Calendar resolves local DST rules; it never changes the validated
            // Gregorian fields or permits an out-of-range wire date.
            var calendar = Calendar(identifier: .gregorian); calendar.timeZone = .current
            guard let local = calendar.date(from: DateComponents(year: year, month: month, day: day, hour: hour, minute: minute, second: second)) else { return nil }
            offset = calendar.timeZone.secondsFromGMT(for: local)
        }
        let seconds = localSeconds - Int64(offset)
        guard seconds >= days(1, 1, 1) * 86400, seconds < days(10000, 1, 1) * 86400 else { return nil }
        // .NET accepts up to16 fraction digits and truncates after7. Keep the
        // original manifest string; Date is only the validation projection.
        let fraction = Double("0." + String((field(7) ?? "0").prefix(7))) ?? 0
        return Date(timeIntervalSince1970: Double(seconds) + fraction)
    }
    private static func days(_ year: Int, _ month: Int, _ day: Int) -> Int64 {
        let y = year - (month <= 2 ? 1 : 0), era = y / 400, yearOfEra = y - era * 400
        let dayOfYear = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1
        return Int64(era * 146097 + yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear - 719468)
    }
}
