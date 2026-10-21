import Foundation
import Darwin
import dnssd
import SystemConfiguration
import DropSpaceCore

public struct DropLinkNetworkInterface: Sendable, Equatable {
    public let name: String
    public let address: String
    public let index: UInt32
    public static func isPrivate(_ address: String) -> Bool {
        let parts = address.split(separator: ".").compactMap { UInt8($0) }
        return parts.count == 4 && (parts[0] == 10 || parts[0] == 192 && parts[1] == 168 || parts[0] == 172 && (16...31).contains(parts[1]))
    }
    public static func preferred() throws -> Self {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0 else { throw DropSpaceError.unavailable("Network interfaces are unavailable") }
        defer { freeifaddrs(list) }
        let primary = SCDynamicStoreCopyValue(nil, "State:/Network/Global/IPv4" as CFString) as? [String: Any]
        let primaryName = primary?["PrimaryInterface"] as? String
        var result: [Self] = [], entry = list
        while let current = entry {
            defer { entry = current.pointee.ifa_next }
            let value = current.pointee
            guard let addr = value.ifa_addr, addr.pointee.sa_family == AF_INET,
                  value.ifa_flags & UInt32(IFF_UP) != 0, value.ifa_flags & UInt32(IFF_LOOPBACK) == 0 else { continue }
            var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(addr, socklen_t(addr.pointee.sa_len), &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST) == 0 else { continue }
            let address = String(decoding: buffer.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self), name = String(cString: value.ifa_name)
            guard isPrivate(address) else { continue }
            result.append(.init(name: name, address: address, index: if_nametoindex(name)))
        }
        result.sort {
            let leftPhysical = $0.name.hasPrefix("en"), rightPhysical = $1.name.hasPrefix("en")
            if leftPhysical != rightPhysical { return leftPhysical }
            if ($0.name == primaryName) != ($1.name == primaryName) { return $0.name == primaryName }
            return $0.name == $1.name ? $0.address < $1.address : $0.name < $1.name
        }
        guard let chosen = result.first else { throw DropSpaceError.unavailable("No reachable private IPv4 network is available") }; return chosen
    }
}

/// Bonjour advertises only on the interface actually bound by the TLS listener.
/// All DNSService callbacks and deallocation run on the main dispatch queue.
@MainActor public final class DropLinkBonjourRegistration {
    private var reference: DNSServiceRef?
    private var pending: CheckedContinuation<Void, any Error>?
    private var timeout: Task<Void, Never>?
    public var onFailure: ((Int32) -> Void)?
    public init() {}
    public func publish(_ device: DropLinkDevice, interface: DropLinkNetworkInterface) async throws {
        guard reference == nil else { throw DropSpaceError.unavailable("Bonjour registration is already active") }
        let record = NetService.data(fromTXTRecord: ["v": Data("1.0".utf8), "id": Data(device.id.dropLinkD.utf8), "name": Data(device.displayName.utf8), "platform": Data("macos".utf8), "caps": Data(String(device.capabilities.rawValue).utf8), "fp": Data(device.identityFingerprint.utf8)])
        guard let port = device.endpoint.port, let wirePort = UInt16(exactly: port), wirePort > 0, record.count <= Int(UInt16.max) else { throw DropSpaceError.invalid("Bonjour descriptor") }
        let name = "DropSpace-" + device.id.dropLinkN.prefix(8)
        try Task.checkCancellation()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                pending = continuation
                let error = record.withUnsafeBytes { bytes in
                    DNSServiceRegister(&reference, 0, interface.index, name, "_dropspace._tcp", "local.", nil, wirePort.bigEndian, UInt16(record.count), bytes.baseAddress, { _, _, error, _, _, _, context in
                        guard let context else { return }
                        MainActor.assumeIsolated { Unmanaged<DropLinkBonjourRegistration>.fromOpaque(context).takeUnretainedValue().registered(error) }
                    }, Unmanaged.passUnretained(self).toOpaque())
                }
                guard error == kDNSServiceErr_NoError, let reference else { registered(error); return }
                let dispatch = DNSServiceSetDispatchQueue(reference, .main)
                guard dispatch == kDNSServiceErr_NoError else { registered(dispatch); return }
                timeout = Task { [weak self] in
                    do { try await Task.sleep(for: .seconds(30)) } catch { return }
                    self?.registered(Int32(kDNSServiceErr_Timeout))
                }
            }
        } onCancel: { Task { @MainActor [weak self] in self?.stop() } }
    }
    public func stop() {
        timeout?.cancel(); timeout = nil
        if let reference { DNSServiceRefDeallocate(reference); self.reference = nil }
        pending?.resume(throwing: CancellationError()); pending = nil
    }
    private func registered(_ error: DNSServiceErrorType) {
        timeout?.cancel(); timeout = nil
        let waiting = pending; pending = nil
        if error == kDNSServiceErr_NoError { waiting?.resume() }
        else { stop(); waiting?.resume(throwing: DropSpaceError.unavailable("Bonjour registration: \(error)")); onFailure?(error) }
    }
}

