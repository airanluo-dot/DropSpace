import Foundation
import IOKit.ps
import Network
import Observation
import Darwin

@MainActor @Observable public final class SystemMetrics {
    public private(set) var cpuPercent: Double?
    public private(set) var memoryUsed: UInt64 = 0
    public private(set) var memoryTotal = ProcessInfo.processInfo.physicalMemory
    public private(set) var batteryPercent: Int?
    public private(set) var charging = false
    public private(set) var diskAvailable: Int64?
    public private(set) var online = false
    public private(set) var interface = ""
    public var uptime: TimeInterval { ProcessInfo.processInfo.systemUptime }
    private var timer: Timer?
    private var previousCPU: (UInt64,UInt64)?
    private let network = NWPathMonitor()
    public init() {}
    public func start() {
        guard timer == nil else { return }
        network.pathUpdateHandler = { [weak self] path in
            let online = path.status == .satisfied
            let interface = path.usesInterfaceType(.wifi) ? "Wi-Fi" : path.usesInterfaceType(.wiredEthernet) ? "Ethernet" : path.usesInterfaceType(.cellular) ? "Cellular" : "Other"
            Task { @MainActor in self?.online = online; self?.interface = interface }
        }
        network.start(queue: DispatchQueue(label: "DropSpace.network-status"))
        sample()
        timer = Timer.scheduledTimer(withTimeInterval: 2,repeats: true) { [weak self] _ in Task { @MainActor in self?.sample() } }
    }
    public func stop() { timer?.invalidate(); timer = nil; network.cancel() }
    private func sample() {
        let host = mach_host_self(); defer { mach_port_deallocate(mach_task_self_,host) }
        var cpu = host_cpu_load_info(); var count = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info>.stride / MemoryLayout<integer_t>.stride)
        let result = withUnsafeMutablePointer(to: &cpu) { ptr in ptr.withMemoryRebound(to: integer_t.self,capacity: Int(count)) { host_statistics(host,HOST_CPU_LOAD_INFO,$0,&count) } }
        if result == KERN_SUCCESS {
            let user = UInt64(cpu.cpu_ticks.0), system = UInt64(cpu.cpu_ticks.1), idle = UInt64(cpu.cpu_ticks.2), nice = UInt64(cpu.cpu_ticks.3)
            let total = user + system + idle + nice
            if let old = previousCPU, total > old.0, idle >= old.1 { cpuPercent = max(0,min(100,100 * (1 - Double(idle - old.1) / Double(total - old.0)))) }
            previousCPU = (total,idle)
        }
        var vm = vm_statistics64(); var vmCount = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.stride / MemoryLayout<integer_t>.stride)
        let vmResult = withUnsafeMutablePointer(to: &vm) { ptr in ptr.withMemoryRebound(to: integer_t.self,capacity: Int(vmCount)) { host_statistics64(host,HOST_VM_INFO64,$0,&vmCount) } }
        if vmResult == KERN_SUCCESS { memoryUsed = min(memoryTotal,(UInt64(vm.active_count) + UInt64(vm.wire_count) + UInt64(vm.compressor_page_count)) * UInt64(getpagesize())) }
        batteryPercent = nil; charging = false
        if let blob = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(), let sources = IOPSCopyPowerSourcesList(blob)?.takeRetainedValue() as? [CFTypeRef] {
            for source in sources {
                guard let d = IOPSGetPowerSourceDescription(blob,source)?.takeUnretainedValue() as? [String:Any], let current = d[kIOPSCurrentCapacityKey] as? Int, let maximum = d[kIOPSMaxCapacityKey] as? Int, maximum > 0 else { continue }
                batteryPercent = min(100,max(0,current * 100 / maximum)); charging = d[kIOPSIsChargingKey] as? Bool ?? false; break
            }
        }
        diskAvailable = (try? FileManager.default.attributesOfFileSystem(forPath: NSHomeDirectory())[.systemFreeSize] as? NSNumber)?.int64Value
    }
}
