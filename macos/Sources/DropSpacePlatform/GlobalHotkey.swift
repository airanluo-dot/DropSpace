import AppKit
import Carbon
import DropSpaceCore

@MainActor public final class GlobalHotkey {
    private var reference: EventHotKeyRef?
    private var handler: EventHandlerRef?
    public var action: (() -> Void)?
    public init() {}
    public func register(_ gesture: String) throws {
        guard let parsed = Hotkey.parse(gesture), let code = Self.keyCodes[parsed.key] else { throw DropSpaceError.invalid("Unsupported shortcut key") }
        if handler == nil {
            var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard),eventKind: UInt32(kEventHotKeyPressed))
            let context = Unmanaged.passUnretained(self).toOpaque()
            let result = InstallEventHandler(GetApplicationEventTarget(),{ _,_,context in
                guard let context else { return OSStatus(eventNotHandledErr) }
                MainActor.assumeIsolated { Unmanaged<GlobalHotkey>.fromOpaque(context).takeUnretainedValue().action?() }
                return noErr
            },1,&type,context,&handler)
            guard result == noErr else { throw DropSpaceError.unavailable("Shortcut handler unavailable: \(result)") }
        }
        var modifiers: UInt32 = 0
        for modifier in parsed.modifiers {
            let mask: UInt32 = switch modifier { case "Cmd": UInt32(cmdKey); case "Ctrl": UInt32(controlKey); case "Alt": UInt32(optionKey); default: UInt32(shiftKey) }
            modifiers |= mask
        }
        unregister()
        let result = RegisterEventHotKey(code,modifiers,EventHotKeyID(signature: 0x44535043,id: 1),GetApplicationEventTarget(),0,&reference)
        guard result == noErr else { throw DropSpaceError.unavailable("Shortcut is reserved or already in use (\(result))") }
    }
    public func unregister() { if let reference { UnregisterEventHotKey(reference) }; reference = nil }
    public func stop() { unregister(); if let handler { RemoveEventHandler(handler) }; handler = nil }
    public static let keyCodes: [String: UInt32] = ["A":0,"S":1,"D":2,"F":3,"H":4,"G":5,"Z":6,"X":7,"C":8,"V":9,"B":11,"Q":12,"W":13,"E":14,"R":15,"Y":16,"T":17,"1":18,"2":19,"3":20,"4":21,"6":22,"5":23,"9":25,"7":26,"8":28,"0":29,"O":31,"U":32,"I":34,"P":35,"L":37,"J":38,"K":40,"N":45,"M":46,",":43,"Space":49,"F1":122,"F2":120,"F3":99,"F4":118,"F5":96,"F6":97,"F7":98,"F8":100,"F9":101,"F10":109,"F11":103,"F12":111,"F13":105,"F14":107,"F15":113,"F16":106,"F17":64,"F18":79,"F19":80,"F20":90]
}
