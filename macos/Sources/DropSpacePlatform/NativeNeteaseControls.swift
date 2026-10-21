import AppKit
@preconcurrency import ApplicationServices
import Observation
import DropSpaceCore

public enum NeteaseTransportCommand: String, Sendable, CaseIterable {
    case play, pause, previous, next
}

/// A deliberately narrow public-menu adapter. It never invents track metadata,
/// a timeline, artwork or an audio-source identity from transport controls.
@MainActor @Observable public final class NativeNeteaseControls {
    public enum State: Sendable { case notInspected, notInstalled, notRunning, needsPermission, available, noControls, failed }
    public nonisolated static let bundleID = "com.netease.163music"
    public private(set) var state = State.notInspected
    public private(set) var version: String?
    public private(set) var commands = Set<NeteaseTransportCommand>()
    public private(set) var busy = false
    public private(set) var errorCode: Int32?
    private let adapter = NeteaseMenuAdapter()
    private var work: Task<Void, Never>?
    private var observation: Task<Void, Never>?
    private var revision = 0
    private var closed = false
    private var processID: pid_t?
    private var processStartedAt: Date?

    public init() {}
    public func setVisible(_ visible: Bool) {
        observation?.cancel(); observation = nil
        guard visible, !closed else { return }
        inspect()
        observation = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(1)) } catch { return }
                guard let self, !closed else { return }
                // Uses the same single operation slot; never overlaps a command.
                inspect()
            }
        }
    }
    public func inspect() { begin(nil) }
    public func perform(_ command: NeteaseTransportCommand) {
        guard commands.contains(command) else { return }
        begin(command)
    }
    public func requestPermission() {
        guard !closed else { return }
        _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
        inspect()
    }
    public func openPlayer() {
        guard !closed, let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: Self.bundleID) else { return }
        // Launch only the application the user explicitly selected.
        NSWorkspace.shared.openApplication(at: url, configuration: .init()) { [weak self] _, _ in
            Task { @MainActor [weak self] in self?.inspect() }
        }
    }
    private func begin(_ command: NeteaseTransportCommand?) {
        guard !closed, !busy else { return }
        revision &+= 1; let run = revision, expectedPID = processID, expectedStart = processStartedAt
        busy = true; errorCode = nil
        work = Task { [weak self] in
            guard let self else { return }
            defer { if revision == run { busy = false; work = nil } }
            guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: Self.bundleID) else {
                state = .notInstalled; commands = []; processID = nil; version = nil; return
            }
            version = Bundle(url: url)?.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
            guard let app = NSRunningApplication.runningApplications(withBundleIdentifier: Self.bundleID).first else {
                state = .notRunning; commands = []; processID = nil; return
            }
            let pid = app.processIdentifier, launched = app.launchDate
            guard command == nil || expectedPID == pid && expectedStart == launched else { commands = []; state = .noControls; return }
            guard AXIsProcessTrusted() else { state = .needsPermission; commands = []; processID = pid; return }
            do {
                let result = try await adapter.inspect(pid: pid, launched: launched, command: command)
                try Task.checkCancellation()
                guard !closed, revision == run,
                      let current = NSRunningApplication(processIdentifier: pid),
                      current.bundleIdentifier == Self.bundleID, current.launchDate == launched else { return }
                commands = result; processID = pid; processStartedAt = launched
                state = result.isEmpty ? .noControls : .available
            } catch is CancellationError { }
            catch {
                guard !closed, revision == run else { return }
                commands = []; errorCode = (error as? NeteaseAXFailure)?.code
                state = AXIsProcessTrusted() ? .failed : .needsPermission
            }
        }
    }
    public func shutdown() async {
        closed = true; revision &+= 1
        let observer = observation; observation = nil; observer?.cancel()
        let old = work; work = nil; old?.cancel(); await old?.value
        await observer?.value
        busy = false; commands = []; processID = nil; processStartedAt = nil
    }
}

private struct NeteaseAXFailure: Error { var code: Int32 }

