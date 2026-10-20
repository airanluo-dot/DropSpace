import Foundation

public enum LyricsReloadPolicy {
    /// Frozen LyricsReloadPolicy.cs: visual/cache preferences do not restart a
    /// recording lookup. The independent translation target is in LyricsQuery.
    public static func requiresReload(_ previous: LyricsSettings?, _ current: LyricsSettings) -> Bool {
        guard let previous else { return true }
        return previous.enabled != current.enabled || previous.localOnly != current.localOnly || previous.provider != current.provider ||
            previous.backupProvider != current.backupProvider || previous.searchRemainingProviders != current.searchRemainingProviders ||
            previous.localLrcDirectory != current.localLrcDirectory || previous.aiTranslationEnabled != current.aiTranslationEnabled ||
            previous.aiModelID != current.aiModelID || previous.gpuAccelerationEnabled != current.gpuAccelerationEnabled || previous.gpuBackend != current.gpuBackend
    }
}
