import Foundation

struct DownloadIntent: Codable, Sendable {
    let identity: String
    let expectedBytes: Int64?
    let sha256: String?
    let maximumBytes: Int64?
}

struct DownloadHTTPFailure: LocalizedError {
    let status: Int
    let retryAfter: TimeInterval?
    init(_ response: HTTPURLResponse) {
        status = response.statusCode
        if let text = response.value(forHTTPHeaderField: "Retry-After"), let seconds = Int(text), seconds >= 0 {
            retryAfter = TimeInterval(seconds)
        } else if let text = response.value(forHTTPHeaderField: "Retry-After") {
            let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(secondsFromGMT: 0); formatter.dateFormat = "EEE',' dd MMM yyyy HH':'mm':'ss z"
            retryAfter = formatter.date(from: text)?.timeIntervalSinceNow
        } else { retryAfter = nil }
    }
    var errorDescription: String? { "Download HTTP status \(status)" }
    var transient: Bool { status == 408 || status == 429 || (500...599).contains(status) }
}

enum DownloadRetry {
    static func run<T: Sendable>(retrying: @Sendable (Int) async -> Void = { _ in }, _ operation: @Sendable () async throws -> T) async throws -> T {
        var attempt = 1
        while true {
            try Task.checkCancellation()
            do { return try await operation() }
            catch {
                try Task.checkCancellation()
                let transient: Bool
                if let http = error as? DownloadHTTPFailure { transient = http.transient }
                else { transient = error is URLError || error is POSIXError }
                guard transient, attempt < 3 else { throw error }
                var delay = min(16, pow(2, Double(attempt - 1))) * Double.random(in: 0.9...1.1)
                if let after = (error as? DownloadHTTPFailure)?.retryAfter, after > delay {
                    guard after <= 120 else { throw error }; delay = after
                }
                await retrying(attempt + 1)
                try await Task.sleep(for: .seconds(delay)); attempt += 1
            }
        }
    }
}
