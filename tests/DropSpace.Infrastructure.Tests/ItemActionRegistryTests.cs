using DropSpace.Core.Actions;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Actions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ItemActionRegistryTests
{
    [TestMethod]
    public void RegistryEvaluatesTheFullSetAndPartitionsPrimaryActions()
    {
        var actions = Enumerable.Range(1, 5)
            .Select(index => (IItemAction)new TestAction((ItemActionId)(100 + index), index))
            .ToArray();
        var registry = new ItemActionRegistry(actions);
        var selection = new ItemSelectionSnapshot([]);

        Assert.AreEqual(5, registry.Evaluate(selection).Count);
        Assert.AreEqual(3, registry.EvaluatePrimary(selection).Count);
        Assert.AreEqual(2, registry.EvaluateMore(selection).Count);
    }

    private sealed class TestAction(ItemActionId id, int order) : IItemAction
    {
        public ItemActionDescriptor Descriptor { get; } = new(id, "Test", "Test", ItemActionGroup.General, order, false, false);

        public ItemActionCapability Evaluate(ItemSelectionSnapshot selection) => new(true, null, Descriptor);

        public Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ItemActionResult.Success());
    }
}
