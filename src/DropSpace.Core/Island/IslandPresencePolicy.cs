using DropSpace.Core.Overlay;

namespace DropSpace.Core.Island;

public enum IslandContentKind { None, Files, Music, Notification, Volume }
public enum IslandPage { Files, Music, Widgets }
public sealed record IslandPresenceInput(OverlaySnapshot Files, bool MediaPlaying, DateTimeOffset? MediaGraceUntil,
    bool ManualOpen, bool Expanded, IslandPage Page, DateTimeOffset? NotificationUntil, DateTimeOffset? VolumeUntil);
public sealed record IslandExperienceSnapshot(OverlayState State, IslandContentKind CompactContent, IslandPage Page,
    bool MediaPresent, DateTimeOffset? NextDeadline, long Revision);

public static class IslandPresencePolicy
{
    public static IslandExperienceSnapshot Resolve(IslandPresenceInput input, DateTimeOffset now, long revision)
    {
        var media = input.MediaPlaying || input.MediaGraceUntil > now;
        var notification = input.NotificationUntil > now;
        var volume = input.VolumeUntil > now;
        var dragging = input.Files.State is OverlayState.DragApproaching or OverlayState.DragReady || input.Files.ExpandedDropActive;
        var files = input.Files.TemporaryItemCount > 0 && input.Files.State is not (OverlayState.Hidden or OverlayState.Dismissing);
        var deadlines = new[] { input.MediaGraceUntil, input.NotificationUntil, input.VolumeUntil };
        var next = deadlines.Where(value => value > now).Min();
        var content = dragging ? IslandContentKind.Files : notification ? IslandContentKind.Notification : volume ? IslandContentKind.Volume :
            media ? IslandContentKind.Music : files || input.ManualOpen ? IslandContentKind.Files : IslandContentKind.None;
        var state = dragging ? input.Files.State : content == IslandContentKind.None ? OverlayState.Hidden :
            !notification && !volume && (input.Expanded || input.ManualOpen) ? OverlayState.Expanded : OverlayState.Compact;
        return new(state, content, input.Page, media, next, revision);
    }
}