/// AX calls stay off MainActor. Each object has its own messaging timeout; the
/// public API does not inherit an application's timeout onto child objects.
private actor NeteaseMenuAdapter {
    private struct Control { let element: AXUIElement; let action: String }
    func inspect(pid: pid_t, launched: Date?, command: NeteaseTransportCommand?) async throws -> Set<NeteaseTransportCommand> {
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        let app = AXUIElementCreateApplication(pid)
        let controls = try controls(app, deadline: deadline)
        if let command {
            guard let control = controls[command] else { throw NeteaseAXFailure(code: AXError.actionUnsupported.rawValue) }
            try check(deadline)
            // Recheck process identity immediately before the sole action.
            guard let current = NSRunningApplication(processIdentifier: pid),
                  current.bundleIdentifier == NativeNeteaseControls.bundleID,
                  current.launchDate == launched else { throw CancellationError() }
            let error = AXUIElementPerformAction(control.element, control.action as CFString)
            guard error == .success else { throw NeteaseAXFailure(code: error.rawValue) }
            // AX action success only acknowledges delivery. This player changes
            // its menu asynchronously; observe the opposite transport state
            // before publishing it. Never replay an unconfirmed action.
            while true {
                try check(deadline)
                guard let current = NSRunningApplication(processIdentifier: pid),
                      current.bundleIdentifier == NativeNeteaseControls.bundleID,
                      current.launchDate == launched else { throw CancellationError() }
                let fresh = Set(try self.controls(app, deadline: deadline).keys)
                let expected: NeteaseTransportCommand? = command == .play ? .pause : command == .pause ? .play : nil
                if expected == nil || fresh.contains(expected!) && !fresh.contains(command) { return fresh }
                try await Task.sleep(for: .milliseconds(50))
            }
        }
        return Set(controls.keys)
    }
    private func check(_ deadline: ContinuousClock.Instant) throws {
        try Task.checkCancellation()
        guard ContinuousClock.now < deadline else { throw NeteaseAXFailure(code: AXError.cannotComplete.rawValue) }
    }
    private func value(_ element: AXUIElement, _ key: String, deadline: ContinuousClock.Instant) throws -> CFTypeRef? {
        try check(deadline)
        AXUIElementSetMessagingTimeout(element, 0.15)
        var value: CFTypeRef?
        let status = AXUIElementCopyAttributeValue(element, key as CFString, &value)
        guard status == .success || status == .attributeUnsupported || status == .noValue else { throw NeteaseAXFailure(code: status.rawValue) }
        return value
    }
    private func children(_ element: AXUIElement, deadline: ContinuousClock.Instant) throws -> [AXUIElement] {
        try check(deadline); AXUIElementSetMessagingTimeout(element, 0.15)
        var count = 0
        let counted = AXUIElementGetAttributeValueCount(element, kAXChildrenAttribute as CFString, &count)
        if counted == .attributeUnsupported || counted == .noValue { return [] }
        guard counted == .success else { throw NeteaseAXFailure(code: counted.rawValue) }
        // Never advertise an unambiguous action from a truncated menu.
        guard count <= 32 else { throw NeteaseAXFailure(code: AXError.cannotComplete.rawValue) }
        guard count > 0 else { return [] }
        var list: CFArray?
        let status = AXUIElementCopyAttributeValues(element, kAXChildrenAttribute as CFString, 0, count, &list)
        guard status == .success || status == .attributeUnsupported || status == .noValue else { throw NeteaseAXFailure(code: status.rawValue) }
        return (list as? [AXUIElement]) ?? []
    }
    private func controls(_ app: AXUIElement, deadline: ContinuousClock.Instant) throws -> [NeteaseTransportCommand: Control] {
        guard let raw = try value(app, kAXMenuBarAttribute, deadline: deadline),
              CFGetTypeID(raw) == AXUIElementGetTypeID() else { return [:] }
        let bar = unsafeDowncast(raw, to: AXUIElement.self)
        var groups: [AXUIElement] = []
        for item in try children(bar, deadline: deadline) {
            if try value(item, kAXTitleAttribute, deadline: deadline) as? String == "控制" { groups.append(item) }
        }
        guard groups.count == 1 else { return [:] }
        var pending = [(groups[0], 0)], visited = 0
        var found: [NeteaseTransportCommand: [Control]] = [:]
        let titles: [String: NeteaseTransportCommand] = ["播放": .play, "暂停": .pause, "上一个": .previous, "下一个": .next]
        while !pending.isEmpty, visited < 48 {
            try check(deadline)
            let (element, depth) = pending.removeFirst(); visited += 1
            let role = try value(element, kAXRoleAttribute, deadline: deadline) as? String
            if role == kAXMenuItemRole,
               let title = try value(element, kAXTitleAttribute, deadline: deadline) as? String,
               let command = titles[title],
               (try value(element, kAXEnabledAttribute, deadline: deadline) as? Bool) == true {
                var names: CFArray?
                AXUIElementSetMessagingTimeout(element, 0.15)
                let status = AXUIElementCopyActionNames(element, &names)
                if status == .success, let actions = names as? [String],
                   let action = [kAXPressAction, kAXPickAction].first(where: { actions.contains($0) }) {
                    found[command, default: []].append(.init(element: element, action: action))
                }
            }
            if depth < 3, role == kAXMenuBarItemRole || role == kAXMenuRole {
                for child in try children(element, deadline: deadline) { pending.append((child, depth + 1)) }
            }
        }
        guard pending.isEmpty else { throw NeteaseAXFailure(code: AXError.cannotComplete.rawValue) }
        // Duplicate semantic controls are ambiguous, not permission to pick one.
        return found.compactMapValues { $0.count == 1 ? $0.first : nil }
    }
}
