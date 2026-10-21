import AppKit
import SwiftUI
import DropSpaceCore

struct SettingsNavigation: View {
    let model: WorkspaceModel
    let categories: [String]
    @Binding var selection: String
    private func title(_ category: String) -> String { model.ui("SettingsPage" + (category == "DLC" ? "Dlc" : category)) }
    var body: some View {
        GeometryReader { geometry in
            let widths = categories.map { (title($0) as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 13)]).width + 32 }
            let total = widths.reduce(0, +)
            let available = geometry.size.width - (total > geometry.size.width ? 48 : 0)
            let count = widths.indices.prefix { index in widths[...index].reduce(0, +) <= available }.count
            HStack(spacing: 0) {
                ForEach(Array(categories.prefix(count)), id: \.self) { category in
                    Button { selection = category } label: {
                        Text(title(category)).lineLimit(1).padding(.horizontal, 16).frame(height: 44)
                            .overlay(alignment: .bottom) { if selection == category { Capsule().fill(.tint).frame(height: 3).padding(.horizontal, 16) } }
                    }.buttonStyle(.plain).accessibilityAddTraits(selection == category ? .isSelected : [])
                        .accessibilityIdentifier("settings-tab-" + category)
                }
                if count < categories.count {
                    Menu { ForEach(Array(categories.dropFirst(count)), id: \.self) { category in Button(title(category)) { selection = category } } }
                    label: { Image(systemName: "ellipsis").frame(width: 36, height: 44) }.menuStyle(.borderlessButton).fixedSize().accessibilityLabel("More settings pages")
                }
                Spacer(minLength: 0)
            }
        }.frame(height: 44).padding(.horizontal, 8)
    }
}

struct SettingsCard<Content: View>: View {
    @ViewBuilder let content: () -> Content
    var body: some View {
        content().padding(.horizontal, 14).padding(.vertical, 10).frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 8))
            .overlay { RoundedRectangle(cornerRadius: 8).stroke(.primary.opacity(0.08), lineWidth: 1) }
    }
}
struct SettingsRow<Content: View>: View {
    let title: String
    @ViewBuilder let control: () -> Content
    var body: some View {
        SettingsCard { HStack(spacing: 20) { Text(title).frame(maxWidth: .infinity, alignment: .leading); control().labelsHidden() } }
    }
}
struct SettingsGroup<Content: View>: View {
    let title: String
    @ViewBuilder let content: () -> Content
    init(_ title: String, @ViewBuilder content: @escaping () -> Content) { self.title = title; self.content = content }
    var body: some View {
        VStack(alignment: .leading, spacing: 10) { Text(title).font(.system(size: 20, weight: .semibold)).padding(.top, 12).padding(.bottom, 4); content() }
    }
}

/// Windows SettingsForm's 350ms edit coalescing and fixed 160pt readout. Saving
/// never disables a captured slider or replaces a newer local drag with a prior
/// completed transaction; blur, release and navigation flush the pending value.
struct SettingsValueSlider: View {
    let title: String
    let model: WorkspaceModel
    let read: (AppSettings) -> Double
    let write: (inout AppSettings, Double) -> Void
    let range: ClosedRange<Double>
    let step: Double
    let format: (Double) -> String
    @State private var draft: Double
    @State private var editing = false
    @State private var pending = false
    @State private var revision = 0
    @State private var delay: Task<Void, Never>?
    @FocusState private var focused: Bool
    init(title: String, model: WorkspaceModel, key: WritableKeyPath<AppSettings, Double>, range: ClosedRange<Double>, step: Double, format: @escaping (Double) -> String) {
        self.init(title: title, model: model, read: { $0[keyPath: key] }, write: { $0[keyPath: key] = $1 },
                  range: range, step: step, format: format)
    }
    init(title: String, model: WorkspaceModel, read: @escaping (AppSettings) -> Double,
         write: @escaping (inout AppSettings, Double) -> Void, range: ClosedRange<Double>, step: Double,
         format: @escaping (Double) -> String) {
        self.title = title; self.model = model; self.read = read; self.write = write
        self.range = range; self.step = step; self.format = format
        _draft = State(initialValue: read(model.settings))
    }
    var body: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                HStack(spacing: 12) {
                    Slider(value: Binding(get: { draft }, set: queue), in: range, step: step, onEditingChanged: { value in editing = value; if !value { flush() } })
                        .focused($focused)
                        .accessibilityLabel(title).accessibilityValue(format(draft))
                    Text(format(draft)).frame(width: 160, alignment: .leading).help(format(draft))
                }
            }
        }.onChange(of: read(model.settings)) { _, value in if !editing && !pending { draft = value } }
            .onChange(of: focused) { _, value in if !value { editing = false; flush() } }
            .onDisappear { flush() }
    }
    private func queue(_ value: Double) {
        draft = min(range.upperBound, max(range.lowerBound, (value / step).rounded() * step)); pending = true; revision += 1
        delay?.cancel(); delay = Task { do { try await Task.sleep(for: .milliseconds(350)) } catch { return }; flush() }
    }
    private func flush() {
        delay?.cancel(); delay = nil; guard pending else { return }
        pending = false; let value = draft, run = revision
        let save = model.update { write(&$0, value) }
        Task { await save.value; if run == revision && !editing && !pending { draft = read(model.settings) } }
    }
}
