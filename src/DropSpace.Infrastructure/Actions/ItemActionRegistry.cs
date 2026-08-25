using DropSpace.Core.Actions;

namespace DropSpace.Infrastructure.Actions;

public sealed class ItemActionRegistry(IEnumerable<IItemAction> actions) : IItemActionRegistry
{
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
            .Take(3)
            .ToArray();
    }

    public Task<ItemActionResult> ExecuteAsync(ItemActionId actionId, ItemActionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_byId.TryGetValue(actionId, out var action))
        {
            return Task.FromResult(ItemActionResult.Failure("unknown-action", "ActionUnavailable"));
        }

        return action.ExecuteAsync(context, cancellationToken);
    }
}
