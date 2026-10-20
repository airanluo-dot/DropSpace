import AppKit
import Foundation
import Darwin
import DropSpaceCore
import DropSpaceInfrastructure

@main struct UpdateHelper {
    @MainActor static func main() async {
        guard CommandLine.arguments.count == 2 else { exit(64) }
        let planURL = URL(fileURLWithPath: CommandLine.arguments[1])
        var plan: UpdateInstallPlan?
        do {
            guard try planURL.resourceValues(forKeys: [.fileSizeKey, .isSymbolicLinkKey]).isSymbolicLink != true,
                  try planURL.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max < 131072 else {
                throw DropSpaceError.invalid("Invalid installation plan")
            }
            var loaded = try JSONDecoder().decode(UpdateInstallPlan.self, from: Data(contentsOf: planURL))
            try loaded.validateLocations()
            guard planURL == loaded.dataDirectory.appendingPathComponent("Updates/\(loaded.id.uuidString).plan.json"),
                  loaded.phase == "prepared" else { throw DropSpaceError.invalid("Installation plan already used") }
            try loaded.manifest.validate(channel: .beta, os: ProcessInfo.processInfo.operatingSystemVersion)
            try AppBundleIntegrity.verify(loaded.application)
            try AppBundleIntegrity.verify(loaded.candidate, manifest: loaded.manifest)
            loaded.phase = "waitingForExit"; try loaded.save(to: planURL); plan = loaded
            let deadline = ContinuousClock.now.advanced(by: .seconds(60))
            while kill(loaded.parentPID, 0) == 0 || errno == EPERM {
                guard ContinuousClock.now < deadline else { throw DropSpaceError.unavailable("The application did not exit; installation was not changed") }
                try await Task.sleep(for: .milliseconds(100))
            }
            try AppBundleIntegrity.verify(loaded.application)
            try AppBundleIntegrity.verify(loaded.candidate, manifest: loaded.manifest)
            loaded.phase = "switching"; try loaded.save(to: planURL); plan = loaded
            // One filesystem operation swaps two complete signed bundles on the same volume.
            guard renamex_np(loaded.candidate.path, loaded.application.path, UInt32(RENAME_SWAP)) == 0 else {
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
            loaded.phase = "awaitingLaunch"; plan = loaded
            do { try loaded.save(to: planURL) } catch {
                if renamex_np(loaded.candidate.path, loaded.application.path, UInt32(RENAME_SWAP)) != 0 {
                    throw DropSpaceError.storage("Cannot persist installation state or restore previous application; previous bundle remains at \(loaded.candidate.path)")
                }
                throw error
            }
            do {
                let configuration = NSWorkspace.OpenConfiguration()
                configuration.createsNewApplicationInstance = true
                configuration.environment = ["DROPSPACE_DATA_DIRECTORY": loaded.dataDirectory.path,
                    "DROPSPACE_UPDATE_RECEIPT": loaded.receipt.path, "DROPSPACE_UPDATE_TOKEN": loaded.id.uuidString]
                let app = try await NSWorkspace.shared.openApplication(at: loaded.application, configuration: configuration)
                let launchDeadline = ContinuousClock.now.advanced(by: .seconds(45))
                var acknowledged = false
                while ContinuousClock.now < launchDeadline && !app.isTerminated {
                    if let bytes = try? Data(contentsOf: loaded.receipt), bytes == Data(loaded.id.uuidString.utf8) { acknowledged = true; break }
                    try await Task.sleep(for: .milliseconds(100))
                }
                if !acknowledged {
                    _ = app.terminate()
                    let stopDeadline = ContinuousClock.now.advanced(by: .seconds(10))
                    while !app.isTerminated && ContinuousClock.now < stopDeadline { try await Task.sleep(for: .milliseconds(100)) }
                    guard app.isTerminated else {
                        loaded.phase = "recoveryRequired"
                        loaded.message = "The new app did not confirm startup and is still running. Previous app retained at \(loaded.candidate.path). Quit it before recovery."
                        try loaded.save(to: planURL); exit(75)
                    }
                    throw DropSpaceError.unavailable("The updated app did not confirm successful startup")
                }
                // Keep the previous signed bundle for recovery until explicitly cleaned up.
                loaded.phase = "completedPendingCleanup"
                loaded.message = "Update started successfully; previous version remains at \(loaded.candidate.path)."
                do { try loaded.save(to: planURL) } catch {
                    // The new process is already running and acknowledged. A journal error
                    // must never enter the pre-start rollback path or move its executable.
                    fputs("Update started; could not persist completion: \(error.localizedDescription)\n", stderr)
                    exit(74)
                }
                guard renamex_np(loaded.candidate.path, loaded.backup.path, UInt32(RENAME_EXCL)) == 0 else {
                    exit(0)
                }
                loaded.phase = "completed"; loaded.message = nil
                do { try loaded.save(to: planURL) } catch {
                    fputs("Update started and previous app retained; completion journal could not be updated\n", stderr)
                    exit(74)
                }
                exit(0)
            } catch {
                // The old bundle stayed intact at candidate. Swap back without removing user data.
                guard renamex_np(loaded.candidate.path, loaded.application.path, UInt32(RENAME_SWAP)) == 0 else {
                    loaded.phase = "recoveryRequired"; loaded.message = "Update launch failed and automatic restoration failed. Previous application: \(loaded.candidate.path). \(error.localizedDescription)"
                    try loaded.save(to: planURL); exit(74)
                }
                loaded.phase = "rolledBack"; loaded.message = error.localizedDescription
                try loaded.save(to: planURL); plan = loaded
                let configuration = NSWorkspace.OpenConfiguration()
                configuration.environment = ["DROPSPACE_DATA_DIRECTORY": loaded.dataDirectory.path]
                _ = try await NSWorkspace.shared.openApplication(at: loaded.application, configuration: configuration)
                exit(1)
            }
        } catch {
            if var plan {
                if plan.phase != "rolledBack" { plan.phase = "failed" }
                plan.message = error.localizedDescription
                try? plan.save(to: planURL)
            }
            fputs("DropSpace update failed: \(error.localizedDescription)\n", stderr)
            exit(1)
        }
    }
}
