namespace DropSpace.Core.SystemActivities;

public sealed record SystemNotificationSnapshot(
    uint Id,
    string SourceAppId,
    string SourceDisplayName,
    string Title,
    string Body,
    byte[]? Icon,
    DateTimeOffset ReceivedAt);

public interface INotificationActivityService : IAsyncDisposable
{
    event EventHandler<SystemNotificationSnapshot>? NotificationReceived;

    string AccessState { get; }

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<string> RequestAccessAsync(CancellationToken cancellationToken = default);
}

public sealed record VolumeActivitySnapshot(
    int Percent,
    bool IsMuted,
    DateTimeOffset ChangedAt);

public interface IVolumeActivityService : IAsyncDisposable
{
    event EventHandler<VolumeActivitySnapshot>? Changed;

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
