import SwiftUI
import DropSpaceCore
import DropSpacePlatform

struct AIModelComponents: View {
    let models: NativeAIModels
    let model: WorkspaceModel
    var body: some View {
        ForEach(models.catalog) { component in
            Section(component.name) {
                let inventory = models.inventory[component.id]
                let active = models.running.contains(component.id)
                LabeledContent("Download size", value: ByteCountFormatter.string(fromByteCount: component.fullsize, countStyle: .file))
                if let inventory {
                    LabeledContent("Stored data", value: ByteCountFormatter.string(fromByteCount: inventory.artifactBytes, countStyle: .file))
                    if inventory.pendingCleanup {
                        Text("Some owned model files still need cleanup.").foregroundStyle(.orange)
                        Button("Retry Cleanup") { Task { await models.retryCleanup() } }
                    }
                    if inventory.installed {
                        Toggle("Enable component", isOn: Binding(get: { inventory.enabled }, set: { value in Task { await models.setEnabled(component.id, value) } })).disabled(active)
                        Button("Use This Model") { model.update { $0.lyrics.aiModelID = component.id } }
                    }
                }
                if active, let progress = models.progress[component.id] {
                    ProgressView(value: Double(progress.received), total: Double(max(1, progress.total)))
                    Text(progress.phase.rawValue.capitalized + " · " + ByteCountFormatter.string(fromByteCount: progress.received, countStyle: .file)).font(.caption)
                }
                if !active, models.progress[component.id]?.phase == .completed, models.failures[component.id] == nil {
                    Label("Model and license verified", systemImage: "checkmark.circle").font(.caption).foregroundStyle(.secondary)
                }
                HStack {
                    if active { Button("Cancel Download") { Task { await models.cancel(component.id) } } }
                    else if inventory?.installed != true { Button((inventory?.artifactBytes ?? 0) > 0 ? "Resume Download" : "Download Model") { models.install(component.id) } }
                    else { Button("Verify and Repair") { models.install(component.id) } }
                    if (inventory?.artifactBytes ?? 0) > 0 || inventory?.pendingCleanup == true {
                        Button("Remove Model", role: .destructive) { Task { await models.remove(component.id) } }
                    }
                }
                if let failure = models.failures[component.id] { Text(failure).foregroundStyle(.red).textSelection(.enabled) }
                ForEach(component.licenseFiles, id: \.self) { name in
                    Link("Model License", destination: URL(string: AIModelCatalog.releaseBase + name)!)
                }
                Text("Model files are downloaded only when you choose Download Model. They stay on this Mac; installation does not enable translation.").font(.caption).foregroundStyle(.secondary)
            }
        }.task { await models.refresh() }
    }
}
