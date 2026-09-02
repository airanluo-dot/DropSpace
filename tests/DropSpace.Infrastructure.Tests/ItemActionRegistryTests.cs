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

    [TestMethod]
    public async Task RegistryMapsKnownActionFailuresToLocalizedCategories()
    {
        var registry = new ItemActionRegistry(
        [
            new ThrowingAction(ItemActionId.HashSha256, new InvalidDataException()),
            new ThrowingAction(ItemActionId.CompressZip, new IOException()),
        ]);
        var context = new ItemActionContext(new ItemSelectionSnapshot([]));

        var invalidData = await registry.ExecuteAsync(ItemActionId.HashSha256, context);
        var outputUnavailable = await registry.ExecuteAsync(ItemActionId.CompressZip, context);

        Assert.IsFalse(invalidData.Succeeded);
        Assert.AreEqual("invalid-data", invalidData.ErrorCategory);
        Assert.AreEqual("ActionInvalidData", invalidData.MessageResourceKey);
        Assert.IsFalse(outputUnavailable.Succeeded);
        Assert.AreEqual("output-unavailable", outputUnavailable.ErrorCategory);
        Assert.AreEqual("ActionOutputUnavailable", outputUnavailable.MessageResourceKey);
    }

    [TestMethod]
    public async Task RegistryMapsCancellationToLocalizedCancellation()
    {
        var registry = new ItemActionRegistry(
        [new ThrowingAction(ItemActionId.HashSha256, new OperationCanceledException())]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await registry.ExecuteAsync(
            ItemActionId.HashSha256,
            new ItemActionContext(new ItemSelectionSnapshot([])),
            cancellation.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("cancelled", result.ErrorCategory);
        Assert.AreEqual("ActionCancelled", result.MessageResourceKey);
    }

    private sealed class TestAction(ItemActionId id, int order) : IItemAction
    {
        public ItemActionDescriptor Descriptor { get; } = new(id, "Test", "Test", ItemActionGroup.General, order, false, false);

        public ItemActionCapability Evaluate(ItemSelectionSnapshot selection) => new(true, null, Descriptor);

        public Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ItemActionResult.Success());
    }

    private sealed class ThrowingAction(ItemActionId id, Exception exception) : IItemAction
    {
        public ItemActionDescriptor Descriptor { get; } = new(id, "Test", "Test", ItemActionGroup.General, 1, false, false);

        public ItemActionCapability Evaluate(ItemSelectionSnapshot selection) => new(true, null, Descriptor);

        public Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default) =>
            Task.FromException<ItemActionResult>(exception);
    }
}
