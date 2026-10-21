import Foundation
import Observation
import DropSpaceCore

/// Owns the parameter sheet and its native IO independently of view/selection
/// changes. A cancelled decoder retains its slot until it actually returns.
@MainActor @Observable public final class NativeImageExport {
    public struct Request: Identifiable, Sendable {
        public let id = UUID()
        public let item: DropItem
        public let action: QuickAction
        public let defaultDirectory: URL
    }
    public struct Parameters: Sendable {
        public let item: DropItem
        public let action: QuickAction
        public let format: String
        public let width: Int?
        public let height: Int?
        public let keepAspect: Bool
        public let directory: URL
    }
    public private(set) var request: Request?
    public var format = "png"
    public var preset = 100
    public var width = ""
    public var height = ""
    public var keepAspect = true
    public var destination: URL?
    public private(set) var naturalSize: (width: Int, height: Int)?
    public private(set) var busy = false
    public private(set) var retiring = false
    public private(set) var errorKey: String?
    public private(set) var errorDetail: String?
    @ObservationIgnored public var onOutput: (@MainActor (URL) -> Void)?
    @ObservationIgnored private var operation: Task<Void, Never>?
    @ObservationIgnored private var closed = false
    private let readSize: @Sendable (DropItem) async throws -> (width: Int, height: Int)
    private let export: @Sendable (Parameters) async throws -> URL

    public init(
        readSize: @escaping @Sendable (DropItem) async throws -> (width: Int, height: Int),
        export: @escaping @Sendable (Parameters) async throws -> URL
    ) {
        self.readSize = readSize
        self.export = export
    }

    @discardableResult
    public func begin(item: DropItem, action: QuickAction, directory: URL) -> Bool {
        guard !closed, request == nil, operation == nil,
              action == .resizeImage || action == .convertImage else { return false }
        let current = Request(item: item, action: action, defaultDirectory: directory)
        request = current
        format = action == .convertImage ? "jpeg" : ImageRules.nativeOutputFormat(forPath: item.file?.path ?? item.payloadPath ?? "")
        preset = 100; width = ""; height = ""; keepAspect = true
        naturalSize = nil; destination = nil; errorKey = nil; errorDetail = nil
        busy = true
        operation = Task {
            defer { operation = nil; busy = false; retiring = false }
            do {
                let size = try await readSize(current.item)
                try Task.checkCancellation()
                guard request?.id == current.id, !closed else { return }
                naturalSize = size; selectPreset(100)
            } catch is CancellationError { }
            catch {
                guard request?.id == current.id, !closed else { return }
                errorKey = "ActionFailed"; errorDetail = error.localizedDescription
            }
        }
        return true
    }

    public func selectPreset(_ value: Int) {
        preset = value
        guard value != 0, let naturalSize,
              let size = try? ImageRules.scaled(width: naturalSize.width, height: naturalSize.height, percent: value) else { return }
        width = String(size.width); height = String(size.height); keepAspect = true
        errorKey = nil; errorDetail = nil
    }

    public func apply() {
        guard let current = request, !closed, operation == nil, naturalSize != nil else { return }
        let w = width.trimmingCharacters(in: .whitespacesAndNewlines)
        let h = height.trimmingCharacters(in: .whitespacesAndNewlines)
        let parsedW = Int(w), parsedH = Int(h)
        guard (w.isEmpty || parsedW != nil), (h.isEmpty || parsedH != nil),
              parsedW.map({ (1...ImageRules.maximumDimension).contains($0) }) ?? true,
              parsedH.map({ (1...ImageRules.maximumDimension).contains($0) }) ?? true,
              (parsedW == nil) == (parsedH == nil),
              current.action != .resizeImage || parsedW != nil,
              ["png", "jpeg", "bmp"].contains(format) else {
            errorKey = "ActionParametersRequired"; errorDetail = nil; return
        }
        let parameters = Parameters(item: current.item, action: current.action, format: format, width: parsedW, height: parsedH,
                                    keepAspect: keepAspect, directory: destination ?? current.defaultDirectory)
        busy = true; errorKey = nil; errorDetail = nil
        operation = Task {
            defer { operation = nil; busy = false; retiring = false }
            do {
                try Task.checkCancellation()
                let output = try await export(parameters)
                guard request?.id == current.id, !closed else { return }
                request = nil
                onOutput?(output)
            } catch is CancellationError { }
            catch {
                guard request?.id == current.id, !closed else { return }
                errorKey = "ActionFailed"; errorDetail = error.localizedDescription
            }
        }
    }

    public func dismiss() {
        request = nil; operation?.cancel(); retiring = operation != nil
    }

    public func shutdown() async {
        closed = true; dismiss()
        await operation?.value
        onOutput = nil
    }
}
