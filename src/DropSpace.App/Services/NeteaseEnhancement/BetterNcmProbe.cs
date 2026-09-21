using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace DropSpace.App.Services.NeteaseEnhancement;

public sealed record BetterNcmInstallation(string ProfilePath, bool LoaderPresent, bool PluginPresent, bool VcRuntimeAvailable);

public sealed class BetterNcmProbe
{
    public Task<BetterNcmInstallation> FindAsync(NeteaseInstallation installation, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string profile = ResolveProfilePath();
        DeploymentPaths.AssertSafe(profile);
        string loader = Path.Combine(Path.GetDirectoryName(installation.ExecutablePath)!, "msimg32.dll");
        DeploymentPaths.AssertSafe(loader);
        bool runtime = NeteaseRuntimeInstaller.Installed(installation.Architecture);
        return new BetterNcmInstallation(profile, File.Exists(loader), File.Exists(Path.Combine(profile, "plugins", "InfLink-rs.plugin")), runtime);
    }, cancellationToken);

    public static string ResolveProfilePath()
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value = Environment.GetEnvironmentVariable("BETTERNCM_PROFILE", target);
            if (!string.IsNullOrWhiteSpace(value))
            {
                DeploymentPaths.AssertSafe(value);
                return Path.GetFullPath(value);
            }
        }
        return @"C:\betterncm";
    }
}
