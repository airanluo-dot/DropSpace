using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace DropSpace.Rendering;

internal static class CaptureInterop
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);

    internal static IDirect3DDevice CreateCaptureDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var result));
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(result); }
        finally { Marshal.Release(result); }
    }

    internal static unsafe GraphicsCaptureItem CreateMonitorItem(nint monitor)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var className));
        nint factory = 0, item = 0;
        try
        {
            var interopId = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, in interopId, out factory));
            var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[4];
            Marshal.ThrowExceptionForHR(create(factory, monitor, &itemId, &item));
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            WindowsDeleteString(className);
        }
    }

    internal static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var pointer = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint access = 0, texture = 0;
        try
        {
            var accessId = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pointer, in accessId, out access));
            var textureId = typeof(ID3D11Texture2D).GUID;
            var get = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
            Marshal.ThrowExceptionForHR(get(access, &textureId, &texture));
            return new ID3D11Texture2D(texture);
        }
        finally { if (access != 0) Marshal.Release(access); Marshal.Release(pointer); }
    }
}
