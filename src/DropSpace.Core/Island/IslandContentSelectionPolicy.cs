using DropSpace.Core.Models;

namespace DropSpace.Core.Island;

/// <summary>Defaults are recomputed only as availability changes; explicit page choices
/// are owned by the experience coordinator and are independent of media refresh cadence.</summary>
public static class IslandContentSelectionPolicy
{
    public static IslandPage ResolveDefault(IslandContentPriority priority, bool filesAvailable,
        bool mediaAvailable, bool mediaPlaying)
    {
        if (filesAvailable && (!mediaAvailable || !mediaPlaying || priority == IslandContentPriority.TemporarySpace))
            return IslandPage.Files;
        return mediaAvailable ? IslandPage.Music : IslandPage.Files;
    }
}
