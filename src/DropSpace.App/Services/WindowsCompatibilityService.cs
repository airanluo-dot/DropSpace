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

internal sealed class WindowsAppRuntimeDependencyProbe : IRuntimeDependencyProbe
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
    private const int SystemMetricRemoteSession = 0x1000;
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
            WindowsCapability.TransientSystemBackdrop => GetTransientSystemBackdrop(),
            WindowsCapability.DesktopAcrylic => GetDesktopAcrylic(),
            WindowsCapability.CompositionEffects => GetCompositionEffects(),
            WindowsCapability.AdvancedMotion => GetAdvancedMotion(),
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

        // Microsoft.UI.Xaml types are managed Windows App SDK types, not Windows Runtime
        // metadata. The application assembly has already loaded the runtime at this point, so
        // the OS build is the capability gate and MainWindow still keeps a catch-based fallback
        // around MicaBackdrop construction.
        return new WindowsCapabilityState(
            WindowsCapability.ModernWindowAppearance,
            CompatibilityStatus.Available,
            "Windows 11 supports the optional Mica window appearance.");
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

        return new WindowsCapabilityState(
            WindowsCapability.ModernDwmAttributes,
            CompatibilityStatus.Available,
            "Windows 11 supports the optional DWM corner and border attributes; each HRESULT is checked at the HWND boundary.");
    }

    private WindowsCapabilityState GetTransientSystemBackdrop()
    {
        if (!_osVersion.IsAtLeast(WindowsCompatibilityPolicy.Windows11Build))
        {
            return new WindowsCapabilityState(
                WindowsCapability.TransientSystemBackdrop,
                CompatibilityStatus.UnsupportedByOs,
                "System backdrop surfaces are limited to Windows 11; a solid visual is used on Windows 10.");
        }

        return new WindowsCapabilityState(
            WindowsCapability.TransientSystemBackdrop,
            CompatibilityStatus.Available,
            "Windows 11 supports a bounded SystemBackdropElement surface when user effects permit it.");
    }

    private WindowsCapabilityState GetDesktopAcrylic()
    {
        var transient = GetTransientSystemBackdrop();
        if (!transient.IsAvailable)
        {
            return new WindowsCapabilityState(
                WindowsCapability.DesktopAcrylic,
                transient.Status,
                transient.Reason);
        }

        if (IsRemoteSession())
        {
            return new WindowsCapabilityState(
                WindowsCapability.DesktopAcrylic,
                CompatibilityStatus.BlockedByPolicy,
                "Desktop Acrylic is disabled in an RDP session to keep the transient surface deterministic.",
                IsRemoteSession: true);
        }

        try
        {
            var supported = Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
            return new WindowsCapabilityState(
                WindowsCapability.DesktopAcrylic,
                supported ? CompatibilityStatus.Available : CompatibilityStatus.BlockedByPolicy,
                supported
                    ? "Desktop Acrylic is supported by the current Windows App SDK/DWM environment."
                    : "The current Windows App SDK/DWM environment does not support Desktop Acrylic.");
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or TypeInitializationException or TypeLoadException)
        {
            return new WindowsCapabilityState(
                WindowsCapability.DesktopAcrylic,
                CompatibilityStatus.FailedRecoverably,
                $"Desktop Acrylic capability probing failed safely: {exception.GetType().Name}.");
        }
    }

    private WindowsCapabilityState GetCompositionEffects()
    {
        // The Windows.UI.Composition metadata probe is valid for the OS-backed
        // compositor. Microsoft.UI.Composition is a managed WinAppSDK surface and
        // must never be passed to Windows Runtime ApiInformation.
        if (!_api.IsTypePresent("Windows.UI.Composition.Compositor"))
        {
            return new WindowsCapabilityState(
                WindowsCapability.CompositionEffects,
                CompatibilityStatus.MissingRuntime,
                "The optional composition compositor is not available; UI-thread animation remains the safe fallback.");
        }

        try
        {
            var capabilities = new Microsoft.UI.Composition.CompositionCapabilities();
            var supported = capabilities.AreEffectsSupported();
            var fast = supported && capabilities.AreEffectsFast();
            return new WindowsCapabilityState(
                WindowsCapability.CompositionEffects,
                supported ? CompatibilityStatus.Available : CompatibilityStatus.BlockedByPolicy,
                supported
                    ? $"Composition effects are supported; fastEffects={fast}."
                    : "Composition effects are not supported in the current graphics environment.",
                IsFast: fast);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or TypeInitializationException or TypeLoadException)
        {
            return new WindowsCapabilityState(
                WindowsCapability.CompositionEffects,
                CompatibilityStatus.FailedRecoverably,
                $"Composition capability probing failed safely: {exception.GetType().Name}.");
        }
    }

    private static bool IsRemoteSession()
    {
        try
        {
            return OperatingSystem.IsWindows() && GetSystemMetrics(SystemMetricRemoteSession) != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private WindowsCapabilityState GetAdvancedMotion()
    {
        var composition = GetCompositionEffects();
        return composition.IsAvailable
            ? new WindowsCapabilityState(
                WindowsCapability.AdvancedMotion,
                CompatibilityStatus.Available,
                "Composition-backed motion channels are available; native geometry remains UI-thread owned.")
            : new WindowsCapabilityState(
                WindowsCapability.AdvancedMotion,
                composition.Status,
                composition.Reason);
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

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
