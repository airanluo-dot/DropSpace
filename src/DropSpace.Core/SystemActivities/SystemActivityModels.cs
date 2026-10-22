namespace DropSpace.Core.SystemActivities;

public enum NotificationAccessState { Disabled, Unspecified, Allowed, Denied, Unavailable }
public sealed record SystemNotificationSnapshot(uint Id, string SourceName, string Title, string Body, DateTimeOffset ReceivedAt);
public sealed record VolumeActivitySnapshot(int Percent, bool Muted, DateTimeOffset ReceivedAt);

public static class SystemActivityPolicy
{
    // Retain the implementation and saved preferences while this release omits
    // the system-activity experience from navigation and background startup.
    public static bool IsAvailable => false;

    public static readonly TimeSpan NotificationLifetime = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan VolumeLifetime = TimeSpan.FromMilliseconds(1800);
}
