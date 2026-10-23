using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DropSpace.Core.SystemActivities;
using DropSpace.App.Services.Audio;
using Microsoft.Extensions.Logging;
using static DropSpace.App.Services.Volume.VolumeInterop;

namespace DropSpace.App.Services.Volume;

public sealed class WindowsVolumeActivityService(ILogger<WindowsVolumeActivityService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly HashSet<object> _controlOwners = new();
    private bool _activityEnabled;
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;
    public bool IsAvailable { get; private set; }
    public VolumeActivitySnapshot? Current { get; private set; }
    public event EventHandler<VolumeActivitySnapshot>? Changed;
    // Unlike Changed, includes attachment/loss so controls can follow the current endpoint
    // without manufacturing an island volume-change notification.
    public event EventHandler? SnapshotChanged;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activityEnabled = enabled;
            await ApplyObservationAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task SetControlActiveAsync(object owner, bool active, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed && !active) return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (active) _controlOwners.Add(owner); else _controlOwners.Remove(owner);
            await ApplyObservationAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ApplyObservationAsync(CancellationToken cancellationToken)
    {
            var enabled = _activityEnabled || _controlOwners.Count > 0;
            if (enabled && _stop is not null && !_worker.IsCompleted) return;
            await StopCoreAsync().ConfigureAwait(false);
            if (!enabled) return;
            _stop = new();
            var token = _stop.Token;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _worker = Task.Factory.StartNew(() => Observe(token, ready), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try { await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch { await StopCoreAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Controls the current Windows default render endpoint, never an individual player.</summary>
    public Task<VolumeActivitySnapshot> SetMasterVolumeAsync(double normalized, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(normalized) || normalized is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(normalized));
        return SetEndpointAsync(endpoint =>
        {
            var context = Guid.Empty;
            endpoint.SetMasterVolumeLevelScalar((float)normalized, context);
            if (normalized > 0) endpoint.SetMute(false, context);
        }, cancellationToken);
    }

    public Task<VolumeActivitySnapshot> SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        SetEndpointAsync(endpoint => { var context = Guid.Empty; endpoint.SetMute(muted, context); }, cancellationToken);

    private async Task<VolumeActivitySnapshot> SetEndpointAsync(Action<IAudioEndpointVolume> apply, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var initialized = ProcessLoopbackInterop.CoInitializeEx(0, 0) >= 0;
                IMMDeviceEnumerator? enumerator = null; IMMDevice? device = null; IAudioEndpointVolume? endpoint = null;
                try
                {
                    if (!initialized) throw new COMException("Volume apartment initialization failed.");
                    enumerator = (IMMDeviceEnumerator)(object)new DeviceEnumerator();
                    enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                    device.Activate(typeof(IAudioEndpointVolume).GUID, 0x17, 0, out var instance);
                    endpoint = (IAudioEndpointVolume)instance;
                    apply(endpoint);
                    endpoint.GetMasterVolumeLevelScalar(out var actual); endpoint.GetMute(out var muted);
                    return new VolumeActivitySnapshot((int)Math.Round(Math.Clamp(actual, 0, 1) * 100), muted, DateTimeOffset.UtcNow);
                }
                finally
                {
                    ProcessLoopbackInterop.Release(endpoint); ProcessLoopbackInterop.Release(device); ProcessLoopbackInterop.Release(enumerator);
                    if (initialized) ProcessLoopbackInterop.CoUninitialize();
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    private void PublishSnapshot()
    {
        if (SnapshotChanged is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); }
            catch (Exception error) { logger.LogDebug("Volume state subscriber failed ({Category}).", error.GetType().Name); }
    }

    private void Observe(CancellationToken token, TaskCompletionSource ready)
    {
        var initialized = ProcessLoopbackInterop.CoInitializeEx(0, 0) >= 0;
        // The queue carries one wake-up; the latest state stays retained separately.
        using var events = new BlockingCollection<bool>(1);
        var eventGate = new object();
        VolumeEvent? pendingVolume = null;
        IMMDeviceEnumerator? enumerator = null;
        IAudioEndpointVolume? endpoint = null;
        EndpointCallback? endpointCallback = null;
        var deviceChangePending = 0;
        var deviceCallback = new DeviceCallback(() =>
        {
            Interlocked.Exchange(ref deviceChangePending, 1);
            Wake();
        });
        var registered = false;
        var generation = 0;

        void Wake()
        {
            if (token.IsCancellationRequested) return;
            try { events.TryAdd(true); } catch (InvalidOperationException) { }
        }
        void Enqueue(VolumeActivitySnapshot snapshot, int sourceGeneration)
        {
            lock (eventGate)
            {
                if (token.IsCancellationRequested || sourceGeneration != generation) return;
                pendingVolume = new(snapshot, sourceGeneration);
            }
            Wake();
        }
        void DetachEndpoint()
        {
            if (endpoint is not null && endpointCallback is not null)
                try { endpoint.UnregisterControlChangeNotify(endpointCallback); }
                catch (COMException exception) { logger.LogDebug("Volume detach unavailable ({Code}).", exception.HResult); }
            ProcessLoopbackInterop.Release(endpoint); endpoint = null; endpointCallback = null;
            IsAvailable = false; Current = null;
            PublishSnapshot();
        }
        void AttachEndpoint()
        {
            DetachEndpoint();
            int sourceGeneration;
            lock (eventGate)
            {
                sourceGeneration = ++generation;
                pendingVolume = null;
            }
            IMMDevice? device = null;
            try
            {
                enumerator!.GetDefaultAudioEndpoint(0, 1, out device);
                device.Activate(typeof(IAudioEndpointVolume).GUID, 0x17, 0, out var value);
                endpoint = (IAudioEndpointVolume)value;
                endpointCallback = new EndpointCallback(snapshot => Enqueue(snapshot, sourceGeneration));
                endpoint.RegisterControlChangeNotify(endpointCallback);
                endpoint.GetMasterVolumeLevelScalar(out var volume);
                endpoint.GetMute(out var muted);
                Current = new((int)Math.Round(Math.Clamp(volume, 0, 1) * 100), muted, DateTimeOffset.UtcNow);
                IsAvailable = true;
                PublishSnapshot();
                // Attaching is not a user volume change and must not show a transient.
            }
            catch (COMException exception)
            { DetachEndpoint(); logger.LogDebug("Volume endpoint unavailable ({Code}).", exception.HResult); }
            finally { ProcessLoopbackInterop.Release(device); }
        }
        try
        {
            if (!initialized) throw new COMException("Volume apartment initialization failed.");
            enumerator = (IMMDeviceEnumerator)(object)new DeviceEnumerator();
            enumerator.RegisterEndpointNotificationCallback(deviceCallback); registered = true;
            AttachEndpoint(); ready.TrySetResult();
            foreach (var _ in events.GetConsumingEnumerable(token))
            {
                if (Interlocked.Exchange(ref deviceChangePending, 0) != 0) { AttachEndpoint(); continue; }
                VolumeEvent? change;
                lock (eventGate) { change = pendingVolume; pendingVolume = null; }
                if (change is null || change.Generation != generation) continue;
                var snapshot = change.Snapshot;
                if (Current is { } old && old.Percent == snapshot.Percent && old.Muted == snapshot.Muted) continue;
                Current = snapshot;
                PublishSnapshot();
                if (Changed is not { } handlers) continue;
                foreach (EventHandler<VolumeActivitySnapshot> handler in handlers.GetInvocationList())
                    try { handler(this, snapshot); }
                    catch (Exception exception) { logger.LogDebug("Volume subscriber failed ({Category}).", exception.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
        catch (Exception exception)
        { logger.LogDebug("Volume observer unavailable ({Category}).", exception.GetType().Name); ready.TrySetException(exception); }
        finally
        {
            DetachEndpoint();
            if (registered)
                try { enumerator!.UnregisterEndpointNotificationCallback(deviceCallback); }
                catch (COMException exception) { logger.LogDebug("Device observer detach unavailable ({Code}).", exception.HResult); }
            ProcessLoopbackInterop.Release(enumerator);
            if (initialized) ProcessLoopbackInterop.CoUninitialize();
        }
    }

    private async Task StopCoreAsync()
    {
        _stop?.Cancel(); await _worker.ConfigureAwait(false);
        _stop?.Dispose(); _stop = null; IsAvailable = false; Current = null;
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _controlOwners.Clear(); await StopCoreAsync().ConfigureAwait(false); await _writes.WaitAsync().ConfigureAwait(false); _writes.Release(); }
        finally { _lifecycle.Release(); }
    }
    private sealed record VolumeEvent(VolumeActivitySnapshot Snapshot, int Generation);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class EndpointCallback(Action<VolumeActivitySnapshot> publish) : IAudioEndpointVolumeCallback, ProcessLoopbackInterop.IAgileObject
    {
        public int OnNotify(nint data)
        {
            if (data == 0) return 0;
            var value = Marshal.PtrToStructure<VolumeNotification>(data);
            if (float.IsFinite(value.MasterVolume))
                publish(new((int)Math.Round(Math.Clamp(value.MasterVolume, 0, 1) * 100), value.Muted != 0, DateTimeOffset.UtcNow));
            return 0;
        }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class DeviceCallback(Action changed) : IMMNotificationClient, ProcessLoopbackInterop.IAgileObject
    {
        public int OnDeviceStateChanged(string id, uint state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(int flow, int role, string? id) { if (flow == 0 && role == 1) changed(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }
}
