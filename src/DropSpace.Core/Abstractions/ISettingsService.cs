using DropSpace.Core.Models;

namespace DropSpace.Core.Abstractions;

public sealed record SettingsRecoveryReport(
    bool Recovered,
    bool PreservedNonUiPreferences,
    string? QuarantineFileName,
    string? ErrorCategory)
{
    public static SettingsRecoveryReport None { get; } = new(false, false, null, null);
}

public interface ISettingsService
{
    SettingsRecoveryReport LastLoadRecovery { get; }

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task<AppSettings> ResetUiSettingsAsync(CancellationToken cancellationToken = default);
}
