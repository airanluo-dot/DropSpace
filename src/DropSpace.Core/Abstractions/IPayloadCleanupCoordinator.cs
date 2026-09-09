namespace DropSpace.Core.Abstractions;

public interface IPayloadCleanupCoordinator
{
    Task<int> DrainAsync(CancellationToken cancellationToken = default);

    Task<int> RecoverAsync(CancellationToken cancellationToken = default);
}
