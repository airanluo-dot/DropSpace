import AppKit
import CoreAudio
import Observation
import UserNotifications
import DropSpaceCore

public struct VolumeSnapshot: Equatable, Sendable {
    public let percent: Int
    public let muted: Bool
}

/// Public Core Audio properties, bound to the current default output device.
/// Initial attachment/device switching updates current state without inventing a user change.
@MainActor @Observable public final class NativeVolumeObserver {
    public private(set) var current: VolumeSnapshot?
    public private(set) var unavailableReason: String?
    public var onChange: ((VolumeSnapshot) -> Void)?
    private var enabled = false
    private var device: AudioObjectID = 0
    private var generation = 0
    private var systemListener: AudioObjectPropertyListenerBlock?
    private var listeners: [(AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []
    private var readScheduled = false
    public init() {}

    public func setEnabled(_ value: Bool) {
        guard enabled != value else { return }
        stop(); enabled = value
        guard value else { return }
        var address = Self.address(kAudioHardwarePropertyDefaultOutputDevice, scope: kAudioObjectPropertyScopeGlobal)
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            Task { @MainActor in self?.attach() }
        }
        let status = AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, listener)
        guard status == noErr else { unavailableReason = "Cannot observe output devices (\(status))"; return }
        systemListener = listener; attach()
    }

    private func attach() {
        detachDevice()
        guard enabled else { return }
        var output = AudioObjectID(0)
        var address = Self.address(kAudioHardwarePropertyDefaultOutputDevice, scope: kAudioObjectPropertyScopeGlobal)
        var bytes = UInt32(MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &bytes, &output) == noErr, output != 0 else {
            unavailableReason = "No default audio output"; return
        }
        device = output; generation += 1; let run = generation
        let candidates = [Self.address(kAudioDevicePropertyVolumeScalar), Self.address(kAudioDevicePropertyVolumeScalar, element: 1),
                          Self.address(kAudioDevicePropertyVolumeScalar, element: 2), Self.address(kAudioDevicePropertyMute)]
        for var property in candidates where AudioObjectHasProperty(device, &property) {
            let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
                Task { @MainActor in self?.scheduleRead(run: run) }
            }
            if AudioObjectAddPropertyListenerBlock(device, &property, .main, listener) == noErr { listeners.append((property, listener)) }
        }
        current = read()
        unavailableReason = current == nil ? "This output device does not expose volume through Core Audio" : nil
    }

    private func scheduleRead(run: Int) {
        guard enabled, generation == run, !readScheduled else { return }
        readScheduled = true
        Task { @MainActor [weak self] in
            await Task.yield()
            guard let self, enabled, generation == run else { return }
            readScheduled = false
            guard let value = read(), value != current else { return }
            current = value; onChange?(value)
        }
    }
    private func read() -> VolumeSnapshot? {
        func scalar(_ element: AudioObjectPropertyElement) -> Float32? {
            var property = Self.address(kAudioDevicePropertyVolumeScalar, element: element)
            var value: Float32 = 0; var bytes = UInt32(MemoryLayout<Float32>.size)
            guard AudioObjectGetPropertyData(device, &property, 0, nil, &bytes, &value) == noErr, value.isFinite else { return nil }
            return value
        }
        let channels = [scalar(1), scalar(2)].compactMap { $0 }
        guard let volume = scalar(kAudioObjectPropertyElementMain) ?? (channels.isEmpty ? nil : channels.reduce(0, +) / Float32(channels.count)) else { return nil }
        var muted: UInt32 = 0; var muteBytes = UInt32(MemoryLayout<UInt32>.size)
        var muteProperty = Self.address(kAudioDevicePropertyMute)
        _ = AudioObjectGetPropertyData(device, &muteProperty, 0, nil, &muteBytes, &muted)
        return VolumeSnapshot(percent: Int((min(1, max(0, Double(volume))) * 100).rounded(.toNearestOrEven)), muted: muted != 0)
    }
    private func detachDevice() {
        generation += 1; readScheduled = false
        for (var address, listener) in listeners { AudioObjectRemovePropertyListenerBlock(device, &address, .main, listener) }
        listeners.removeAll(); device = 0; current = nil
    }
    public func stop() {
        enabled = false; detachDevice()
        if let systemListener {
            var address = Self.address(kAudioHardwarePropertyDefaultOutputDevice, scope: kAudioObjectPropertyScopeGlobal)
            AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, systemListener)
        }
        systemListener = nil; unavailableReason = nil
    }
    private static func address(_ selector: AudioObjectPropertySelector, scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeOutput,
                                element: AudioObjectPropertyElement = kAudioObjectPropertyElementMain) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: element)
    }
}

