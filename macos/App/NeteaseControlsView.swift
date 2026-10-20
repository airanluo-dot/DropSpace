import SwiftUI
import DropSpacePlatform

struct NeteaseControlsView: View {
    let model: WorkspaceModel
    private var controls: NativeNeteaseControls { model.neteaseControls }
    var body: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text(model.ui("Mac.NeteaseNativeTitle")).font(.headline)
                    if let version = controls.version { Text(version).foregroundStyle(.secondary) }
                    Spacer()
                    if controls.busy { ProgressView().controlSize(.small) }
                }
                Text(model.ui("Mac.NeteaseNativeHelp")).font(.callout).foregroundStyle(.secondary)
                Text(model.ui(statusKey)).accessibilityIdentifier("netease-native-status")
                if let error = controls.errorCode {
                    Text("AX \(error)").font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                }
                HStack {
                    Button(model.ui("Mac.PlayerInspectControls")) { controls.inspect() }
                        .accessibilityIdentifier("netease-inspect")
                    if controls.state == .needsPermission {
                        Button(model.ui("Mac.PlayerAllowAccessibility")) { controls.requestPermission() }
                            .accessibilityIdentifier("netease-request-access")
                    }
                    if controls.state == .notRunning {
                        Button(model.ui("Mac.PlayerOpen", model.ui("LyricsProviderNetEase"))) { controls.openPlayer() }
                    }
                }
                if controls.state == .available {
                    HStack {
                        ForEach(NeteaseTransportCommand.allCases.filter { controls.commands.contains($0) }, id: \.self) { command in
                            Button(model.ui(commandKey(command))) { controls.perform(command) }
                                .accessibilityIdentifier("netease-" + command.rawValue)
                        }
                    }
                }
            }.disabled(controls.busy || model.isQuitting)
        }.onAppear { controls.setVisible(true) }
            .onDisappear { controls.setVisible(false) }
    }
    private var statusKey: String {
        switch controls.state {
        case .notInspected: "NeteaseEnhancementDetecting"
        case .notInstalled: "Mac.PlayerNotInstalled"
        case .notRunning: "Mac.PlayerNotRunning"
        case .needsPermission: "Mac.PlayerNeedsAccessibility"
        case .available: "Mac.PlayerMenuControlsAvailable"
        case .noControls: "Mac.PlayerControlsUnavailable"
        case .failed: "Mac.PlayerControlFailure"
        }
    }
    private func commandKey(_ value: NeteaseTransportCommand) -> String {
        switch value {
        case .play: "MediaPlayLabel"
        case .pause: "MediaPauseLabel"
        case .previous: "MediaPrevious.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"
        case .next: "MediaNext.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"
        }
    }
}
