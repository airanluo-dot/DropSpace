import SwiftUI
import AppKit
import DropSpaceCore
import DropSpacePlatform

@MainActor @Observable final class WidgetRuntime {
    let metrics = SystemMetrics()
    var stopwatchStarted: Date?
    var stopwatchElapsed: TimeInterval = 0
    var focusEnd: Date? { didSet { startTimers() } }
    var countdownEnd: Date? { didSet { startTimers() } }
    var countdownSeconds = 300
    var onTimerCompleted: ((String) -> Void)?
    private var timer: Timer?
    func startTimers() {
        guard focusEnd != nil || countdownEnd != nil else { stopTimers(); return }
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let now = Date()
                if let end = self.focusEnd, end <= now { self.focusEnd = nil; self.onTimerCompleted?("Focus timer complete") }
                if let end = self.countdownEnd, end <= now { self.countdownEnd = nil; self.onTimerCompleted?("Countdown complete") }
            }
        }
    }
    func stopTimers() { timer?.invalidate(); timer = nil }
    func stopwatch(_ now: Date) -> TimeInterval { stopwatchElapsed + (stopwatchStarted.map { now.timeIntervalSince($0) } ?? 0) }
    func toggleStopwatch() { if let start = stopwatchStarted { stopwatchElapsed += Date().timeIntervalSince(start); stopwatchStarted = nil } else { stopwatchStarted = Date() } }
}
struct WidgetGrid: View {
    let model: WorkspaceModel
    let runtime: WidgetRuntime
    var body: some View {
        GeometryReader { geometry in
            let cellWidth = (geometry.size.width - 7 * 8) / 8
            let cellHeight = (geometry.size.height - 3 * 8) / 4
            ZStack(alignment: .topLeading) {
                ForEach(model.settings.widgets.layout.expanded) { item in
                    WidgetTile(id: item.id,model: model,runtime: runtime)
                        .frame(width: cellWidth * CGFloat(item.size.columns) + CGFloat(item.size.columns - 1) * 8,height: cellHeight * CGFloat(item.size.rows) + CGFloat(item.size.rows - 1) * 8)
                        .background(.quaternary,in: RoundedRectangle(cornerRadius: 10))
                        .offset(x: CGFloat(item.column) * (cellWidth + 8),y: CGFloat(item.row) * (cellHeight + 8))
                }
            }
        }
    }
}
struct WidgetTile: View {
    let id: WidgetID; let model: WorkspaceModel; let runtime: WidgetRuntime
    var body: some View {
        TimelineView(.periodic(from: .now,by: 1)) { context in
            VStack(spacing: 3) {
                switch id {
                case .clock: Text(context.date,style: .time).monospacedDigit(); Text(context.date,format: .dateTime.weekday()).font(.caption)
                case .utcClock: Text(context.date.formatted(Date.FormatStyle(date: .omitted,time: .standard,timeZone: TimeZone(secondsFromGMT: 0)!))).monospacedDigit(); Text("UTC").font(.caption)
                case .calendar: Text(context.date,format: .dateTime.month(.wide)); Text(context.date,format: .dateTime.day()).font(.largeTitle); Text(context.date,format: .dateTime.weekday(.wide)).font(.caption)
                case .resourceUsage: Text(runtime.metrics.cpuPercent.map { "CPU \(Int($0))%" } ?? "CPU …"); Text("RAM \(ByteCountFormatter.string(fromByteCount: Int64(runtime.metrics.memoryUsed),countStyle: .memory))").font(.caption)
                case .settings: Button { model.openSettings?() } label: { Image(systemName: "gearshape").font(.title2) }.help("Settings")
                case .battery: Image(systemName: runtime.metrics.charging ? "battery.100percent.bolt" : "battery.50percent"); Text(runtime.metrics.batteryPercent.map { "\($0)%" } ?? "AC power")
                case .uptime: Text("Uptime").font(.caption); Text(duration(runtime.metrics.uptime)).monospacedDigit()
                case .stopwatch: Text(duration(runtime.stopwatch(context.date))).monospacedDigit(); HStack { Button(runtime.stopwatchStarted == nil ? "Start" : "Pause") { runtime.toggleStopwatch() }; Button("Reset") { runtime.stopwatchElapsed = 0; runtime.stopwatchStarted = nil } }.font(.caption)
                case .clipboardPause: Button { model.update { $0.clipboardPaused.toggle() } } label: { Image(systemName: model.settings.clipboardPaused ? "play" : "pause").font(.system(size: 18)) }.help(model.ui(model.settings.clipboardPaused ? "ClipboardIslandResume" : "ClipboardIslandPause")).accessibilityLabel(model.ui(model.settings.clipboardPaused ? "ClipboardIslandResume" : "ClipboardIslandPause"))
                case .focusTimer: Text(runtime.focusEnd.map { duration(max(0,$0.timeIntervalSince(context.date))) } ?? "25:00").monospacedDigit(); Button(runtime.focusEnd == nil ? "Focus" : "Reset") { runtime.focusEnd = runtime.focusEnd == nil ? Date().addingTimeInterval(1500) : nil }
                case .countdown: Text(runtime.countdownEnd.map { duration(max(0,$0.timeIntervalSince(context.date))) } ?? duration(Double(runtime.countdownSeconds))).monospacedDigit(); HStack { TextField("Seconds",value: Binding(get: { runtime.countdownSeconds },set: { runtime.countdownSeconds = max(1,min(86400,$0)) }),format: .number).frame(width: 50); Button(runtime.countdownEnd == nil ? "Start" : "Reset") { runtime.countdownEnd = runtime.countdownEnd == nil ? Date().addingTimeInterval(Double(runtime.countdownSeconds)) : nil } }
                case .diskSpace: Text("Free space").font(.caption); Text(runtime.metrics.diskAvailable.map { ByteCountFormatter.string(fromByteCount: $0,countStyle: .file) } ?? "Unavailable")
                case .networkStatus: Image(systemName: runtime.metrics.online ? "network" : "network.slash"); Text(runtime.metrics.online ? runtime.metrics.interface : "Offline")
                case .calculator: Button { NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: "/System/Applications/Calculator.app"),configuration: .init()) } label: { Label("Calculate",systemImage: "plus.forwardslash.minus") }.labelStyle(.iconOnly)
                case .pinnedItems: Button { model.section = "pinned"; model.refresh(); model.openMainWindow?() } label: { Label("Pinned",systemImage: "pin") }
                case .weekProgress: let day = (Calendar.current.component(.weekday,from: context.date) + 5) % 7; Text("Week").font(.caption); ProgressView(value: Double(day * 24 + Calendar.current.component(.hour,from: context.date)),total: 168); Text("\(Int(Double(day * 24 + Calendar.current.component(.hour,from: context.date)) / 168 * 100))%").font(.caption)
                }
            }.padding(5).font(.callout).minimumScaleFactor(0.7).buttonStyle(.borderless)
        }
    }
    private func duration(_ value: TimeInterval) -> String { let seconds = max(0,Int(value)); return seconds >= 3600 ? String(format: "%d:%02d:%02d",seconds / 3600,seconds / 60 % 60,seconds % 60) : String(format: "%02d:%02d",seconds / 60,seconds % 60) }
}
struct WidgetEditor: View {
    let model: WorkspaceModel
    var body: some View {
        Toggle("Enable widgets",isOn: Binding(get: { model.settings.widgets.enabled },set: { value in model.update { $0.widgets.enabled = value } }))
        Section("Compact Slots") {
            ForEach(0..<3,id: \.self) { slot in
                Picker("Slot \(slot + 1)",selection: Binding(get: { model.settings.widgets.layout.compact[slot]?.rawValue ?? "" },set: { value in model.update { $0.widgets.layout.compact[slot] = WidgetID(rawValue: value) } })) { Text("None").tag(""); Text("Clock").tag("clock"); Text("Resources").tag("resourceUsage") }
            }
        }
        Section("Expanded · 8 × 4") {
            ForEach(WidgetID.allCases,id: \.self) { id in
                DisclosureGroup(id.rawValue) {
                    let item = model.settings.widgets.layout.expanded.first { $0.id == id }
                    Toggle("Enabled",isOn: Binding(get: { item != nil },set: { enabled in model.update { settings in
                        if enabled { if let slot = settings.widgets.layout.slot(id,size: id.sizes[0]) { settings.widgets.layout.expanded.append(slot) } else { model.error = "No free space for this widget" } }
                        else { settings.widgets.layout.expanded.removeAll { $0.id == id } }
                    } }))
                    if let item {
                        Picker("Size",selection: Binding(get: { item.size },set: { size in change(item,size: size) })) { ForEach(id.sizes,id: \.self) { size in Text("\(size.columns) × \(size.rows)").tag(size) } }
                        Stepper("Column: \(item.column + 1)",value: Binding(get: { item.column },set: { change(item,column: $0) }),in: 0...(8-item.size.columns))
                        Stepper("Row: \(item.row + 1)",value: Binding(get: { item.row },set: { change(item,row: $0) }),in: 0...(4-item.size.rows))
                    }
                }
            }
            Button("Reset Widget Layout") { model.update { $0.widgets.layout = .default } }
        }
    }
    private func change(_ item: WidgetPlacement,size: WidgetSize? = nil,column: Int? = nil,row: Int? = nil) {
        var desired = item; desired.size = size ?? item.size; desired.column = column ?? item.column; desired.row = row ?? item.row
        model.update { settings in if !settings.widgets.layout.move(desired) { model.error = "This position overlaps another widget or exceeds the grid" } }
    }
}
