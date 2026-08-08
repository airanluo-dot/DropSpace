namespace DropSpace.Core.Abstractions;

public interface ILocalStorageMetrics
{
    string RootPath { get; }

    Task<long?> GetByteLengthAsync(CancellationToken cancellationToken = default);
}
