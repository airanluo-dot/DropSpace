using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DropSpace.App.Services;

/// <summary>
/// Per-user, no-elevation startup registration shared by portable and Inno-installed builds.
/// </summary>
public sealed class StartupRegistrationService(ILogger<StartupRegistrationService> logger)
    : IStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DropSpace";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value &&
                   string.Equals(value, BuildCommand(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The current-user Windows startup registry key is unavailable.");
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
            logger.LogInformation("Per-user Windows startup registration is enabled.");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            logger.LogInformation("Per-user Windows startup registration is disabled.");
        }

        return Task.CompletedTask;
    }

    private static string BuildCommand()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("The DropSpace executable path is unavailable.");
        }

        return $"\"{executable}\" --startup";
    }
}
