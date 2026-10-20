import Foundation

public enum QuickActionRules {
    /// Production registration order; enum-only IDs stay decodable but are not
    /// offered as implemented action services in the preferences editor.
    public static let registeredLocalActions: [QuickAction] = [.hashSha256, .resizeImage, .convertImage, .stripMetadata, .compressZip, .generateQr]
    public static func isImage(_ item: DropItem) -> Bool {
        item.kind == .image || ["png","jpg","jpeg","gif","bmp","tif","tiff","webp","ico","heic"].contains(URL(fileURLWithPath:item.file?.path ?? item.payloadPath ?? "").pathExtension.lowercased())
    }
    public static func profile(_ items: [DropItem]) -> String? {
        let profiles = Set(items.map { item in isImage(item) ? "image" : item.kind == .url ? "url" : [.text,.code,.color].contains(item.kind) ? "text" : "file" })
        return profiles.count == 1 ? profiles.first : nil
    }
    public static func partition(available: [QuickAction],items: [DropItem],preferences: [String:QuickActionPreference]) -> (primary:[QuickAction],more:[QuickAction]) {
        var seen = Set<QuickAction>()
        let unique = available.filter { seen.insert($0).inserted }
        let profile = profile(items), preference = profile.flatMap { preferences[$0] } ?? .init()
        if preference.automatic {
            let priority: [QuickAction] = profile == "image" ? [.resizeImage,.convertImage,.stripMetadata] : []
            let ordered = priority.filter(unique.contains) + unique.filter { !priority.contains($0) }
            return (Array(ordered.prefix(3)),Array(ordered.dropFirst(3)))
        }
        let primary = preference.actions.compactMap { $0 }.filter(unique.contains)
        return (primary,unique.filter { !primary.contains($0) })
    }
    /// Only the six production-registered Beta7 local actions. Other enum values
    /// remain configurable but are not invented as registry capabilities.
    public static func localCapabilities(_ items: [DropItem]) -> [QuickAction] {
        guard !items.isEmpty else { return [] }
        let paths = items.allSatisfy { $0.status == .available && ($0.file != nil || $0.payloadPath != nil) }
        var result: [QuickAction] = []
        if items.count == 1,paths,items[0].kind != .folder { result.append(.hashSha256) }
        if items.count == 1,paths,isImage(items[0]) { result += [.resizeImage,.convertImage,.stripMetadata] }
        if paths { result.append(.compressZip) }
        if items.count == 1,let text = items[0].text,ImageRules.canEncodeQR(text) { result.append(.generateQr) }
        return result
    }
}
