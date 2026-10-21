import Foundation
import Darwin
import DropSpaceCore

private final class NoRedirect: NSObject,URLSessionTaskDelegate,Sendable {
    func urlSession(_ session: URLSession,task: URLSessionTask,willPerformHTTPRedirection response: HTTPURLResponse,newRequest request: URLRequest,completionHandler: @escaping @Sendable (URLRequest?) -> Void) { completionHandler(nil) }
}
public actor HTTPDownloader {
    public let budget: DownloadBudget
    private let session: URLSession
    private let delegate = NoRedirect()
    private let userAgent: String?
    public init(budget: DownloadBudget, requestTimeout: TimeInterval = 30, userAgent: String? = nil) {
        self.budget = budget
        self.userAgent = userAgent
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil; configuration.urlCredentialStorage = nil; configuration.urlCache = nil
        configuration.timeoutIntervalForRequest = requestTimeout
        // Resource duration is distinct from idle time. Do not impose the old
        // one-hour cutoff on a model whose owner permits a two-hour transfer.
        // URLSession retains its documented seven-day resource ceiling; owners
        // separately enforce component deadlines and cancellation.
        configuration.httpMaximumConnectionsPerHost = 256 // The shared permit pool is the live ceiling.
        session = URLSession(configuration:configuration,delegate:delegate,delegateQueue:nil)
    }
    public func download(source: URL,staging: URL,expectedBytes: Int64? = nil,sha256: String? = nil,maximumBytes: Int64? = nil,trusted: @escaping @Sendable (URL) -> Bool = { ["http","https"].contains($0.scheme ?? "") },metrics: @escaping @Sendable (DownloadMetrics) async -> Void = { _ in },progress: @escaping @Sendable (Int64,Int64?) async -> Void = { _,_ in }) async throws {
        if let maximumBytes, maximumBytes <= 0 || (expectedBytes ?? 0) > maximumBytes { throw DropSpaceError.invalid("Download size limit") }
        if let expectedBytes,expectedBytes < 0 { throw DropSpaceError.invalid("Download expected size") }
        if let sha256,sha256.count != 64 || sha256.range(of:"^[0-9a-fA-F]{64}$",options:.regularExpression) == nil { throw DropSpaceError.invalid("Download SHA-256") }
        try FileManager.default.createDirectory(at:staging.deletingLastPathComponent(),withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        let files = try DownloadFiles(staging: staging)
        try files.write(DownloadIntent(identity: Digests.sha256(Data(source.absoluteString.utf8)), expectedBytes: expectedBytes,
                                       sha256: sha256, maximumBytes: maximumBytes), to: files.staging.appendingPathExtension("request.json"))
        let observation = DownloadObservation(sink: metrics)
        await observation.stage(.queued)
        let transfer = try await budget.transfers.acquire(deadline: .now.advanced(by: .seconds(30 * 60)))
        do {
            let parallel = ParallelHTTPDownload(engine: self, budget: budget, observation: observation)
            if try await !parallel.download(source: source, files: files, expected: expectedBytes, maximum: maximumBytes, trusted: trusted, progress: progress) {
                try await DownloadRetry.run(retrying: { attempt in await observation.stage(.retryWaiting, attempt: attempt) }) {
                    try await self.once(source: source, staging: files.staging, expected: expectedBytes, maximum: maximumBytes, trusted: trusted, observation: observation, progress: progress)
                }
            }
            if let sha256 {
                await observation.stage(.verifying)
                let hashing = Task.detached(priority:.utility) { try Digests.sha256(file:staging) }
                let digest = try await withTaskCancellationHandler { try await hashing.value } onCancel: { hashing.cancel() }
                try Task.checkCancellation()
                if digest.caseInsensitiveCompare(sha256) != .orderedSame {
                    try DownloadStaging.clean(staging)
                    throw DropSpaceError.invalid("Downloaded file checksum mismatch")
                }
            }
            await observation.finish(completed: true)
            await budget.transfers.release(transfer)
        } catch { await observation.finish(completed: false); await budget.transfers.release(transfer); throw error }
    }
    private func metadataURL(_ staging: URL) -> URL { staging.appendingPathExtension("resume.json") }
    private func once(source: URL,staging: URL,expected: Int64?,maximum: Int64?,trusted: @escaping @Sendable (URL)->Bool,observation: DownloadObservation,progress: @escaping @Sendable (Int64,Int64?) async -> Void) async throws {
        try safeStaging(staging)
        let files = try DownloadFiles(staging: staging)
        let identity = Digests.sha256(Data(source.absoluteString.utf8))
        var metadata: HTTPResumeMetadata?
        let meta = metadataURL(staging)
        if let size = try files.size(meta), size < 16384, let bytes = try files.read(meta) { metadata = try? JSONDecoder().decode(HTTPResumeMetadata.self,from:bytes) }
        var offset: Int64 = metadata?.identity == identity && metadata?.etag != nil ? Int64((try? staging.resourceValues(forKeys:[.fileSizeKey]).fileSize) ?? 0) : 0
        if let length = metadata?.length,offset >= length { offset = 0 }
        await observation.stage(.waitingConnection)
        let connection = try await budget.connections.acquire()
        await observation.connected(1); await observation.stage(.connecting)
        do {
            var response = try await request(source:source,offset:offset,etag:metadata?.etag,trusted:trusted)
            if offset > 0,(response.1.statusCode == 416 || response.1.statusCode == 206 && !(metadata?.accepts(offset:offset,etag:response.1.value(forHTTPHeaderField:"ETag"),contentRange:response.1.value(forHTTPHeaderField:"Content-Range"),contentLength:length(response.1)) ?? false)) {
                response.0.task.cancel(); offset = 0
                response = try await request(source:source,offset:0,etag:nil,trusted:trusted)
            }
            let stream = response.0,http = response.1
            defer { stream.task.cancel() }
            if http.statusCode == 200 { offset = 0 }
            else if http.statusCode != 206 || offset == 0 { throw DownloadHTTPFailure(http) }
            if let encoding = http.value(forHTTPHeaderField:"Content-Encoding"),encoding.lowercased() != "identity" { throw DropSpaceError.invalid("Encoded download response") }
            var total: Int64? = offset > 0 ? metadata?.length : length(http)
            if offset > 0,total == nil,let range = http.value(forHTTPHeaderField:"Content-Range") { total = Int64(range.split(separator:"/").last ?? "") }
            if let expected,let total,expected != total { throw DropSpaceError.invalid("Unexpected download size") }
            total = total ?? expected
            if let maximum,(total ?? 0) > maximum || offset > maximum { throw DropSpaceError.limit("Download size limit") }
            if let total { try files.requireSpace(max(0, total - offset)) }
            let fd = open(staging.path,O_WRONLY | O_CREAT | O_NOFOLLOW | O_NONBLOCK | (offset > 0 ? O_APPEND : 0),0o600)
            guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue:errno) ?? .EIO) }
            let handle = FileHandle(fileDescriptor:fd,closeOnDealloc:true)
            defer { try? handle.close() }
            var info = stat()
            guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1 else { throw DropSpaceError.invalid("Unsafe staging file ownership") }
            if offset == 0, ftruncate(fd, 0) != 0 { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
            if offset > 0, info.st_size != offset { throw DropSpaceError.invalid("Staging file changed during resume") }
            let tag = http.value(forHTTPHeaderField:"ETag").flatMap { $0.hasPrefix("W/") ? nil : $0 }
            // The old representation is truncated before recording the new validator.
            try files.write(HTTPResumeMetadata(identity:identity,etag:tag,length:total), to: meta)
            var buffer = Data(); buffer.reserveCapacity(131072)
            var received = offset; var lastReport = ContinuousClock.now
            await observation.begin(bytes: received, total: total)
            await progress(received,total)
            do {
            for try await byte in stream {
                buffer.append(byte)
                if buffer.count >= 131072 {
                    try Task.checkCancellation(); try bounds(received:received,count:buffer.count,total:total,maximum:maximum)
                    try await budget.bandwidth.consume(buffer.count); try handle.write(contentsOf:buffer); received += Int64(buffer.count); buffer.removeAll(keepingCapacity:true)
                    await observation.bytes(received, total: total)
                    if lastReport.duration(to:.now) >= .milliseconds(100) { await progress(received,total); lastReport = .now }
                }
            }
            if !buffer.isEmpty { try bounds(received:received,count:buffer.count,total:total,maximum:maximum); try await budget.bandwidth.consume(buffer.count); try handle.write(contentsOf:buffer); received += Int64(buffer.count) }
            try handle.synchronize(); try Task.checkCancellation()
            if let total,total != received { throw URLError(.networkConnectionLost) }
            await observation.bytes(received, total: total); await progress(received,total)
            } catch { await observation.bytes(received, total: total); await progress(received,total); throw error }
            await observation.connected(-1); await budget.connections.release(connection)
        } catch { await observation.connected(-1); await budget.connections.release(connection); throw error }
    }
    func request(source: URL,offset: Int64,etag: String?,end: Int64? = nil,trusted: @escaping @Sendable (URL)->Bool) async throws -> (URLSession.AsyncBytes,HTTPURLResponse) {
        var url = source
        for _ in 0..<6 {
            try Task.checkCancellation()
            guard trusted(url),url.user == nil,url.password == nil,["http","https"].contains(url.scheme?.lowercased() ?? ""),url.host != nil else { throw DropSpaceError.invalid("Untrusted download destination") }
            var request = URLRequest(url:url); request.setValue("identity",forHTTPHeaderField:"Accept-Encoding")
            if let userAgent { request.setValue(userAgent, forHTTPHeaderField: "User-Agent") }
            if offset > 0 || end != nil { request.setValue("bytes=\(offset)-\(end.map(String.init) ?? "")",forHTTPHeaderField:"Range"); if let etag { request.setValue(etag,forHTTPHeaderField:"If-Range") } }
            let (bytes,response) = try await session.bytes(for:request)
            guard let http = response as? HTTPURLResponse else { bytes.task.cancel(); throw DropSpaceError.invalid("Non-HTTP download response") }
            if [301,302,303,307,308].contains(http.statusCode) {
                bytes.task.cancel()
                guard let location = http.value(forHTTPHeaderField:"Location"),let next = URL(string:location,relativeTo:url)?.absoluteURL else { throw DropSpaceError.invalid("Missing download redirect") }
                url = next; continue
            }
            return (bytes,http)
        }
        throw DropSpaceError.invalid("Too many download redirects")
    }
    private func safeStaging(_ url: URL) throws { let value = try? url.resourceValues(forKeys:[.isSymbolicLinkKey,.isDirectoryKey]); if value?.isSymbolicLink == true || value?.isDirectory == true { throw DropSpaceError.invalid("Unsafe staging file") } }
    private func length(_ response: HTTPURLResponse) -> Int64? { response.value(forHTTPHeaderField:"Content-Length").flatMap(Int64.init) }
    private func bounds(received: Int64,count: Int,total: Int64?,maximum: Int64?) throws {
        if let total,Int64(count) > total-received { throw DropSpaceError.limit("Download exceeds declared length") }
        if let maximum,Int64(count) > maximum-received { throw DropSpaceError.limit("Download exceeds allowed size") }
    }
}
