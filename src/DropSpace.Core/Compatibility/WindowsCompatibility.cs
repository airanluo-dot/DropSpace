namespace DropSpace.Core.Compatibility;

public static class WindowsCompatibilityPolicy
{
    public const int MinimumSupportedWindowsBuild = 17_763;
    public const int Windows11Build = 22_000;
    public const int CompileTimeWindowsSdkBuild = 26_100;

    public static bool IsSupportedBuild(int build) => build >= MinimumSupportedWindowsBuild;

    public static bool IsWindows11OrLater(int build) => build >= Windows11Build;
}

public enum CompatibilityStatus
{
    Available,
    UnsupportedByOs,
    MissingRuntime,
    BlockedByPolicy,
    FailedRecoverably,
    FailedFatal,
}

public enum WindowsCapability
{
    ModernWindowAppearance,
    ModernDwmAttributes,
    WindowsShareTarget,
    PdfPreview,
    MediaPreview,
}

public sealed record WindowsOsVersion(int Major, int Minor, int Build, int Revision)
{
    public override string ToString() => $"{Major}.{Minor}.{Build}.{Revision}";
}

public sealed record RuntimeDependencyState(CompatibilityStatus Status, string Reason)
{
    public bool IsAvailable => Status == CompatibilityStatus.Available;
}

public sealed record WindowsCapabilityState(
    WindowsCapability Capability,
    CompatibilityStatus Status,
    string Reason)
{
    public bool IsAvailable => Status == CompatibilityStatus.Available;
}

public sealed record WindowsCompatibilitySnapshot(
    WindowsOsVersion OperatingSystem,
    RuntimeDependencyState RuntimeDependency)
{
    public bool IsSupported => WindowsCompatibilityPolicy.IsSupportedBuild(OperatingSystem.Build);
}

public interface IOsVersionPolicy
{
    WindowsOsVersion Current { get; }

    bool IsSupported { get; }

    bool IsAtLeast(int build);
}

public interface IApiAvailabilityService
{
    bool IsTypePresent(string typeName);

    bool IsMethodPresent(string typeName, string methodName);

    bool IsApiContractPresent(string contractName, ushort majorVersion);
}

public interface IRuntimeDependencyProbe
{
    RuntimeDependencyState Probe();
}

public interface IWindowsCapabilityService
{
    WindowsCompatibilitySnapshot Snapshot { get; }

    WindowsCapabilityState Get(WindowsCapability capability);

    bool IsAvailable(WindowsCapability capability);
}
