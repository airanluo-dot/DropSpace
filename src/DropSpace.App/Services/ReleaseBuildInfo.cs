using System.Reflection;
using DropSpace.Core.Updates;

namespace DropSpace.App.Services;

public sealed class ReleaseBuildInfo
{
    public ReleaseBuildInfo()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var normalized = informational?.Split('+', 2)[0];
        CurrentVersion = ReleaseVersion.TryParse(normalized, out var version)
            ? version
            : throw new InvalidOperationException($"The executable has an invalid release version: {informational}");
    }

    public ReleaseVersion CurrentVersion { get; }
}
