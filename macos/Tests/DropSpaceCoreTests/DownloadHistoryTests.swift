import Foundation
import Testing
@testable import DropSpaceCore

@Test func nativeDownloadHistoryPaginationNeverHidesUnfinishedTasks() {
    let source = URL(string: "https://example.invalid/file")!, destination = URL(fileURLWithPath: "/tmp/file")
    var records = (0..<130).map { index in
        var record = DownloadRecord(source: source, destination: destination)
        record.phase = index.isMultiple(of: 2) ? .completed : .cancelled
        record.created = Date(timeIntervalSince1970: Double(index)); return record
    }
    let phases: [DownloadPhase] = [.queued, .connecting, .downloading, .verifying, .paused, .failed]
    for (index, phase) in phases.enumerated() {
        var record = DownloadRecord(source: source, destination: destination)
        record.created = Date(timeIntervalSince1970: Double(index - 10)); record.phase = phase; records.append(record)
    }
    let first = DownloadHistory.visible(records.reversed(), limit: 50)
    #expect(first.count == 56 && first.prefix(50).map(\.id) == Array(records[80..<130].reversed()).map(\.id))
    #expect(Set(first.suffix(6).map(\.id)) == Set(records.suffix(6).map(\.id)))
    #expect(DownloadHistory.visible(records, limit: 100).count == 106)
    #expect(DownloadHistory.visible(records, limit: 0).count == 6)
}
