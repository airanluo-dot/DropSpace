using DropSpace.Core.Actions;
using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Actions;

public sealed class ItemActionRegistry(
    IEnumerable<IItemAction> actions,
    ILogger<ItemActionRegistry>? logger = null,
    IRecentItemsService? recent = null) : IItemActionRegistry, IAsyncDisposable
{
    private readonly ILogger<ItemActionRegistry>? _logger = logger;
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeExecutions;
    private bool _stopping;
    private readonly IReadOnlyDictionary<ItemActionId, IItemAction> _byId = actions
        .GroupBy(action => action.Descriptor.Id)
        .ToDictionary(group => group.Key, group => group.OrderByDescending(action => action.Descriptor.Order).First());

    public IReadOnlyList<IItemAction> Actions => _byId.Values
        .OrderBy(action => action.Descriptor.Group)
        .ThenBy(action => action.Descriptor.Order)
        .ToArray();

    public IReadOnlyList<ItemActionCapability> Evaluate(ItemSelectionSnapshot selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Actions
            .Select(action => action.Evaluate(selection))
            .Where(capability => capability.IsAvailable)
            .ToArray();
    }

    public IReadOnlyList<ItemActionCapability> EvaluatePrimary(ItemSelectionSnapshot selection) => Evaluate(selection).Take(3).ToArray();

    public IReadOnlyList<ItemActionCapability> EvaluateMore(ItemSelectionSnapshot selection) => Evaluate(selection).Skip(3).ToArray();

    public async Task<ItemActionResult> ExecuteAsync(
        ItemActionId actionId,
        ItemActionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_lifetimeGate)
        {
            if (_stopping) return ItemActionResult.Failure("cancelled", "ActionCancelled");
            _activeExecutions++;
        }
        try { return await ExecuteCoreAsync(actionId, context, cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_lifetimeGate)
            {
                _activeExecutions--;
                if (_stopping && _activeExecutions == 0) _drained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Stop new actions and drain owners through final-output registration. The UI first cancels
    /// its action tokens; already committed files are still registered before recent storage closes.
    /// </summary>
    public Task ShutdownAsync()
    {
        lock (_lifetimeGate)
        {
            _stopping = true;
            if (_activeExecutions == 0) _drained.TrySetResult();
            return _drained.Task;
        }
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task<ItemActionResult> ExecuteCoreAsync(
        ItemActionId actionId, ItemActionContext context, CancellationToken cancellationToken)
    {
        if (!_byId.TryGetValue(actionId, out var action))
        {
            return ItemActionResult.Failure("unknown-action", "ActionUnavailable");
        }

        try
        {
            var result = await action.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            if (recent is null || (!result.Succeeded && !result.IsPartial) || result.OutputPaths.Count == 0)
                return result;

            var ids = new List<Guid>(result.OutputItemIds);
            var registrationFailed = false;
            // The action has returned after closing its output streams. A cancelled or failed
            // action never reaches this path; a late dialog cancellation cannot hide committed work.
            foreach (var path in result.OutputPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var item = await recent.RegisterOutputAsync(path, actionId.ToString(), result.IsPartial,
                        CancellationToken.None).ConfigureAwait(false);
                    if (item is not null) ids.Add(item.Id);
                    else registrationFailed = true;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    registrationFailed = true;
                    _logger?.LogWarning(error, "Completed action {ActionId} could not register an output.", actionId);
                }
            }
            return result with
            {
                OutputItemIds = ids.Distinct().ToArray(),
                MessageResourceKey = registrationFailed ? "ActionOutputRegistrationFailed" :
                    result.IsPartial ? "ActionPartiallyCompleted" : result.MessageResourceKey,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("Quick action {ActionId} was cancelled.", actionId);
            return ItemActionResult.Failure("cancelled", "ActionCancelled");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger?.LogWarning(exception, "Quick action {ActionId} could not access its source or output.", actionId);
            return ItemActionResult.Failure("access-denied", "ActionAccessDenied");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger?.LogWarning(exception, "Quick action {ActionId} could not find its source or output.", actionId);
            return ItemActionResult.Failure("source-unavailable", "ActionSourceUnavailable");
        }
        catch (InvalidDataException exception)
        {
            _logger?.LogWarning(exception, "Quick action {ActionId} rejected its input or output data.", actionId);
            return ItemActionResult.Failure("invalid-data", "ActionInvalidData");
        }
        catch (ArgumentException exception)
        {
            _logger?.LogWarning(exception, "Quick action {ActionId} rejected its parameters.", actionId);
            return ItemActionResult.Failure("invalid-parameters", "ActionParametersRequired");
        }
        catch (IOException exception)
        {
            _logger?.LogWarning(exception, "Quick action {ActionId} could not write its output.", actionId);
            return ItemActionResult.Failure("output-unavailable", "ActionOutputUnavailable");
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Quick action {ActionId} failed unexpectedly.", actionId);
            return ItemActionResult.Failure("execution-failed", "ActionFailed");
        }
    }
}
