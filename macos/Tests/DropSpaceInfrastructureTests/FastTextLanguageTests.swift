import Foundation
import Testing
import DropSpaceCore
import DropSpaceInfrastructure

@Test(.enabled(if: ProcessInfo.processInfo.environment["DROPSPACE_LANGUAGE_INTEGRATION"] == "1"))
func actualLanguageWorkerSharesModelAndSeparatesRolesWithoutTruncating() async throws {
    let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    let identifier = FastTextLanguageIdentifier(executable: root.appendingPathComponent("build/language-worker/bin/language-worker"), model: root.appendingPathComponent("build/ValidationModels/lid.176.bin"))
    let english = "The quiet river flows beneath the stars and carries our dreams home."
    let first = try await identifier.predict(english, role: .original)
    #expect(first.diagnostic == nil && first.label == "__label__en" && !first.cacheHit)
    #expect(try await identifier.predict(english, role: .original).cacheHit)
    #expect(try await !identifier.predict(english, role: .providerTranslation).cacheHit)
    let long = try await identifier.predict(String(repeating: english + " ", count: 1024), role: .original)
    #expect(long.label == "__label__en" && long.diagnostic == nil) // Above the former 32 KiB wire limit.
    var document = try LyricsParser.parse("[00:01]静静的河流在星空下面流淌带着我们的梦想回家", provider: .localLrc)
    document = try await identifier.prepare(document, target: "zh")
    #expect(document.translationAdmission?.decision == .originalTarget)
    let stats = await identifier.statistics()
    #expect(stats.modelLoads == 1 && stats.predictions == 4 && stats.cacheEntries == 4 && stats.workerRunning)
    await identifier.stop(clearPredictions: true)
    let stopped = await identifier.statistics()
    #expect(!stopped.workerRunning && stopped.cacheEntries == 0)
}

@Test func languageWorkerCancellationDrainsHungChildAndDoesNotCacheFailure() async throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("DropSpaceLanguage-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let executable = directory.appendingPathComponent("hung-worker"), model = directory.appendingPathComponent("model")
    try Data("#!/usr/bin/python3\nimport os,signal,sys,time\nsignal.signal(signal.SIGTERM, signal.SIG_IGN)\nwith open(sys.argv[2]+'.started','w') as marker: marker.write(str(os.getpid()))\ntime.sleep(30)\n".utf8).write(to: executable)
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)
    try Data().write(to: model)
    let identifier = FastTextLanguageIdentifier(executable: executable, model: model, timeout: 10)
    let started = ContinuousClock.now
    let request = Task { try await identifier.predict("The river carries our dreams home", role: .original) }
    let marker = URL(fileURLWithPath: model.path + ".started")
    let readyDeadline = ContinuousClock.now.advanced(by: .seconds(4))
    while !FileManager.default.fileExists(atPath: marker.path), ContinuousClock.now < readyDeadline { try await Task.sleep(for: .milliseconds(25)) }
    let pid = try #require(Int32(String(contentsOf: marker, encoding: .utf8)))
    request.cancel()
    do { _ = try await request.value; Issue.record("Cancelled language request returned a result") } catch is CancellationError { }
    let stopped = await identifier.statistics()
    #expect(!stopped.workerRunning && stopped.cacheEntries == 0)
    #expect(started.duration(to: .now) < .seconds(7))
    #expect(kill(pid, 0) == -1 && errno == ESRCH)
    let missing = FastTextLanguageIdentifier(executable: directory.appendingPathComponent("absent"), model: model)
    #expect(try await missing.predict("This is an original lyric", role: .original).diagnostic != nil)
    #expect(await missing.statistics().cacheEntries == 0)
    await identifier.stop(); await missing.stop()
}
