import Foundation
import Testing
import DropSpaceCore

@Test func nearbyFixedBaselineRangesAndDisposition() throws {
    struct Fixture: Decodable {
        struct RangeRow: Decodable { let value: String; let total: Int64; let valid: Bool; let start: Int64; let end: Int64 }
        struct Header: Decodable { let name: String; let value: String }
        let baseline: String; let ranges: [RangeRow]; let headers: [Header]
    }
    let url = try #require(Bundle.module.url(forResource: "nearby-baseline", withExtension: "json", subdirectory: "Fixtures"))
    let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url))
    #expect(fixture.baseline == Baseline.commit)
    for row in fixture.ranges {
        let result = NearbySharePolicy.range(row.value, total: row.total)
        #expect((result != nil) == row.valid, "Range \(row.value) / \(row.total)")
        if row.valid { #expect(result?.lowerBound == row.start && result?.upperBound == row.end) }
    }
    for row in fixture.headers { #expect(NearbySharePolicy.disposition(row.name) == row.value) }
}
