import Foundation
import Observation
import DropSpaceCore

/// Application-owned pending edits, matching NativeSettingsEditor's 400ms
/// coalescing. Navigation and shutdown flush through the same transaction.
@MainActor @Observable final class DownloadLimitEditor {
    private weak var model: WorkspaceModel?
    var connections: Int
    var speed: Int
    var concurrent: Int
    private var pendingConnections: Int?
    private var pendingSpeed: Int?
    private var pendingConcurrent: Int?
    private var delay: Task<Void, Never>?
    private var saving = 0
    private var interactions = 0
    init(model: WorkspaceModel) {
        self.model = model; connections = model.settings.maxDownloadConnections
        speed = Int(model.settings.downloadSpeedLimitBytesPerSecond / 1_048_576); concurrent = model.settings.maxConcurrentDownloads
    }
    func queue(connections: Int? = nil, speed: Int? = nil, concurrent: Int? = nil) {
        if let connections { self.connections = min(256, max(1, connections)); pendingConnections = self.connections }
        if let speed { self.speed = min(1024, max(0, speed)); pendingSpeed = self.speed }
        if let concurrent { self.concurrent = min(3, max(1, concurrent)); pendingConcurrent = self.concurrent }
        delay?.cancel(); delay = Task { [weak self] in
            do { try await Task.sleep(for: .milliseconds(400)) } catch { return }
            await self?.flush()
        }
    }
    func interaction(_ active: Bool) { interactions = max(0, interactions + (active ? 1 : -1)); if !active { commit() } }
    func commit() { Task { await flush() } }
    func refresh() {
        guard saving == 0, interactions == 0, pendingConnections == nil, pendingSpeed == nil, pendingConcurrent == nil, let model else { return }
        connections = model.settings.maxDownloadConnections; speed = Int(model.settings.downloadSpeedLimitBytesPerSecond / 1_048_576)
        concurrent = model.settings.maxConcurrentDownloads
    }
    func flush() async {
        delay?.cancel(); delay = nil
        guard let model, pendingConnections != nil || pendingSpeed != nil || pendingConcurrent != nil else { refresh(); return }
        let connections = pendingConnections, speed = pendingSpeed, concurrent = pendingConcurrent
        pendingConnections = nil; pendingSpeed = nil; pendingConcurrent = nil; saving += 1
        await model.update { settings in
            if let connections { settings.maxDownloadConnections = connections }
            if let speed { settings.downloadSpeedLimitBytesPerSecond = Int64(speed) * 1_048_576 }
            if let concurrent { settings.maxConcurrentDownloads = concurrent }
        }.value
        saving -= 1; refresh()
    }
}
