using DropSpace.Core.Models;

namespace DropSpace.Core.Lyrics;

public static class LyricsReloadPolicy
{
    public static bool RequiresReload(AppSettings? previous, AppSettings current)
    {
        if (previous is null || previous.Language != current.Language) return true;
        var left = previous.Lyrics;
        var right = current.Lyrics;
        return left.Enabled != right.Enabled || left.Mode != right.Mode || left.Provider != right.Provider ||
            left.BackupProvider != right.BackupProvider || left.SearchRemainingProviders != right.SearchRemainingProviders ||
            left.SelectionMode != right.SelectionMode ||
            left.LocalLrcDirectory != right.LocalLrcDirectory || left.AiTranslationEnabled != right.AiTranslationEnabled ||
            left.AiModelId != right.AiModelId || left.AiSelectionModelId != right.AiSelectionModelId ||
            left.AiLyricsGpuAccelerationEnabled != right.AiLyricsGpuAccelerationEnabled;
    }
}
