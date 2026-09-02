using DropSpace.Core.Actions;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Actions;

public sealed class ItemActionRegistry(
    IEnumerable<IItemAction> actions,
    ILogger<ItemActionRegistry>? logger = null) : IItemActionRegistry
{
    private readonly ILogger<ItemActionRegistry>? _logger = logger;
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
        if (!_byId.TryGetValue(actionId, out var action))
        {
            return ItemActionResult.Failure("unknown-action", "ActionUnavailable");
        }

        try
        {
            return await action.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
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
