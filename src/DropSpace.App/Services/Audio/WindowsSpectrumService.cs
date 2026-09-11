using System.Runtime.InteropServices;
using DropSpace.Core.Audio;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Audio;

public sealed class WindowsSpectrumService : IAudioSpectrumService
{
    private readonly IMediaSessionService _media;
    private readonly ILogger<WindowsSpectrumService> _logger;
    private readonly SpectrumAnalyzer _analyzer = new(32);
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _enabled;
    private bool _disposed;
    private SpectrumFrame _current = SpectrumFrame.Empty;

    public WindowsSpectrumService(IMediaSessionService media, ILogger<WindowsSpectrumService> logger)
    {
        _media = media;
        _logger = logger;
        _media.Changed += OnMediaChanged;
    }

    public event EventHandler<SpectrumFrame>? FrameChanged;

    public SpectrumFrame Current => _current;

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _enabled = enabled;
            if (!enabled || _media.Current.PlaybackState != MediaPlaybackState.Playing)
            {
                _timer?.Dispose();
                _timer = null;
                Publish(SpectrumFrame.Empty);
            }
            else if (_timer is null)
            {
                _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(33));
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _media.Changed -= OnMediaChanged;
            _timer?.Dispose();
            _timer = null;
        }

        return ValueTask.CompletedTask;
    }

    private void OnMediaChanged(object? sender, MediaSessionSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_enabled || snapshot.PlaybackState != MediaPlaybackState.Playing)
            {
                _timer?.Dispose();
                _timer = null;
                Publish(SpectrumFrame.Empty);
            }
            else if (_timer is null)
            {
                _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(33));
            }
        }
    }

    private void Sample()
    {
        if (!_enabled || _media.Current.PlaybackState != MediaPlaybackState.Playing) return;
        try
        {
            // The endpoint meter is the documented bounded fallback when Process Loopback
            // activation cannot resolve a single media PID (browser/UWP multi-process cases).
            // It is sampled only while media is actively playing and the feature is enabled.
            var peak = ReadDefaultRenderPeak();
            Span<float> samples = stackalloc float[32];
            samples.Fill(peak);
            var bars = _analyzer.Analyze(samples).ToArray();
            Publish(new SpectrumFrame(bars, SpectrumCaptureMode.SystemFallback, DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            _logger.LogDebug(exception, "Spectrum fallback sample failed safely.");
        }
    }

    private static float ReadDefaultRenderPeak()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioMeterInformation? meter = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device);
            var iid = typeof(IAudioMeterInformation).GUID;
            device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var instance);
            meter = (IAudioMeterInformation)instance;
            meter.GetPeakValue(out var peak);
            return Math.Clamp(peak, 0, 1);
        }
        finally
        {
            ReleaseComObject(meter);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private void Publish(SpectrumFrame frame)
    {
        _current = frame;
        FrameChanged?.Invoke(this, frame);
    }

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }
    private enum ClsCtx : uint { All = 0x17 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), ClassInterface(ClassInterfaceType.None)]
    private sealed class MMDeviceEnumerator { }

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

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }
}
