import SwiftUI
import AppKit

/// NSSlider owns mouse tracking, keyboard increments and accessibility writes.
/// Only the end of a pointer gesture publishes a seek to the remote player.
struct NativeSeekSlider: NSViewRepresentable {
    var value: Double
    var duration: Double
    var editing: (Bool, Double) -> Void
    var preview: (Double) -> Void
    var commit: (Double) -> Void
    func makeNSView(context: Context) -> SeekControl {
        let view = SeekControl()
        view.minValue = 0; view.isContinuous = true
        view.target = view; view.action = #selector(SeekControl.changed)
        view.setAccessibilityLabel("Playback position")
        view.identifier = NSUserInterfaceItemIdentifier("playback-position")
        return view
    }
    func updateNSView(_ view: SeekControl, context: Context) {
        view.editing = editing; view.preview = preview; view.commit = commit
        if !view.tracking {
            if view.maxValue != max(1, duration) { view.maxValue = max(1, duration) }
            view.doubleValue = min(view.maxValue, max(0, value))
        }
    }
    @MainActor final class SeekControl: NSSlider {
        var tracking = false
        var editing: ((Bool, Double) -> Void)?
        var preview: ((Double) -> Void)?
        var commit: ((Double) -> Void)?
        override func mouseDown(with event: NSEvent) {
            tracking = true; editing?(true, doubleValue)
            super.mouseDown(with: event)
            // Ending editing can synchronously update the representable from
            // the player's previous poll. Preserve the user's completed value
            // before allowing that update to replace the control's value.
            let completedValue = doubleValue
            tracking = false; editing?(false, completedValue); commit?(completedValue)
        }
        @objc func changed() {
            preview?(doubleValue)
            if !tracking { commit?(doubleValue) }
        }
        override func setAccessibilityValue(_ value: Any?) {
            guard let number = value as? NSNumber, number.doubleValue.isFinite else { return }
            doubleValue = min(maxValue, max(minValue, number.doubleValue))
            preview?(doubleValue); commit?(doubleValue)
        }
    }
}
