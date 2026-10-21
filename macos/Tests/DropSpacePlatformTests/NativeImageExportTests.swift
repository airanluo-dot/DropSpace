import Foundation
import Testing
import DropSpaceCore
@testable import DropSpacePlatform

@MainActor @Test func imageExportUsesFrozenDefaultsAndImmutableParameters() async throws {
    let captured = ImageExportCapture()
    let owner = NativeImageExport(readSize: { _ in (400, 200) }, export: {
        await captured.append($0)
        return $0.directory.appendingPathComponent("result.jpg")
    })
    let item = DropItem(source: .space, kind: .file, title: "source.bmp", file: .init(path: "/source.bmp"))
    #expect(owner.begin(item: item, action: .resizeImage, directory: URL(fileURLWithPath: "/default")))
    try await imageExportIdle(owner)
    #expect(owner.format == "bmp" && owner.width == "400" && owner.height == "200")
    owner.selectPreset(50)
    #expect(owner.width == "200" && owner.height == "100" && owner.keepAspect)
    owner.selectPreset(0); owner.width = ""; owner.height = ""
    owner.apply()
    #expect(owner.errorKey == "ActionParametersRequired")
    owner.dismiss()
    #expect(owner.begin(item: item, action: .convertImage, directory: URL(fileURLWithPath: "/default")))
    try await imageExportIdle(owner)
    #expect(owner.format == "jpeg" && owner.preset == 100)
    owner.selectPreset(0); owner.width = "100"; owner.height = ""
    owner.apply()
    #expect(owner.errorKey == "ActionParametersRequired")
    owner.width = ""; owner.height = ""
    owner.destination = URL(fileURLWithPath: "/chosen")
    owner.apply()
    // Edits cannot change the submitted request while its operation is queued.
    owner.format = "png"; owner.destination = URL(fileURLWithPath: "/later")
    try await imageExportIdle(owner)
    let values = await captured.values
    #expect(values.count == 1)
    #expect(values.first?.item.id == item.id)
    #expect(values.first?.format == "jpeg" && values.first?.width == nil && values.first?.height == nil)
    #expect(values.first?.directory.path == "/chosen")
    #expect(owner.request == nil)
    await owner.shutdown()
}

@MainActor @Test func imageExportCancellationRetainsSlotAndRejectsLatePublication() async throws {
    let gate = ImageExportGate()
    let owner = NativeImageExport(readSize: { _ in (400, 200) }, export: { _ in await gate.wait() })
    var outputs: [URL] = []
    owner.onOutput = { outputs.append($0) }
    let item = DropItem(source: .space, kind: .file, title: "source.png", file: .init(path: "/source.png"))
    #expect(owner.begin(item: item, action: .convertImage, directory: URL(fileURLWithPath: "/default")))
    try await imageExportIdle(owner)
    owner.apply()
    let deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while !(await gate.entered), ContinuousClock.now < deadline { await Task.yield() }
    #expect(await gate.entered)
    owner.dismiss()
    #expect(owner.retiring && owner.request == nil)
    #expect(!owner.begin(item: item, action: .resizeImage, directory: URL(fileURLWithPath: "/other")))
    await gate.release()
    try await imageExportIdle(owner)
    #expect(outputs.isEmpty && !owner.retiring)
    await owner.shutdown()
    #expect(!owner.begin(item: item, action: .convertImage, directory: URL(fileURLWithPath: "/closed")))
}

@MainActor private func imageExportIdle(_ owner: NativeImageExport) async throws {
    let deadline = ContinuousClock.now.advanced(by: .seconds(2))
    while owner.busy, ContinuousClock.now < deadline { await Task.yield() }
    #expect(!owner.busy)
}

private actor ImageExportCapture {
    var values: [NativeImageExport.Parameters] = []
    func append(_ value: NativeImageExport.Parameters) { values.append(value) }
}

private actor ImageExportGate {
    var entered = false
    private var continuation: CheckedContinuation<URL, Never>?
    func wait() async -> URL {
        entered = true
        return await withCheckedContinuation { continuation = $0 }
    }
    func release() {
        continuation?.resume(returning: URL(fileURLWithPath: "/late.png"))
        continuation = nil
    }
}
