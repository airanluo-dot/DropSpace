import SwiftUI
import AppKit
import DropSpaceCore
import DropSpacePlatform

struct TextIntakeView: View {
    let model: WorkspaceModel
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    var body: some View {
        VStack(alignment: .leading,spacing: 16) {
            Text("Add Text or URL").font(.title2)
            TextEditor(text: $text).font(.body).border(.quaternary).frame(minHeight: 220)
            HStack { Spacer(); Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction); Button("Add to Temporary Space") { model.addText(text); dismiss() }.keyboardShortcut(.defaultAction).disabled(text.isEmpty) }
        }.padding(24).frame(width: 560)
    }
}
struct OnboardingView: View {
    let model: WorkspaceModel
    @State private var clipboard = true
    @State private var login = true
    var body: some View {
        VStack(alignment: .leading,spacing: 20) {
            Label("Welcome to DropSpace",systemImage: "tray.and.arrow.down").font(.title2)
            Text("Temporary Space keeps file references. Clipboard history is stored locally on this Mac.")
            Toggle("Record clipboard history",isOn: $clipboard)
            Toggle("Open DropSpace at login",isOn: $login)
            Text("macOS may ask for permission when you use clipboard history, player controls, or other system integrations.").font(.callout).foregroundStyle(.secondary)
            if let error = model.error { Text(error).foregroundStyle(.red).font(.caption).textSelection(.enabled) }
            HStack { Spacer(); Button("Continue") { model.update { $0.clipboardPaused = !clipboard; $0.startAtLogin = login; $0.privacyChoicesCompleted = true } }.keyboardShortcut(.defaultAction).disabled(model.savingSettings) }
        }.padding(28).frame(width: 460)
    }
}
struct ImageTransformView: View {
    let model: WorkspaceModel
    let request: NativeImageExport.Request
    @State private var folderPanel: NSOpenPanel?
    private var owner: NativeImageExport { model.imageExport }
    var body: some View {
        @Bindable var owner = model.imageExport
        VStack(alignment: .leading, spacing: 16) {
            Text(model.ui(request.action.labelResource)).font(.title2)
            Text(model.ui("QuickActionParametersDescription")).foregroundStyle(.secondary)
            Form {
                LabeledContent(model.ui("QuickActionDestination")) {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(owner.destination?.path ?? model.ui("QuickActionDefaultDestination")).textSelection(.enabled)
                            .accessibilityIdentifier("image-export-destination")
                        Button(model.ui("QuickActionChooseFolder"), action: chooseFolder)
                            .accessibilityIdentifier("image-export-choose-folder")
                    }
                }
                Picker(model.ui("QuickActionOutputFormat"), selection: $owner.format) {
                    Text(model.ui("QuickActionFormatPng")).tag("png")
                    Text(model.ui("QuickActionFormatJpeg")).tag("jpeg")
                    Text(model.ui("QuickActionFormatBmp")).tag("bmp")
                }.accessibilityIdentifier("image-export-format")
                Picker(model.ui("ImageSizePreset"), selection: Binding(get: { owner.preset }, set: owner.selectPreset)) {
                    Text(model.ui("ImageSizeOriginal")).tag(100)
                    ForEach([75, 50, 25], id: \.self) { Text("\($0)%").tag($0) }
                    Text(model.ui("ImageSizeCustom")).tag(0)
                }.accessibilityIdentifier("image-export-preset")
                if owner.preset == 0 {
                    TextField(model.ui("QuickActionWidth"), text: $owner.width).accessibilityIdentifier("image-export-width")
                    TextField(model.ui("QuickActionHeight"), text: $owner.height).accessibilityIdentifier("image-export-height")
                    Toggle(model.ui("QuickActionKeepAspectRatio"), isOn: $owner.keepAspect)
                    if request.action == .convertImage { Text(model.ui("QuickActionResizeOptional")).font(.caption) }
                } else if owner.naturalSize != nil {
                    Text(model.ui("ImageSizeOutput", owner.width, owner.height))
                }
            }.formStyle(.grouped).disabled(owner.busy)
            if let key = owner.errorKey {
                Text(model.ui(key)).foregroundStyle(.red).accessibilityIdentifier("image-export-error")
                if let detail = owner.errorDetail { Text(detail).font(.caption).textSelection(.enabled) }
            }
            HStack {
                if owner.busy { ProgressView().controlSize(.small) }
                Spacer()
                Button(model.ui("QuickActionCancel")) { owner.dismiss() }
                    .keyboardShortcut(.cancelAction).accessibilityIdentifier("image-export-cancel")
                Button(model.ui("QuickActionApply")) { owner.apply() }
                    .keyboardShortcut(.defaultAction).disabled(owner.busy || owner.naturalSize == nil)
                    .accessibilityIdentifier("image-export-apply")
            }
        }.padding(24).frame(width: 520)
            .onDisappear { folderPanel?.cancel(nil); folderPanel = nil }
    }
    private func chooseFolder() {
        guard folderPanel == nil, owner.request?.id == request.id, !owner.busy else { return }
        let panel = NSOpenPanel()
        panel.canChooseFiles = false; panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false; panel.canCreateDirectories = true
        panel.prompt = model.ui("QuickActionChooseFolder")
        panel.directoryURL = owner.destination ?? request.defaultDirectory
        folderPanel = panel
        let completed: (NSApplication.ModalResponse) -> Void = { result in
            folderPanel = nil
            guard owner.request?.id == request.id, !model.isQuitting, result == .OK, let url = panel.url else { return }
            owner.destination = url
        }
        if let window = NSApp.keyWindow { panel.beginSheetModal(for: window, completionHandler: completed) }
        else { panel.begin(completionHandler: completed) }
    }
}
