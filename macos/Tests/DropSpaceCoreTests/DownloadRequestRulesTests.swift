import Foundation
import Testing
@testable import DropSpaceCore

@Test func nativeDownloadRequestUsesFrozenSanitizationAndRejectsManualMutation() throws {
    let root = URL(fileURLWithPath: "/tmp/DropSpace-download-rules")
    let source = URL(string: "https://example.invalid/a%2Fb%3F.txt?token=secret")!
    #expect(try DownloadRequestRules.validate(source: source, directory: root, name: nil) == "a_b_.txt")
    #expect(try DownloadRequestRules.validate(source: URL(string: "https://example.invalid/")!, directory: root, name: "  ") == "download.bin")
    #expect(try DownloadRequestRules.validate(source: URL(string: "https://example.invalid/folder/")!, directory: root, name: nil) == "download.bin")
    #expect(try DownloadRequestRules.validate(source: source, directory: root, name: "  report.txt  ") == "report.txt")
    for invalid in [".", "..", "CON", "nul.txt", "COM¹.txt", "LPT9.csv", "path/file", "path\\file", "report.", "bad?name", "bad\0name", String(repeating: "a", count: 181)] {
        #expect(throws: (any Error).self) { try DownloadRequestRules.validate(source: source, directory: root, name: invalid) }
    }
    #expect(DownloadRequestRules.sanitize(" CON.txt. ") == "_CON.txt")
    #expect(DownloadRequestRules.sanitize("\u{7F}file\u{85}") == "_file")
    #expect(DownloadRequestRules.sanitize(String(repeating: "a", count: 179) + "😀").utf16.count == 179)
    #expect(throws: (any Error).self) { try DownloadRequestRules.validate(source: URL(string: "https://user:secret@example.invalid/file")!, directory: root, name: nil) }
    #expect(throws: (any Error).self) { try DownloadRequestRules.validate(source: URL(string: "https://example.invalid/" + String(repeating: "a", count: 16384))!, directory: root, name: nil) }
    #expect(throws: (any Error).self) { try DownloadRequestRules.validate(source: source, directory: URL(string: "https://example.invalid/output")!, name: nil) }
}
