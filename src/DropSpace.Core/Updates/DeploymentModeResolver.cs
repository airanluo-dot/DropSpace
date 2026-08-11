namespace DropSpace.Core.Updates;

public static class DeploymentModeResolver
{
    public static DeploymentMode Resolve(
        bool hasPackageIdentity,
        string? registeredInstallPath,
        string executableDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        var executablePath = Normalize(executableDirectory);
        if (!string.IsNullOrWhiteSpace(registeredInstallPath) &&
            string.Equals(Normalize(registeredInstallPath), executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return DeploymentMode.Installer;
        }

        return hasPackageIdentity ? DeploymentMode.Packaged : DeploymentMode.Portable;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
