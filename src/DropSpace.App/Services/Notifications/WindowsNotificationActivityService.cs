using System.Runtime.InteropServices;
using DropSpace.Core.SystemActivities;
using Microsoft.Extensions.Logging;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DropSpace.App.Services.Notifications;

public sealed class WindowsNotificationActivityService : INotificationActivityService
{
    private readonly ILogger<WindowsNotificationActivityService> _logger;
    private UserNotificationListener? _listener;
    private bool _enabled;
    private bool _disposed;

    public WindowsNotificationActivityService(ILogger<WindowsNotificationActivityService> logger) => _logger = logger;

    public event EventHandler<SystemNotificationSnapshot>? NotificationReceived;

    public string AccessState { get; private set; } = "Unspecified";

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled == enabled && (!enabled || _listener is not null)) return;
        _enabled = enabled;
        if (!enabled)
        {
            if (_listener is not null) _listener.NotificationChanged -= OnNotificationChanged;
            _listener = null;
            AccessState = "Disabled";
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _listener = UserNotificationListener.Current;
            AccessState = _listener.GetAccessStatus().ToString();
            if (_listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed)
            {
                _listener.NotificationChanged += OnNotificationChanged;
                await PublishLatestAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            AccessState = $"Unavailable: {exception.GetType().Name}";
            _logger.LogWarning(exception, "Windows notification listener is unavailable.");
            _listener = null;
        }
    }

    public async Task<string> RequestAccessAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null) _listener = UserNotificationListener.Current;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var status = await _listener.RequestAccessAsync();
            AccessState = status.ToString();
            if (status == UserNotificationListenerAccessStatus.Allowed && _enabled)
            {
                _listener.NotificationChanged -= OnNotificationChanged;
                _listener.NotificationChanged += OnNotificationChanged;
                await PublishLatestAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            AccessState = $"Unavailable: {exception.GetType().Name}";
        }

        return AccessState;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_listener is not null) _listener.NotificationChanged -= OnNotificationChanged;
            _listener = null;
        }

        return ValueTask.CompletedTask;
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args) =>
        _ = PublishLatestSafelyAsync();

    private async Task PublishLatestSafelyAsync()
    {
        try { await PublishLatestAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        { _logger.LogDebug(exception, "Notification listener refresh failed safely."); }
    }

    private async Task PublishLatestAsync(CancellationToken cancellationToken)
    {
        if (!_enabled || _listener is null || _listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed) return;
        var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        var notification = notifications.LastOrDefault();
        if (notification is null) return;
        var texts = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
        var values = texts?.Select(text => text.Text).Where(text => !string.IsNullOrWhiteSpace(text)).Take(3).ToArray() ?? [];
        var title = values.ElementAtOrDefault(0) ?? string.Empty;
        var body = string.Join(Environment.NewLine, values.Skip(1));
        var app = notification.AppInfo;
        NotificationReceived?.Invoke(this, new SystemNotificationSnapshot(
            notification.Id,
            app?.AppUserModelId ?? string.Empty,
            app?.DisplayInfo?.DisplayName ?? app?.AppUserModelId ?? "Windows notification",
            title,
            body,
            null,
            DateTimeOffset.UtcNow));
        cancellationToken.ThrowIfCancellationRequested();
    }
}
