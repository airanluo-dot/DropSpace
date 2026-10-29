using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace DropSpace.Rendering;

/// <summary>Optional Windows 11 capture pacing, without raising the minimum supported OS.</summary>
internal sealed class CaptureRateControl : IDisposable
{
    // Windows SDK 26100 windows.graphics.capture.h: IGraphicsCaptureSession5,
    // IInspectable slots 6/7, TimeSpan is a signed 64-bit count of 100 ns units.
    private nint _native;
    private readonly long _originalInterval;
    internal bool IsSupported => _native != 0;

    internal unsafe CaptureRateControl(GraphicsCaptureSession session)
    {
        var pointer = WinRT.MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
        try
        {
            var iid = new Guid("67c0ea62-1f85-5061-925a-239be0ac09cb");
            var result = Marshal.QueryInterface(pointer, in iid, out _native);
            if (result == unchecked((int)0x80004002)) { _native = 0; return; }
            Marshal.ThrowExceptionForHR(result);
            long interval = 0;
            var get = (delegate* unmanaged[Stdcall]<nint, long*, int>)(*(nint**)_native)[6];
            Marshal.ThrowExceptionForHR(get(_native, &interval));
            _originalInterval = interval;
        }
        catch { Dispose(); throw; }
        finally { Marshal.Release(pointer); }
    }

    internal unsafe void SetRate(double? rate)
    {
        if (_native == 0) return;
        var interval = rate is { } hz ? hz > 0 ? (long)(TimeSpan.TicksPerSecond / hz) : 0 : _originalInterval;
        var set = (delegate* unmanaged[Stdcall]<nint, long, int>)(*(nint**)_native)[7];
        Marshal.ThrowExceptionForHR(set(_native, interval));
    }

    public void Dispose()
    {
        if (_native == 0) return;
        Marshal.Release(_native); _native = 0;
    }
}