@MainActor public final class DropLinkBonjourBrowser: NSObject, @preconcurrency NetServiceBrowserDelegate, @preconcurrency NetServiceDelegate {
    private var browser: NetServiceBrowser?
    private var services: [String: NetService] = [:]
    private var serviceDevices: [String: UUID] = [:]
    private var devices: [UUID: DropLinkDevice] = [:]
    private var pending: CheckedContinuation<[DropLinkDevice], any Error>?
    private var timeout: Task<Void, Never>?
    public func discover(seconds: Double = 4) async throws -> [DropLinkDevice] {
        guard browser == nil, seconds > 0, seconds <= 60 else { throw DropSpaceError.invalid("Discovery is already running or has an invalid deadline") }
        try Task.checkCancellation()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                pending = continuation; devices = [:]
                let browser = NetServiceBrowser(); self.browser = browser; browser.delegate = self
                browser.searchForServices(ofType: "_dropspace._tcp.", inDomain: "local.")
                timeout = Task { [weak self] in
                    do { try await Task.sleep(for: .seconds(seconds)) } catch { return }
                    self?.finish(error: nil)
                }
            }
        } onCancel: { Task { @MainActor [weak self] in self?.stop() } }
    }
    public func stop() { finish(error: CancellationError()) }
    public func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService, moreComing: Bool) {
        guard self.browser === browser, services.count < 128 else { return }
        let key = serviceKey(service)
        services[key]?.stop(); services[key] = service; service.delegate = self; service.resolve(withTimeout: 3)
    }
    public func netServiceBrowser(_ browser: NetServiceBrowser, didRemove service: NetService, moreComing: Bool) {
        let key = serviceKey(service)
        services.removeValue(forKey: key)?.stop()
        if let id = serviceDevices.removeValue(forKey: key), !serviceDevices.values.contains(id) { devices[id] = nil }
    }
    public func netServiceBrowser(_ browser: NetServiceBrowser, didNotSearch errorDict: [String: NSNumber]) {
        guard self.browser === browser else { return }; finish(error: DropSpaceError.unavailable("Bonjour discovery: \(errorDict[NetService.errorCode]?.intValue ?? 0)"))
    }
    public func netServiceDidResolveAddress(_ sender: NetService) {
        guard services[serviceKey(sender)] === sender, let record = sender.txtRecordData(), record.count <= 4096,
              (1...65535).contains(sender.port) else { return }
        let values = NetService.dictionary(fromTXTRecord: record).compactMapValues { String(data: $0, encoding: .utf8) }
        guard values["v"] == "1.0", let id = values["id"].flatMap(UUID.init(uuidString:)), id != .zero,
              let name = values["name"], !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, name.utf16.count <= 64,
              !name.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }),
              let caps = values["caps"].flatMap(Int32.init), caps >= 0,
              let fingerprint = values["fp"], fingerprint.utf8.count == 64,
              fingerprint.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }),
              let platformName = values["platform"]?.lowercased(),
              let platform = ["windows": DevicePlatform.windows, "macos": .macOS, "ios": .iOS, "ipados": .iPadOS, "android": .android, "linux": .linux][platformName] else { return }
        let addresses = (sender.addresses ?? []).compactMap { bytes -> String? in
            bytes.withUnsafeBytes { buffer in
                guard buffer.count >= MemoryLayout<sockaddr_in>.size, let base = buffer.baseAddress else { return nil }
                let address = base.assumingMemoryBound(to: sockaddr.self)
                guard address.pointee.sa_family == AF_INET, Int(address.pointee.sa_len) <= buffer.count else { return nil }
                var text = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                guard getnameinfo(address, socklen_t(address.pointee.sa_len), &text, socklen_t(text.count), nil, 0, NI_NUMERICHOST) == 0 else { return nil }
                let result = String(decoding: text.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self); return DropLinkNetworkInterface.isPrivate(result) ? result : nil
            }
        }.sorted()
        guard let address = addresses.first, let endpoint = URL(string: "https://\(address):\(sender.port)") else { return }
        serviceDevices[serviceKey(sender)] = id
        devices[id] = .init(deviceID: id, name: name, platform: platform, capabilities: .init(rawValue: Int(caps)), fingerprint: fingerprint.lowercased(), endpoint: endpoint)
    }
    public func netService(_ sender: NetService, didNotResolve errorDict: [String: NSNumber]) { sender.stop() }
    private func serviceKey(_ service: NetService) -> String { service.name + "\0" + service.type + "\0" + service.domain }
    private func finish(error: (any Error)?) {
        timeout?.cancel(); timeout = nil
        browser?.stop(); browser?.delegate = nil; browser = nil
        for service in services.values { service.stop(); service.delegate = nil }; services.removeAll()
        let waiting = pending; pending = nil
        if let error { waiting?.resume(throwing: error) } else { waiting?.resume(returning: devices.values.sorted { $0.displayName.localizedStandardCompare($1.displayName) == .orderedAscending }) }
        devices.removeAll(); serviceDevices.removeAll()
    }
}
