import Foundation
import DropSpaceCore

// A separately built, real native example. Never included in the official
// catalog or embedded in the application as a substitute for module hosting.
let arguments = CommandLine.arguments
guard arguments.count == 5, arguments[1] == "--module-session", arguments[3] == "--data-version",
      arguments[2].count == 32, arguments[4] == "1" else { exit(64) }
let session = arguments[2], input = FileHandle.standardInput, output = FileHandle.standardOutput
var ready = false, lastID: Int64 = 0, showIsland = true

func readEnvelope() throws -> ModuleEnvelope? {
    var bytes = Data()
    while let byte = try input.read(upToCount: 1), !byte.isEmpty {
        if byte[0] == 10 { return try ModuleEnvelope.decode(bytes) }
        guard bytes.count < ModuleContract.maximumMessageBytes else { throw DropSpaceError.limit("ModuleMessageTooLarge") }
        bytes.append(byte)
    }
    guard bytes.isEmpty else { throw DropSpaceError.invalid("IncompleteMessage") }; return nil
}
do {
    while let request = try readEnvelope() {
        guard request.protocolVersion == 1, request.session == session, let id = Int64(request.id), id > lastID else { exit(65) }
        lastID = id
        var result: ModuleJSON = .null, island: ModuleJSON = .null, error: String?, stop = false
        switch request.method {
        case "hello":
            guard !ready, request.payload["moduleId"]?.string == "dropspace.sample", request.payload["moduleVersion"]?.string == "1.0.0",
                  request.payload["protocol"]?.integer == 1, request.payload["hostInterface"]?.integer == 1, request.payload["dataVersion"]?.integer == 1 else { exit(66) }
            ready = true
            result = .object([.init("moduleId", .string("dropspace.sample")), .init("moduleVersion", .string("1.0.0")),
                              .init("protocol", .number("1")), .init("dataVersion", .number("1"))])
        case "settings" where ready:
            if case .object(let values) = request.payload, values.count == 1, values[0].name == "show-island",
               let value = values[0].value.string, ["true", "false"].contains(value) {
                showIsland = value == "true"; result = .object([.init("saved", .boolean(true))])
            } else { error = "InvalidSetting" }
        case "action" where ready:
            if request.payload["action"]?.string == "show" {
                result = .object([.init("messageKey", .string("message"))])
                if showIsland {
                    let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
                    let value = ModuleIslandContent(compact: .init(key: "compact"), expanded: .init(key: "expanded"),
                        actions: [.init(id: "show", label: .init(key: "action"))], expiresAt: Date().addingTimeInterval(30))
                    island = try ModuleJSON.decode(encoder.encode(value))
                }
            } else { error = "UnknownAction" }
        case "stop": result = .object([.init("stopped", .boolean(true))]); stop = true
        default: error = ready ? "UnknownMethod" : "HandshakeRequired"
        }
        let envelope = ModuleEnvelope(session: session, id: request.id, method: "result", payload: .object([.init("Result", result), .init("Island", island)]), errorCode: error)
        try output.write(contentsOf: envelope.encoded() + Data([10]))
        if stop { exit(0) }
    }
} catch { exit(67) }
