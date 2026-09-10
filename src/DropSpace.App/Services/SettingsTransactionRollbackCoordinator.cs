namespace DropSpace.App.Services;

internal sealed record SettingsRollbackFailure(string Category, Exception Exception, Func<Task> Retry);

internal sealed class SettingsTransactionRollbackCoordinator
{
    private readonly Stack<(string Category, Func<Task> Undo)> _committed = new();

    internal void Committed(string category, Func<Task> undo) => _committed.Push((category, undo));

    internal async Task<IReadOnlyList<SettingsRollbackFailure>> RollbackAsync(Action<string, Exception> report)
    {
        var failures = new List<SettingsRollbackFailure>();
        while (_committed.TryPop(out var step))
        {
            try { await step.Undo(); }
            catch (Exception exception)
            {
                failures.Add(new SettingsRollbackFailure(step.Category, exception, step.Undo));
                try { report(step.Category, exception); }
                catch { }
            }
        }
        return failures;
    }
}
