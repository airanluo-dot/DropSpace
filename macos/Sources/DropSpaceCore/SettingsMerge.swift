import Foundation

public enum SettingsMerge {
    /// Merge edits from an older form snapshot into the latest saved preferences.
    public static func merge(baseline: AppSettings,requested: AppSettings,latest: AppSettings) throws -> AppSettings {
        func object(_ settings: AppSettings) throws -> [String:Any] { try JSONSerialization.jsonObject(with:JSONEncoder().encode(settings)) as! [String:Any] }
        func equal(_ a: Any?,_ b: Any?) -> Bool {
            guard let a,let b else { return a == nil && b == nil }
            return (a as AnyObject).isEqual(b)
        }
        func apply(_ base: [String:Any],_ requested: [String:Any],_ latest: [String:Any]) -> [String:Any] {
            var result = latest
            for key in Set(base.keys).union(requested.keys) where !equal(base[key],requested[key]) {
                if let b = base[key] as? [String:Any],let r = requested[key] as? [String:Any],let l = latest[key] as? [String:Any] { result[key] = apply(b,r,l) }
                else { result[key] = requested[key] }
            }
            return result
        }
        let value = try apply(object(baseline),object(requested),object(latest))
        return try JSONDecoder().decode(AppSettings.self,from:JSONSerialization.data(withJSONObject:value)).validated()
    }
}
