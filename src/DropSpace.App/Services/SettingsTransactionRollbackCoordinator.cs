namespace DropSpace.App.Services;

internal sealed class SettingsTransactionRollbackCoordinator
{
    private readonly Stack<(string Category, Func<Task> Undo)> _committed = new();

    internal void Committed(string category, Func<Task> undo) => _committed.Push((category, undo));

    internal async Task RollbackAsync(Action<string, Exception> report)
    {
        while (_committed.TryPop(out var step))
        {
            try { await step.Undo(); }
            catch (Exception exception)
            {
                // Diagnostics must not interrupt the remaining rollback steps either.
                try { report(step.Category, exception); }
                catch { }
            }
        }
    }
}
