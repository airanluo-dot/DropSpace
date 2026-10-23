using DropSpace.Core.Models;

namespace DropSpace.Core.Abstractions;

/// <summary>Shared, passive recent-content ownership. Changed never requests an Island activation.</summary>
public interface IRecentItemsService : IAsyncDisposable
{
    event EventHandler? Changed;
    IReadOnlyList<RelayDirectoryStatus> Directories { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task AddDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task SetDirectoryPausedAsync(string path, bool paused, CancellationToken cancellationToken = default);
    Task RemoveDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<DropItem?> RegisterOutputAsync(string path, string operation, bool partial = false, CancellationToken cancellationToken = default);
    Task RetainAsync(Guid id, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
    Task RefreshAsync(Guid id, CancellationToken cancellationToken = default);
    Task ApplyRetentionAsync(CancellationToken cancellationToken = default);
    IDisposable Protect(Guid id);
}
