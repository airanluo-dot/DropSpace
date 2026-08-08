using DropSpace.Core.Models;

namespace DropSpace.Core.Abstractions;

public interface IPayloadStore
{
    Task<PayloadRecord> WriteAsync(
        string kind,
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);

    string ResolvePath(string relativePath);
}
