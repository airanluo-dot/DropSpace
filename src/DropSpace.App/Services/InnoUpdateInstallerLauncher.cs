using System.Diagnostics;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;

namespace DropSpace.App.Services;

public sealed class InnoUpdateInstallerLauncher : IUpdateInstallerLauncher
{
    public Task<bool> LaunchAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = update.FilePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(update.FilePath)!,
        };
        foreach (var argument in UpdateInstallerArguments.Create(update.InstallLogPath))
        {
            start.ArgumentList.Add(argument);
        }
        return Task.FromResult(Process.Start(start) is not null);
    }
}
