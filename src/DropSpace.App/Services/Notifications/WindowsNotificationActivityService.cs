using System.Runtime.InteropServices;
using System.Threading.Channels;
using DropSpace.Core.SystemActivities;
using Microsoft.Extensions.Logging;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DropSpace.App.Services.Notifications;

public sealed class WindowsNotificationActivityService(ILogger<WindowsNotificationActivityService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private UserNotificationListener? _listener;
    private Channel<uint>? _events;
    private Task _consumer = Task.CompletedTask;
    private CancellationTokenSource? _stop;
    private bool _disposed;
    public NotificationAccessState AccessState { get; private set; } = NotificationAccessState.Disabled;
    public event EventHandler<SystemNotificationSnapshot>? NotificationReceived;

    // Called only by the explicit permission action on the UI thread.
    public async Task<NotificationAccessState> RequestAccessAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var status = await UserNotificationListener.Current.RequestAccessAsync().AsTask(cancellationToken);
            return ConvertStatus(status);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            logger.LogDebug("Notification permission unavailable ({Category}).", exception.GetType().Name);
            return NotificationAccessState.Unavailable;
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            if (!enabled) return;
            try
            {
                var listener = UserNotificationListener.Current;
                AccessState = ConvertStatus(listener.GetAccessStatus());
                if (AccessState != NotificationAccessState.Allowed) return;
                _stop = new();
                _events = Channel.CreateBounded<uint>(new BoundedChannelOptions(32)
                { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
                _listener = listener;
                listener.NotificationChanged += OnChanged;
                _consumer = ConsumeAsync(listener, _events.Reader, _stop.Token);
                // Never replay notification history when the option is enabled.
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                await StopCoreAsync().ConfigureAwait(false);
                AccessState = NotificationAccessState.Unavailable;
                logger.LogDebug("Notification listener unavailable ({Category}).", exception.GetType().Name);
            }
        }
        finally { _lifecycle.Release(); }
    }

    private void OnChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Added) _events?.Writer.TryWrite(args.UserNotificationId);
    }

    private async Task ConsumeAsync(UserNotificationListener listener, ChannelReader<uint> reader, CancellationToken token)
    {
        var recent = new Queue<uint>();
        try
        {
            await foreach (var id in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (recent.Contains(id)) continue;
                recent.Enqueue(id);
                if (recent.Count > 64) recent.Dequeue();
                try
                {
                    if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
                    { AccessState = NotificationAccessState.Denied; continue; }
                    var notification = listener.GetNotification(id);
                    if (notification is null) continue;
                    var text = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                    var snapshot = new SystemNotificationSnapshot(id, Bound(notification.AppInfo?.DisplayInfo?.DisplayName, 256),
                        Bound(text?.FirstOrDefault()?.Text, 512),
                        string.Join(Environment.NewLine, text?.Skip(1).Take(2).Select(item => Bound(item.Text, 1024)) ?? []), DateTimeOffset.UtcNow);
                    token.ThrowIfCancellationRequested();
                    if (NotificationReceived is not { } handlers) continue;
                    foreach (EventHandler<SystemNotificationSnapshot> handler in handlers.GetInvocationList())
                        try { handler(this, snapshot); }
                        catch (Exception exception) { logger.LogDebug("Notification subscriber failed ({Category}).", exception.GetType().Name); }
                }
                catch (Exception exception) when (IsUnavailable(exception))
                { logger.LogDebug("Notification read unavailable ({Category}).", exception.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task StopCoreAsync()
    {
        if (_listener is { } listener)
        {
            try { listener.NotificationChanged -= OnChanged; }
            catch (Exception exception) when (IsUnavailable(exception))
            { logger.LogDebug("Notification detach unavailable ({Category}).", exception.GetType().Name); }
        }
        _listener = null;
        _stop?.Cancel(); _events?.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
        _stop?.Dispose(); _stop = null; _events = null;
        AccessState = NotificationAccessState.Disabled;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private static string Bound(string? text, int length) => string.IsNullOrEmpty(text) ? string.Empty : text[..Math.Min(text.Length, length)];
    private static bool IsUnavailable(Exception exception) => exception is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException;
    private static NotificationAccessState ConvertStatus(UserNotificationListenerAccessStatus status) => status switch
    {
        UserNotificationListenerAccessStatus.Allowed => NotificationAccessState.Allowed,
        UserNotificationListenerAccessStatus.Denied => NotificationAccessState.Denied,
        _ => NotificationAccessState.Unspecified,
    };
}
