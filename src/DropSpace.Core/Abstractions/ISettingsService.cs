using DropSpace.Core.Models;

namespace DropSpace.Core.Abstractions;

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
