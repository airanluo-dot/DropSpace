using DropSpace.Core.Overlay;

namespace DropSpace.Core.Island;

public enum IslandContentKind { None, Files, Music, Notification, Volume }
public enum IslandPresentationVariant { Idle, EmptyWake, SingleFile, MultipleFiles, Music, Activity }
public enum IslandPage { Widgets, Music, Files, Clipboard }
public sealed record IslandPresenceInput(OverlaySnapshot Files, bool MediaPlaying, DateTimeOffset? MediaGraceUntil,
    bool ManualOpen, bool Expanded, IslandPage Page, DateTimeOffset? NotificationUntil, DateTimeOffset? VolumeUntil, bool RetainMedia = false, IslandPage? ContentPage = null);
public sealed record IslandExperienceSnapshot(OverlayState State, IslandContentKind CompactContent, IslandPage Page,
    bool MediaPresent, DateTimeOffset? NextDeadline, long Revision, IslandPresentationVariant Variant = IslandPresentationVariant.Idle, bool PendingHide = false);

public static class IslandPresencePolicy
{
    public static IslandExperienceSnapshot Resolve(IslandPresenceInput input, DateTimeOffset now, long revision)
    {
        var media = input.MediaPlaying || input.RetainMedia || input.MediaGraceUntil > now;
        var notification = input.NotificationUntil > now;
        var volume = input.VolumeUntil > now;
        var dragging = input.Files.State is OverlayState.DragApproaching or OverlayState.DragReady || input.Files.ExpandedDropActive;
        var files = input.Files.TemporaryItemCount > 0;
        var deadlines = new[] { input.MediaGraceUntil, input.NotificationUntil, input.VolumeUntil };
        var next = deadlines.Where(value => value > now).Min();
        var fileContent = files && input.ContentPage == IslandPage.Files;
        var content = dragging ? IslandContentKind.Files : notification ? IslandContentKind.Notification : volume ? IslandContentKind.Volume :
            fileContent ? IslandContentKind.Files : media ? IslandContentKind.Music :
            files || input.ManualOpen ? IslandContentKind.Files : IslandContentKind.None;
        var state = dragging ? input.Files.State : content == IslandContentKind.None ? OverlayState.Hidden :
            !notification && !volume && (input.Expanded || input.ManualOpen) ? OverlayState.Expanded : OverlayState.Compact;
        return new(state, content, dragging ? IslandPage.Files : input.Page, media, next, revision);
    }
}
