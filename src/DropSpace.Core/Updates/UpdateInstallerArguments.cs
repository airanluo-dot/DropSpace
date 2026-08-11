namespace DropSpace.Core.Updates;

public static class UpdateInstallerArguments
{
    public static IReadOnlyList<string> Create(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        if (!Path.IsPathFullyQualified(logPath))
        {
            throw new ArgumentException("The update installer log path must be absolute.", nameof(logPath));
        }

        return [
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/UPDATE",
            $"/LOG={logPath}",
        ];
    }
}