public struct NativeActivity: Identifiable, Sendable {
    public enum Kind: Sendable { case notification, volume }
    public let id = UUID()
    public let kind: Kind
    public let title: String
    public let body: String
    public let volume: VolumeSnapshot?
    public let expiresAt: Date
}

@MainActor @Observable public final class NativeSystemActivities {
    public let volume = NativeVolumeObserver()
    public private(set) var notificationAuthorization: UNAuthorizationStatus = .notDetermined
    public private(set) var error: String?
    public private(set) var notification: NativeActivity?
    public private(set) var volumeActivity: NativeActivity?
    public var onChange: (() -> Void)?
    private var settings = SystemActivitySettings()
    private var expiry: Task<Void, Never>?
    private var generation = 0
    public init() {
        volume.onChange = { [weak self] value in
            guard let self, settings.showVolumeChanges else { return }
            volumeActivity = NativeActivity(kind: .volume, title: value.muted ? "Muted" : "Volume", body: "\(value.percent)%", volume: value,
                                            expiresAt: Date().addingTimeInterval(1.8))
            publish()
        }
    }
    public var current: NativeActivity? {
        let now = Date()
        if let notification, notification.expiresAt > now { return notification }
        if let volumeActivity, volumeActivity.expiresAt > now { return volumeActivity }
        return nil
    }
    public func configure(_ value: SystemActivitySettings, requestPermission: Bool = false) async {
        generation += 1; let run = generation
        settings = value; volume.setEnabled(value.showVolumeChanges)
        if !value.showApplicationNotifications { notification = nil }
        if !value.showVolumeChanges { volumeActivity = nil }
        publish()
        guard value.showApplicationNotifications else { return }
        let center = UNUserNotificationCenter.current()
        if requestPermission {
            do { _ = try await center.requestAuthorization(options: [.alert, .sound]) }
            catch { if generation == run { self.error = error.localizedDescription } }
        }
        let status = await center.notificationSettings().authorizationStatus
        guard generation == run else { return }
        notificationAuthorization = status
    }
    /// Only activities produced by DropSpace or its connected modules enter this method.
    public func notify(title: String, body: String) async {
        guard settings.showApplicationNotifications else { return }
        let run = generation
        let event = NativeActivity(kind: .notification, title: String(title.prefix(160)), body: String(body.prefix(2048)), volume: nil,
                                   expiresAt: Date().addingTimeInterval(5))
        notification = event; publish()
        let status = await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
        guard generation == run else { return }
        notificationAuthorization = status
        guard settings.showApplicationNotifications, [.authorized, .provisional].contains(status) else { return }
        let content = UNMutableNotificationContent(); content.title = event.title; content.body = event.body
        do { try await UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: event.id.uuidString, content: content, trigger: nil)) }
        catch { self.error = error.localizedDescription }
    }
    private func publish() {
        expiry?.cancel(); onChange?()
        let now = Date()
        let dates = [notification?.expiresAt, volumeActivity?.expiresAt].compactMap { $0 }.filter { $0 > now }
        guard let next = dates.min() else { return }
        expiry = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(max(0, next.timeIntervalSinceNow))) } catch { return }
            guard let self, !Task.isCancelled else { return }
            let now = Date()
            if let notification, notification.expiresAt <= now { self.notification = nil }
            if let volumeActivity, volumeActivity.expiresAt <= now { self.volumeActivity = nil }
            publish()
        }
    }
    public func stop() {
        generation += 1; expiry?.cancel(); volume.stop(); notification = nil; volumeActivity = nil; onChange?()
    }
}
