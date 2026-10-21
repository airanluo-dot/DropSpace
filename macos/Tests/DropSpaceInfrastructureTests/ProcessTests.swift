import Testing
import Foundation
import Darwin
@testable import DropSpaceInfrastructure

@Test func cancelledNativeProcessIsConfirmedExited() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
    defer { try? FileManager.default.removeItem(at:root) }
    let pidFile = root.appendingPathComponent("pid")
    let work = Task { try await ProcessRunner.run(executable:URL(fileURLWithPath:"/bin/sh"),arguments:["-c","echo $$ > \"$1\"; trap '' TERM; exec /bin/sleep 30","dropspace-test",pidFile.path],timeout:10) }
    for _ in 0..<100 { if FileManager.default.fileExists(atPath:pidFile.path) { break }; try await Task.sleep(for:.milliseconds(20)) }
    let pid = Int32(try String(contentsOf:pidFile,encoding:.utf8).trimmingCharacters(in:.whitespacesAndNewlines))!
    work.cancel()
    do { try await work.value; Issue.record("Cancelled worker succeeded") } catch is CancellationError { }
    #expect(kill(pid,0) == -1 && errno == ESRCH)
}
