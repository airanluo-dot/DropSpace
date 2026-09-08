using DropSpace.Core.Models;
using DropSpace.Core.Undo;

namespace DropSpace.App.Services;

/// <summary>
/// Application boundary for destructive workspace mutations. Views and
/// ViewModels request an undoable operation here; repository deletion and the
/// payload cleanup owner remain behind the UndoCoordinator boundary.
/// </summary>
public sealed class WorkspaceMutationUseCase(UndoCoordinator undo)
{
    public Task FinalizeActiveAsync(CancellationToken cancellationToken = default) =>
        undo.FinalizeActiveAsync(cancellationToken);

    public Task<UndoState?> BeginRemovalAsync(
        IReadOnlyCollection<Guid> ids,
        UndoOperationKind kind,
        string messageResourceKey,
        CancellationToken cancellationToken = default) =>
        undo.BeginRemovalAsync(ids, kind, messageResourceKey, cancellationToken);

    public Task<UndoState?> BeginClipboardClearAsync(
        DateTimeOffset? fromUtc,
        bool includePinned,
        string messageResourceKey,
        CancellationToken cancellationToken = default) =>
        undo.BeginClipboardClearAsync(fromUtc, includePinned, messageResourceKey, cancellationToken);
}
