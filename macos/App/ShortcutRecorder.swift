import AppKit
import SwiftUI
import DropSpaceCore
import DropSpacePlatform

struct ShortcutRecorder: View {
    let label: String
    let value: String
    let model: WorkspaceModel
    let commit: (String) -> Void
    @State private var recording = false
    @State private var monitor: Any?
    var body: some View {
        LabeledContent(label) {
            Button(recording ? "Press shortcut · Escape cancels" : value) { start() }
                .onDisappear { stop() }
                .accessibilityLabel(label + ": " + value)
        }
    }
    private func start() {
        stop(); recording = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            if event.keyCode == 53 { stop(); return nil }
            guard let key = GlobalHotkey.keyCodes.first(where: { $0.value == event.keyCode })?.key else { NSSound.beep(); return nil }
            let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
            let parts: [String] = [(flags.contains(.command) ? "Cmd" : nil),(flags.contains(.control) ? "Ctrl" : nil),(flags.contains(.option) ? "Alt" : nil),(flags.contains(.shift) ? "Shift" : nil)].compactMap { $0 }
            let chord = (parts + [key]).joined(separator: "+")
            guard Hotkey.parse(chord) != nil else { NSSound.beep(); return nil }
            stop(); commit(chord); return nil
        }
    }
    private func stop() { if let monitor { NSEvent.removeMonitor(monitor) }; monitor = nil; recording = false }
}

extension Hotkey {
    var equivalent: KeyEquivalent {
        if key == "Space" { return .space }
        if key.hasPrefix("F"), let number = Int(key.dropFirst()), let scalar = UnicodeScalar(0xF704 + number - 1) { return KeyEquivalent(Character(scalar)) }
        return KeyEquivalent(Character(key.lowercased()))
    }
    var eventModifiers: EventModifiers {
        var result: EventModifiers = []
        if modifiers.contains("Cmd") { result.insert(.command) }
        if modifiers.contains("Ctrl") { result.insert(.control) }
        if modifiers.contains("Alt") { result.insert(.option) }
        if modifiers.contains("Shift") { result.insert(.shift) }
        return result
    }
}

extension View {
    func configuredShortcut(_ id: String,model: WorkspaceModel) -> some View {
        let chord = Hotkey.parse(model.settings.shortcut(id))!
        return keyboardShortcut(chord.equivalent,modifiers: chord.eventModifiers)
    }
}
