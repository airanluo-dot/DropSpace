using System.ComponentModel;
using System.Threading.Channels;
using DropSpace.App.Services.Notifications;
using DropSpace.App.Services.Volume;
using DropSpace.App.ViewModels;
using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.SystemActivities;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services;

public sealed class SystemActivityExperienceService(
    MainViewModel main, SystemActivityViewModel view, IslandExperienceCoordinator island,
    WindowsNotificationActivityService notifications, WindowsVolumeActivityService volume,
    DispatcherQueue dispatcher, ILogger<SystemActivityExperienceService> logger) : IAsyncDisposable
{
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private Task _worker = Task.CompletedTask;
    private SystemActivitySettings _settings = new();
    private SystemNotificationSnapshot? _pendingNotification, _notification;
    private VolumeActivitySnapshot? _pendingVolume, _volume;
    private int _dispatchPending;
    private bool _initialized, _disposed;

    public void Initialize()
    {
        if (_initialized || _disposed) return;
        _initialized = true; _settings = main.Settings.SystemActivities;
        main.PropertyChanged += OnSettings;
        notifications.NotificationReceived += OnNotification; volume.Changed += OnVolume;
        island.Changed += OnIsland;
        _worker = RunAsync(); _changes.Writer.TryWrite(true);
    }
    private void OnSettings(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.Settings)) return;
        var next = main.Settings.SystemActivities;
        if (next == _settings) return;
        Volatile.Write(ref _settings, next); _changes.Writer.TryWrite(true);
        if (!next.ShowWindowsNotifications) { _notification = null; island.ClearNotifications(); }
        if (!next.ShowVolumeChanges) { _volume = null; island.ClearVolume(); }
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var _ in _changes.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var settings = Volatile.Read(ref _settings);
                try
                {
                    await notifications.SetEnabledAsync(settings.ShowWindowsNotifications, _stop.Token).ConfigureAwait(false);
                    await volume.SetEnabledAsync(settings.ShowVolumeChanges, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogWarning("System activity observer unavailable ({Category}).", exception.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private void OnNotification(object? sender, SystemNotificationSnapshot value) { Volatile.Write(ref _pendingNotification, value); Schedule(); }
    private void OnVolume(object? sender, VolumeActivitySnapshot value) { Volatile.Write(ref _pendingVolume, value); Schedule(); }
    private void Schedule()
    {
        if (_disposed || Interlocked.Exchange(ref _dispatchPending, 1) != 0) return;
        if (!dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _dispatchPending, 0);
            if (_disposed) return;
            var now = DateTimeOffset.UtcNow;
            var notification = Interlocked.Exchange(ref _pendingNotification, null);
            var changedVolume = Interlocked.Exchange(ref _pendingVolume, null);
            if (notification is not null && notification.ReceivedAt + SystemActivityPolicy.NotificationLifetime > now && main.Settings.SystemActivities.ShowWindowsNotifications)
            { _notification = notification; island.Notify(); }
            if (changedVolume is not null && changedVolume.ReceivedAt + SystemActivityPolicy.VolumeLifetime > now && main.Settings.SystemActivities.ShowVolumeChanges)
            { _volume = changedVolume; island.VolumeChanged(); }
        })) Interlocked.Exchange(ref _dispatchPending, 0);
    }
    private void OnIsland(object? sender, IslandExperienceSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        if (_notification?.ReceivedAt + SystemActivityPolicy.NotificationLifetime <= now) _notification = null;
        if (_volume?.ReceivedAt + SystemActivityPolicy.VolumeLifetime <= now) _volume = null;
        if (snapshot.CompactContent == IslandContentKind.Notification && _notification is not null) view.Show(_notification);
        else if (snapshot.CompactContent == IslandContentKind.Volume && _volume is not null) view.Show(_volume);
        else view.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        main.PropertyChanged -= OnSettings; island.Changed -= OnIsland;
        notifications.NotificationReceived -= OnNotification; volume.Changed -= OnVolume;
        _stop.Cancel(); _changes.Writer.TryComplete(); await _worker.ConfigureAwait(false);
        await notifications.SetEnabledAsync(false).ConfigureAwait(false);
        await volume.SetEnabledAsync(false).ConfigureAwait(false);
        _pendingNotification = _notification = null; _pendingVolume = _volume = null;
        _stop.Dispose();
    }
}
