import SwiftUI
import DropSpaceCore

/// Keep partially typed input local until native commit/focus loss. Persistence
/// remains immediate at commit, and a rejected save restores the persisted value.
struct SettingTextField: View {
    let title: String
    let model: WorkspaceModel
    let key: WritableKeyPath<AppSettings, String>
    @State private var draft = ""
    @State private var committing = false
    @FocusState private var focused: Bool
    var body: some View {
        TextField(title, text: $draft).focused($focused)
            .onAppear { draft = model.settings[keyPath: key] }
            .onChange(of: model.settings[keyPath: key]) { _, value in if !focused { draft = value } }
            .onChange(of: focused) { _, value in if !value { commit() } }
            .onSubmit { commit() }
    }
    private func commit() {
        guard !committing, draft != model.settings[keyPath: key] else { return }
        committing = true; let value = draft
        Task {
            await model.update { $0[keyPath: key] = value }.value
            draft = model.settings[keyPath: key]; committing = false
        }
    }
}
