using System.Runtime.InteropServices;
using DropSpace.Core.SystemActivities;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Volume;

public sealed class WindowsVolumeActivityService : IVolumeActivityService
{
    private readonly ILogger<WindowsVolumeActivityService> _logger;
    private IAudioEndpointVolume? _endpoint;
    private AudioEndpointVolumeCallback? _callback;
    private bool _enabled;
    private bool _disposed;

    public WindowsVolumeActivityService(ILogger<WindowsVolumeActivityService> logger) => _logger = logger;

    public event EventHandler<VolumeActivitySnapshot>? Changed;

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled == enabled) return Task.CompletedTask;
        _enabled = enabled;
        if (enabled) Attach(); else Detach();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Detach();
        }

        return ValueTask.CompletedTask;
    }

    private void Attach()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var endpoint);
            _endpoint = (IAudioEndpointVolume)endpoint;
            _callback = new AudioEndpointVolumeCallback(this);
            _endpoint.RegisterControlChangeNotify(_callback);
            PublishCurrent();
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Default render endpoint volume activity is unavailable.");
            Detach();
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private void Detach()
    {
        if (_endpoint is not null && _callback is not null)
        {
            try { _endpoint.UnregisterControlChangeNotify(_callback); } catch (COMException) { }
        }

        ReleaseComObject(_endpoint);
        _callback = null;
        _endpoint = null;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private void PublishCurrent()
    {
        if (_endpoint is null) return;
        _endpoint.GetMasterVolumeLevelScalar(out var volume);
        _endpoint.GetMute(out var muted);
        Changed?.Invoke(this, new VolumeActivitySnapshot((int)Math.Round(Math.Clamp(volume, 0, 1) * 100), muted, DateTimeOffset.UtcNow));
    }

    private sealed class AudioEndpointVolumeCallback(WindowsVolumeActivityService owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notificationData)
        {
            if (owner._enabled) owner.PublishCurrent();
            return 0;
        }
    }

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }
    private enum ClsCtx : uint { All = 0x17 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), ClassInterface(ClassInterfaceType.None)]
    private sealed class MMDeviceEnumerator : IMMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out object devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, ClsCtx clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.Interface)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        int GetChannelCount(out uint channelCount);
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(ref Guid eventContext);
        int VolumeStepDown(ref Guid eventContext);
        int QueryHardwareSupport(out uint hardwareSupportMask);
        int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        int OnNotify(IntPtr notificationData);
    }
}
