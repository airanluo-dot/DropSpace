using System.Runtime.InteropServices;
using DropSpace.Core.Displays;

namespace DropSpace.App.Services;

public sealed record DisplayIdentityResolution(
    string Id,
    bool IsPersistent,
    string? DevicePath);

/// <summary>
/// Resolves a runtime HMONITOR to the active DisplayConfig target device path. The path is hashed
/// before it becomes a settings key; the HMONITOR is retained only for process-lifetime Win32 use.
/// </summary>
public sealed class DisplayIdentityService
{
    private const uint QueryDisplayConfigOnlyActivePaths = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;
    private const uint DeviceInfoSourceName = 1;
    private const uint DeviceInfoTargetName = 2;

    public DisplayIdentityResolution Resolve(nint monitorHandle, string? gdiDeviceName)
    {
        var devicePath = string.IsNullOrWhiteSpace(gdiDeviceName)
            ? null
            : TryResolveTargetPath(gdiDeviceName);
        return devicePath is not null
            ? new DisplayIdentityResolution(DisplayIdentity.CreatePersistentId(devicePath), true, devicePath)
            : new DisplayIdentityResolution(DisplayIdentity.CreateRuntimeFallbackId(monitorHandle), false, null);
    }

    private static string? TryResolveTargetPath(string gdiDeviceName)
    {
        try
        {
            var result = GetDisplayConfigBufferSizes(
                QueryDisplayConfigOnlyActivePaths,
                out var pathCount,
                out var modeCount);
            if (result != 0 || pathCount == 0)
            {
                return null;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var paths = new DisplayConfigPathInfo[checked((int)pathCount)];
                var modeBufferSize = checked((nuint)Math.Max(1u, modeCount) * (nuint)128);
                var modeBuffer = Marshal.AllocHGlobal(checked((nint)modeBufferSize));
                try
                {
                    var requestedPathCount = pathCount;
                    var requestedModeCount = modeCount;
                    result = QueryDisplayConfig(
                        QueryDisplayConfigOnlyActivePaths,
                        ref requestedPathCount,
                        paths,
                        ref requestedModeCount,
                        modeBuffer,
                        nint.Zero);
                    if (result == ErrorInsufficientBuffer)
                    {
                        pathCount = requestedPathCount;
                        modeCount = requestedModeCount;
                        continue;
                    }

                    if (result != 0)
                    {
                        return null;
                    }

                    var pathLimit = Math.Min(checked((int)requestedPathCount), paths.Length);
                    for (var index = 0; index < pathLimit; index++)
                    {
                        var path = paths[index];
                        var sourceName = new DisplayConfigSourceDeviceName
                        {
                            Header = CreateHeader(
                                DeviceInfoSourceName,
                                (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                                path.SourceInfo.AdapterId,
                                path.SourceInfo.Id),
                            ViewGdiDeviceName = string.Empty,
                        };
                        if (DisplayConfigGetDeviceInfo(ref sourceName) != 0 ||
                            !string.Equals(sourceName.ViewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var targetName = new DisplayConfigTargetDeviceName
                        {
                            Header = CreateHeader(
                                DeviceInfoTargetName,
                                (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                                path.TargetInfo.AdapterId,
                                path.TargetInfo.Id),
                            MonitorFriendlyDeviceName = string.Empty,
                            MonitorDevicePath = string.Empty,
                        };
                        return DisplayConfigGetDeviceInfo(ref targetName) == 0 &&
                               !string.IsNullOrWhiteSpace(targetName.MonitorDevicePath)
                            ? targetName.MonitorDevicePath
                            : null;
                    }

                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(modeBuffer);
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
                                          MarshalDirectiveException or InvalidOperationException or
                                          ArgumentException or OverflowException)
        {
            return null;
        }

        return null;
    }

    private static DisplayConfigDeviceInfoHeader CreateHeader(
        uint type,
        uint size,
        DisplayConfigLuid adapterId,
        uint id) =>
        new()
        {
            Type = type,
            Size = size,
            AdapterId = adapterId,
            Id = id,
        };

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public DisplayConfigLuid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public DisplayConfigLuid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public DisplayConfigLuid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public uint TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        nint modeInfoArray,
        nint currentTopologyId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);
}
