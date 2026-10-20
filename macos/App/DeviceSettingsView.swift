import SwiftUI
import DropSpaceCore

struct DeviceSettingsView: View {
    @Bindable var model: WorkspaceModel
    var body: some View {
        SettingsGroup(model.ui("DevicesSharingSection.Text")) {
            row("DeviceHandoff", value: model.settings.enableDeviceHandoff) { enabled in
                model.update { $0.enableDeviceHandoff = enabled; $0.enableCrossDeviceClipboard = enabled && $0.enableCrossDeviceClipboard }
            }.accessibilityIdentifier("DeviceHandoffToggle")
            row("CrossDeviceClipboard", value: model.settings.enableCrossDeviceClipboard) { enabled in
                model.update { $0.enableCrossDeviceClipboard = enabled; $0.enableDeviceHandoff = enabled || $0.enableDeviceHandoff }
            }.accessibilityIdentifier("CrossDeviceClipboardToggle")
            HStack(spacing: 20) {
                labels("DefaultClipboardSyncMode")
                Picker(model.ui("DefaultClipboardSyncModeTitle.Text"), selection: Binding(get: { model.settings.defaultClipboardSyncMode }, set: { value in model.update { $0.defaultClipboardSyncMode = value } })) {
                    Text(model.ui("ClipboardSyncModeOff.Content")).tag(ClipboardSyncMode.off)
                    Text(model.ui("ClipboardSyncModeManual.Content")).tag(ClipboardSyncMode.manual)
                    Text(model.ui("ClipboardSyncModeTextUrl.Content")).tag(ClipboardSyncMode.textAndURL)
                    Text(model.ui("ClipboardSyncModeTextUrlImage.Content")).tag(ClipboardSyncMode.textURLAndImage)
                }.labelsHidden().frame(width: 220)
            }
            row("NearbySharing", value: model.settings.enableNearbySharing) { enabled in model.update { $0.enableNearbySharing = enabled } }
            row("InternetSharing", value: model.settings.enableInternetSharing) { enabled in model.update { $0.enableInternetSharing = enabled } }
            if let devices = model.devices { DeviceConnectionControls(model: model, devices: devices) }
            if let shares = model.shares {
                Button(model.ui("ManageNearbyLinksLabel.Text")) { model.openMainWindow?(); shares.showsPanel = true }
            }
            if let shares = model.internetShares {
                Button(model.ui("Mac.InternetManage")) { model.openMainWindow?(); shares.showsPanel = true }
                if !shares.isConfigured { Text(model.ui("Mac.InternetBackendMissing")).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            }
        }.task { model.devices?.reloadPeers() }
    }
    private func labels(_ prefix: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(model.ui(prefix + "Title.Text")).fontWeight(.semibold)
            Text(model.ui(prefix + "Description.Text")).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
    private func row(_ prefix: String, value: Bool, change: @escaping @MainActor @Sendable (Bool) -> Void) -> some View {
        HStack(spacing: 24) {
            labels(prefix)
            Toggle(model.ui(prefix + "Title.Text"), isOn: Binding(get: { value }, set: change)).labelsHidden().toggleStyle(.switch)
        }
    }
}
private struct DeviceConnectionControls: View {
    let model: WorkspaceModel
    @Bindable var devices: NativeDevices
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if devices.changing { ProgressView("Connecting to the local network…") }
            if let endpoint = devices.endpoint { Text(endpoint.absoluteString).font(.caption.monospaced()).textSelection(.enabled).accessibilityIdentifier("DropLinkEndpoint") }
            if let fingerprint = devices.fingerprint {
                DisclosureGroup("Device identity fingerprint") { Text(fingerprint).font(.caption.monospaced()).textSelection(.enabled) }
            }
            if let status = devices.clipboardStatus { Text(status).font(.caption).foregroundStyle(.secondary).textSelection(.enabled) }
            if let error = devices.error { Text(error).foregroundStyle(.red).textSelection(.enabled) }
            HStack(spacing: 8) {
                Button(model.ui("RefreshDevicesButton.Content")) { devices.discover() }.disabled(devices.discovering).accessibilityIdentifier("RefreshDevicesButton")
                Button(model.ui("PairDeviceButton.Content")) { devices.pairSelected() }.disabled(devices.selected == nil || devices.pairing).accessibilityIdentifier("PairDeviceButton")
                if devices.discovering || devices.pairing || devices.sending > 0 { ProgressView().controlSize(.small); Button(model.ui("CommonCancel")) { devices.cancelOperations() } }
            }
            if !devices.devices.isEmpty {
                List(selection: $devices.selected) {
                    ForEach(devices.devices) { device in
                        HStack(spacing: 8) { Image(systemName: device.platform == .macOS ? "desktopcomputer" : "pc"); Text(device.displayName); Text(device.platform == .macOS ? "macOS" : String(describing: device.platform)).foregroundStyle(.secondary) }.tag(device.id)
                    }
                }.frame(height: min(180, CGFloat(devices.devices.count) * 32 + 16)).listStyle(.plain)
            } else if devices.enabled && !devices.discovering { Text(model.ui("NoDevicesFound")).foregroundStyle(.secondary) }
            ForEach(devices.peers) { peer in
                HStack {
                    VStack(alignment: .leading) { Text(peer.displayName); Text(peer.trustState == .trusted ? "Paired" : "Pairing or cleanup pending").font(.caption).foregroundStyle(.secondary) }
                    Spacer()
                    if peer.trustState == .trusted {
                    Picker(peer.displayName + ": " + model.ui("DefaultClipboardSyncModeTitle.Text"), selection: Binding(get: {
                        model.settings.clipboardPeerModes.first { UUID(uuidString: $0.key) == peer.id }?.value ?? .off
                    }, set: { mode in
                        model.update { value in
                            value.clipboardPeerModes = value.clipboardPeerModes.filter { UUID(uuidString: $0.key) != peer.id }
                            value.clipboardPeerModes[peer.id.uuidString] = mode
                        }
                    })) {
                        Text(model.ui("ClipboardSyncModeOff.Content")).tag(ClipboardSyncMode.off)
                        Text(model.ui("ClipboardSyncModeManual.Content")).tag(ClipboardSyncMode.manual)
                        Text(model.ui("ClipboardSyncModeTextUrl.Content")).tag(ClipboardSyncMode.textAndURL)
                        Text(model.ui("ClipboardSyncModeTextUrlImage.Content")).tag(ClipboardSyncMode.textURLAndImage)
                    }.frame(width: 220).labelsHidden().accessibilityIdentifier("ClipboardPeerMode-" + peer.id.dropLinkN)
                    }
                    Button("Unpair") { devices.unpair(peer) }.accessibilityIdentifier("Unpair-" + peer.id.dropLinkN)
                }
            }
            if !devices.receivedTransfers.isEmpty {
                DisclosureGroup(model.ui("FileTransfersTitle")) {
                    ForEach(devices.receivedTransfers) { transfer in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(transfer.manifest.items.map(\.displayName).joined(separator: ", "))
                            Text(String(describing: transfer.state)).font(.caption).foregroundStyle(.secondary)
                            if !transfer.completedPaths.isEmpty { Text(transfer.completedPaths.joined(separator: "\n")).font(.caption).textSelection(.enabled) }
                            if let error = transfer.errorCategory { Text(error).foregroundStyle(.red) }
                            if transfer.pendingCleanup, transfer.state.isTerminal, transfer.cleanupError != nil {
                                if let error = transfer.cleanupError { Text(error).font(.caption).foregroundStyle(.secondary) }
                                Button(model.ui("ModuleRetryCleanup")) { devices.retryTransferCleanup() }
                            }
                        }.frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            }
        }
        .task {
            while !Task.isCancelled { await devices.refreshTransfers(); do { try await Task.sleep(for: .seconds(1)) } catch { return } }
        }
    }
}
