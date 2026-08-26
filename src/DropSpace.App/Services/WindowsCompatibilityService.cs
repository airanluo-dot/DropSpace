using System.Runtime.InteropServices;
using DropSpace.Core.Compatibility;
using Windows.Foundation.Metadata;

namespace DropSpace.App.Services;

internal sealed class WindowsOsVersionPolicy : IOsVersionPolicy
{
    public WindowsOsVersionPolicy()
    {
        Current = TryReadKernelVersion() ?? ReadEnvironmentVersion();
    }

    public WindowsOsVersion Current { get; }

    public bool IsSupported => WindowsCompatibilityPolicy.IsSupportedBuild(Current.Build);

    public bool IsAtLeast(int build) => Current.Build >= build;

    private static WindowsOsVersion? TryReadKernelVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var version = new RtlOsVersionInfoEx
            {
                Size = (uint)Marshal.SizeOf<RtlOsVersionInfoEx>(),
            };
            if (RtlGetVersion(ref version) != 0)
            {
                return null;
            }

            return new WindowsOsVersion(
                (int)version.Major,
                (int)version.Minor,
                (int)version.Build,
                version.Revision);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (MarshalDirectiveException)
        {
            return null;
        }
    }

    private static WindowsOsVersion ReadEnvironmentVersion()
    {
        var version = Environment.OSVersion.Version;
        return new WindowsOsVersion(version.Major, version.Minor, version.Build, version.Revision);
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int RtlGetVersion(ref RtlOsVersionInfoEx versionInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfoEx
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint PlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? ServicePack;

        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;

        public int Revision => 0;
    }
}

internal sealed class WindowsApiAvailabilityService : IApiAvailabilityService
{
    public bool IsTypePresent(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return Try(() => ApiInformation.IsTypePresent(typeName));
    }

    public bool IsMethodPresent(string typeName, string methodName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        return Try(() => ApiInformation.IsMethodPresent(typeName, methodName));
    }

    public bool IsApiContractPresent(string contractName, ushort majorVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);
        return Try(() => ApiInformation.IsApiContractPresent(contractName, majorVersion));
    }

    private static bool Try(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (TypeInitializationException)
        {
            return false;
        }
        catch (TypeLoadException)
        {
            return false;
        }
    }
}

internal sealed class WindowsAppRuntimeDependencyProbe(IApiAvailabilityService api) : IRuntimeDependencyProbe
{
    public RuntimeDependencyState Probe()
    {
        // WinAppSDK managed types are not Windows Runtime metadata types, so
        // ApiInformation.IsTypePresent cannot be used to probe Microsoft.UI.Xaml.
        // Reaching this service means the WinUI application assembly has already
        // loaded; optional OS capabilities are probed separately below.
        return new RuntimeDependencyState(
            CompatibilityStatus.Available,
            "Microsoft Windows App SDK XAML runtime is loaded.");
    }
}

internal sealed class WindowsCapabilityService : IWindowsCapabilityService
{
    private readonly IOsVersionPolicy _osVersion;
    private readonly IApiAvailabilityService _api;
    private readonly RuntimeDependencyState _runtimeDependency;

    public WindowsCapabilityService(
        IOsVersionPolicy osVersion,
        IApiAvailabilityService api,
        IRuntimeDependencyProbe runtimeProbe)
    {
        _osVersion = osVersion;
        _api = api;
        _runtimeDependency = runtimeProbe.Probe();
        Snapshot = new WindowsCompatibilitySnapshot(_osVersion.Current, _runtimeDependency);
    }

    public WindowsCompatibilitySnapshot Snapshot { get; }

    public WindowsCapabilityState Get(WindowsCapability capability)
    {
        if (!_osVersion.IsSupported)
        {
            return new WindowsCapabilityState(
                capability,
                CompatibilityStatus.UnsupportedByOs,
                $"Windows build {_osVersion.Current.Build} is below the supported build " +
                $"{WindowsCompatibilityPolicy.MinimumSupportedWindowsBuild}.");
        }

        if (!_runtimeDependency.IsAvailable)
        {
            return new WindowsCapabilityState(capability, _runtimeDependency.Status, _runtimeDependency.Reason);
        }

        return capability switch
        {
            WindowsCapability.ModernWindowAppearance => GetModernWindowAppearance(),
            WindowsCapability.ModernDwmAttributes => GetModernDwmAttributes(),
            WindowsCapability.WindowsShareTarget => GetWindowsShareTarget(),
            WindowsCapability.PdfPreview => GetApiCapability(
                capability,
                "Windows.Data.Pdf.PdfDocument",
                "Windows.Data.Pdf is available."),
            WindowsCapability.MediaPreview => GetApiCapability(
                capability,
                "Windows.Media.Playback.MediaPlayer",
                "Windows media playback APIs are available."),
            _ => new WindowsCapabilityState(
                capability,
                CompatibilityStatus.FailedFatal,
                "The capability is not recognized by this build."),
        };
    }

    public bool IsAvailable(WindowsCapability capability) => Get(capability).IsAvailable;

    private WindowsCapabilityState GetModernWindowAppearance()
    {
        if (!_osVersion.IsAtLeast(WindowsCompatibilityPolicy.Windows11Build))
        {
            return new WindowsCapabilityState(
                WindowsCapability.ModernWindowAppearance,
                CompatibilityStatus.UnsupportedByOs,
                "Mica is a Windows 11 visual capability; the solid base visual is used on Windows 10.");
        }

        return GetApiCapability(
            WindowsCapability.ModernWindowAppearance,
            "Microsoft.UI.Xaml.Media.MicaBackdrop",
            "Mica window appearance is available.");
    }

    private WindowsCapabilityState GetModernDwmAttributes()
    {
        if (!_osVersion.IsAtLeast(WindowsCompatibilityPolicy.Windows11Build))
        {
            return new WindowsCapabilityState(
                WindowsCapability.ModernDwmAttributes,
                CompatibilityStatus.UnsupportedByOs,
                "Windows 11 DWM corner and border attributes are not used on Windows 10.");
        }

        var available = _api.IsTypePresent("Microsoft.UI.Windowing.AppWindow");
        return new WindowsCapabilityState(
            WindowsCapability.ModernDwmAttributes,
            available ? CompatibilityStatus.Available : CompatibilityStatus.MissingRuntime,
            available
                ? "Modern DWM attributes are available."
                : "The Windows App SDK AppWindow type is not available.");
    }

    private WindowsCapabilityState GetWindowsShareTarget()
    {
        return GetApiCapability(
            WindowsCapability.WindowsShareTarget,
            "Windows.ApplicationModel.DataTransfer.ShareTarget.ShareOperation",
            "The Windows Share Target contract is available; package identity is checked separately.");
    }

    private WindowsCapabilityState GetApiCapability(
        WindowsCapability capability,
        string typeName,
        string availableReason)
    {
        var available = _api.IsTypePresent(typeName);
        return new WindowsCapabilityState(
            capability,
            available ? CompatibilityStatus.Available : CompatibilityStatus.MissingRuntime,
            available ? availableReason : $"The optional Windows API '{typeName}' is not available.");
    }
}

internal static class WindowsCompatibilityErrorDialog
{
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconError = 0x00000010;

    public static void Show(string message)
    {
        try
        {
            _ = MessageBox(nint.Zero, message, "DropSpace", MessageBoxOk | MessageBoxIconError);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // The crash marker remains the durable diagnostic when even the native dialog is unavailable.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
