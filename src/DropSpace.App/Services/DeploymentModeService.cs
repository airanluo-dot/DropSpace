using System.Runtime.InteropServices;
using System.Text;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;
using Microsoft.Win32;

namespace DropSpace.App.Services;

public sealed class DeploymentModeService : IDeploymentModeService
{
    private const int AppModelErrorNoPackage = 15700;

    public DeploymentModeService()
    {
        Current = Resolve();
    }

    public DeploymentMode Current { get; }

    private static DeploymentMode Resolve()
    {
        var processPath = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable."));
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hive.OpenSubKey(@"Software\DropSpace\Install", writable: false);
        var installPath = key?.GetValue("InstallPath") as string;
        return DeploymentModeResolver.Resolve(
            HasPackageIdentity(),
            installPath,
            Path.GetDirectoryName(processPath)!);
    }

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return false;
        }

        if (result != 122 || length == 0)
        {
            return result == 0;
        }

        var value = new StringBuilder(checked((int)length));
        return GetCurrentPackageFullName(ref length, value) == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}
