using DropSpace.Core.Models;

namespace DropSpace.Core.Abstractions;

public interface IFileReferenceService
{
    Task<FileCandidate> InspectAsync(string path, CancellationToken cancellationToken = default);

    Task<FileAvailabilityCheck> CheckAvailabilityAsync(
        FileReference reference,
        CancellationToken cancellationToken = default);
}
