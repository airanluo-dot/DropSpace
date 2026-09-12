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
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;
    public bool IsAvailable { get; private set; }
    public VolumeActivitySnapshot? Current { get; private set; }
    public event EventHandler<VolumeActivitySnapshot>? Changed;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
        finally { _lifecycle.Release(); }
    }

    private void Observe(CancellationToken token, TaskCompletionSource ready)
    {
        var initialized = ProcessLoopbackInterop.CoInitializeEx(0, 0) >= 0;
        using var events = new BlockingCollection<VolumeEvent>(32);
        IMMDeviceEnumerator? enumerator = null;
        IAudioEndpointVolume? endpoint = null;
        EndpointCallback? endpointCallback = null;
        var deviceChangePending = 0;
        var deviceCallback = new DeviceCallback(() =>
        {
            Interlocked.Exchange(ref deviceChangePending, 1);
            Enqueue(new(true, null, 0));
        });
        var registered = false;
        var generation = 0;

        void Enqueue(VolumeEvent value)
        {
            if (token.IsCancellationRequested) return;
            try { events.TryAdd(value); } catch (InvalidOperationException) { }
        }
        void DetachEndpoint()
        {
            if (endpoint is not null && endpointCallback is not null)
                try { endpoint.UnregisterControlChangeNotify(endpointCallback); }
                catch (COMException exception) { logger.LogDebug("Volume detach unavailable ({Code}).", exception.HResult); }
            ProcessLoopbackInterop.Release(endpoint); endpoint = null; endpointCallback = null;
            IsAvailable = false; Current = null;
        }
        void AttachEndpoint()
        {
            DetachEndpoint();
            var sourceGeneration = ++generation;
            IMMDevice? device = null;
            try
            {
                enumerator!.GetDefaultAudioEndpoint(0, 1, out device);
                device.Activate(typeof(IAudioEndpointVolume).GUID, 0x17, 0, out var value);
                endpoint = (IAudioEndpointVolume)value;
                endpointCallback = new EndpointCallback(snapshot => Enqueue(new(false, snapshot, sourceGeneration)));
                endpoint.RegisterControlChangeNotify(endpointCallback);
                endpoint.GetMasterVolumeLevelScalar(out var volume);
                endpoint.GetMute(out var muted);
                Current = new((int)Math.Round(Math.Clamp(volume, 0, 1) * 100), muted, DateTimeOffset.UtcNow);
                IsAvailable = true;
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
            foreach (var change in events.GetConsumingEnumerable(token))
            {
                if (Interlocked.Exchange(ref deviceChangePending, 0) != 0 || change.DeviceChanged) { AttachEndpoint(); continue; }
                if (change.Generation != generation || change.Snapshot is not { } snapshot) continue;
                if (Current is { } old && old.Percent == snapshot.Percent && old.Muted == snapshot.Muted) continue;
                Current = snapshot;
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
        try { if (_disposed) return; _disposed = true; await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }
    private sealed record VolumeEvent(bool DeviceChanged, VolumeActivitySnapshot? Snapshot, int Generation);

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
