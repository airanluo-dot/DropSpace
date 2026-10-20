import Foundation
import Darwin
import DropSpaceCore

public struct UpdateInstallPlan: Codable, Sendable {
    public let id: UUID
    public let application: URL
    public let candidate: URL
    public let backup: URL
    public let receipt: URL
    public let parentPID: Int32
    public let manifest: MacUpdateManifest
    public let dataDirectory: URL
    public var phase: String
    public var message: String?

    public func save(to url: URL) throws {
        try JSONEncoder().encode(self).write(to: url, options: .atomic)
    }
    public func validateLocations() throws {
        let parent = application.deletingLastPathComponent()
        let expectedRoot = dataDirectory.appendingPathComponent("Updates")
        guard application.isFileURL, application.pathExtension == "app",
              parent.path == parent.resolvingSymlinksInPath().path,
              candidate.isFileURL, candidate.path == parent.appendingPathComponent(".DropSpace-candidate-\(id.uuidString).app").path,
              backup.isFileURL, backup.path == parent.appendingPathComponent(".DropSpace-backup-\(id.uuidString).app").path,
              receipt.isFileURL, receipt.path == expectedRoot.appendingPathComponent("\(id.uuidString).receipt").path,
              dataDirectory.isFileURL, parentPID > 1 else {
            throw DropSpaceError.invalid("Invalid update installation locations")
        }
    }
}

public actor MacUpdateStore {
    public let directory: URL
    private let downloader: HTTPDownloader
    private let dataDirectory: URL
    public init(dataDirectory: URL, budget: DownloadBudget) {
        self.dataDirectory = dataDirectory
        directory = dataDirectory.appendingPathComponent("Updates")
        downloader = HTTPDownloader(budget: budget)
    }

    public func check(channel: UpdateChannel) async throws -> MacUpdateManifest? {
        try createDirectory()
        let url = MacUpdateOrigin.feed(channel)
        let file = directory.appendingPathComponent("feed-\(UUID().uuidString).json")
        defer { try? DownloadStaging.clean(file) }
        try await downloader.download(source: url, staging: file, maximumBytes: 131072, trusted: { $0 == url })
        let feed = try JSONDecoder().decode(MacUpdateFeed.self, from: Data(contentsOf: file))
        try feed.validate(channel: channel)
        try feed.release?.validate(channel: channel, os: ProcessInfo.processInfo.operatingSystemVersion)
        return feed.release
    }

    public func download(_ manifest: MacUpdateManifest, progress: @escaping @Sendable (Int64, Int64?) async -> Void) async throws -> URL {
        try manifest.validate(channel: .beta, os: ProcessInfo.processInfo.operatingSystemVersion)
        try createDirectory()
        let archive = directory.appendingPathComponent(manifest.tag + ".zip")
        try await downloader.download(source: manifest.archive.url, staging: archive,
                                      expectedBytes: manifest.archive.size, sha256: manifest.archive.sha256,
                                      maximumBytes: 2 * 1024 * 1024 * 1024,
                                      trusted: MacUpdateOrigin.allowsAssetRedirect, progress: progress)
        return archive
    }

    public func prepare(archive: URL, manifest: MacUpdateManifest, application: URL) async throws -> (UpdateInstallPlan, URL) {
        try createDirectory()
        try manifest.validate(channel: .beta, os: ProcessInfo.processInfo.operatingSystemVersion)
        try AppBundleIntegrity.verify(application)
        let parent = application.deletingLastPathComponent()
        guard parent.path == parent.resolvingSymlinksInPath().path,
              try parent.resourceValues(forKeys: [.volumeIsReadOnlyKey]).volumeIsReadOnly != true,
              FileManager.default.isWritableFile(atPath: parent.path),
              FileManager.default.isWritableFile(atPath: application.path) else {
            throw DropSpaceError.unavailable("This installation cannot be replaced. Download the DMG and copy DropSpace to a writable Applications folder, then reopen it there.")
        }
        let id = UUID()
        let extraction = directory.appendingPathComponent("extract-\(id.uuidString)")
        let candidate = parent.appendingPathComponent(".DropSpace-candidate-\(id.uuidString).app")
        var preserveCandidate = false
        defer {
            try? FileManager.default.removeItem(at: extraction)
            if !preserveCandidate { try? FileManager.default.removeItem(at: candidate) }
        }
        let digest = try await Task.detached(priority: .utility) { try Digests.sha256(file: archive) }.value
        guard digest == manifest.archive.sha256,
              Int64(try archive.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? -1) == manifest.archive.size else {
            throw DropSpaceError.invalid("Update archive changed after download")
        }
        try await Task.detached(priority: .utility) { try UpdateArchive.inspect(archive) }.value
        try FileManager.default.createDirectory(at: extraction, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        try await ProcessRunner.run(executable: URL(fileURLWithPath: "/usr/bin/ditto"), arguments: ["-x", "-k", archive.path, extraction.path], timeout: 120)
        let extracted = extraction.appendingPathComponent("DropSpace.app")
        try AppBundleIntegrity.verify(extracted, manifest: manifest)
        try Task.checkCancellation()
        try FileManager.default.copyItem(at: extracted, to: candidate)
        try AppBundleIntegrity.verify(candidate, manifest: manifest)
        let plan = UpdateInstallPlan(id: id, application: application, candidate: candidate,
                                     backup: parent.appendingPathComponent(".DropSpace-backup-\(id.uuidString).app"),
                                     receipt: directory.appendingPathComponent("\(id.uuidString).receipt"),
                                     parentPID: getpid(), manifest: manifest, dataDirectory: dataDirectory,
                                     phase: "prepared")
        try plan.validateLocations()
        let planURL = directory.appendingPathComponent("\(id.uuidString).plan.json")
        try plan.save(to: planURL)
        preserveCandidate = true
        return (plan, planURL)
    }

    public func history() throws -> [UpdateInstallPlan] {
        guard FileManager.default.fileExists(atPath: directory.path) else { return [] }
        return try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.fileSizeKey])
            .filter { $0.lastPathComponent.hasSuffix(".plan.json") }
            .prefix(100).compactMap { url in
                guard let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size < 131072 else { return nil }
                guard var plan = try? JSONDecoder().decode(UpdateInstallPlan.self, from: Data(contentsOf: url)),
                      (try? plan.validateLocations()) != nil else { return nil }
                if ["switching", "awaitingLaunch", "completedPendingCleanup"].contains(plan.phase),
                   (try? Data(contentsOf: plan.receipt)) == Data(plan.id.uuidString.utf8),
                   (try? AppBundleIntegrity.verify(plan.application, manifest: plan.manifest)) != nil {
                    let previous = FileManager.default.fileExists(atPath: plan.backup.path) ? plan.backup : plan.candidate
                    plan.phase = "completedPendingCleanup"
                    plan.message = "Startup was confirmed. Previous application retained at \(previous.path)."
                    try? plan.save(to: url)
                }
                return plan
            }
    }

    private func createDirectory() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
    }
}
