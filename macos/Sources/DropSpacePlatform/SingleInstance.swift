import AppKit
import Darwin
import DropSpaceInfrastructure

@MainActor public final class SingleInstance {
    private var descriptor: Int32 = -1
    private var observer: NSObjectProtocol?
    public init() {}
    public func acquire(directory: URL,onActivate: @escaping @MainActor () -> Void) throws -> Bool {
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        let fd = open(directory.appendingPathComponent("instance.lock").path,O_CREAT | O_RDWR | O_NOFOLLOW | O_CLOEXEC,0o600)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue:errno) ?? .EIO) }
        let name = Notification.Name("io.github.airanluo-dot.DropSpace.activate." + Digests.sha256(Data(directory.standardizedFileURL.path.utf8)))
        guard flock(fd,LOCK_EX | LOCK_NB) == 0 else {
            let code = errno; close(fd)
            guard code == EWOULDBLOCK else { throw POSIXError(POSIXErrorCode(rawValue:code) ?? .EIO) }
            DistributedNotificationCenter.default().postNotificationName(name,object:nil,userInfo:nil,deliverImmediately:true)
            return false
        }
        descriptor = fd
        observer = DistributedNotificationCenter.default().addObserver(forName:name,object:nil,queue:.main) { _ in Task { @MainActor in onActivate() } }
        return true
    }
    public func release() {
        if let observer { DistributedNotificationCenter.default().removeObserver(observer) }; observer = nil
        if descriptor >= 0 { flock(descriptor,LOCK_UN); close(descriptor); descriptor = -1 }
    }
}
