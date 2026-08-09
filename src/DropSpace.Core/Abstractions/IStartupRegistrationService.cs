namespace DropSpace.Core.Abstractions;

public interface IStartupRegistrationService
{
    bool IsEnabled { get; }

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
