using DropSpace.Core.Transfer;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class ClipboardImageBudgetPolicyTests
{
    [TestMethod]
    public void TwentyAndFiftyMegapixelImagesFitTheDefaultBudget()
    {
        var budget = ClipboardImageBudgetPolicy.Create(25L * 1024 * 1024, 50_000_000);

        Assert.IsTrue(budget.Assess(10L * 1024 * 1024, 4_000, 5_000).IsWithinBudget);
        Assert.IsTrue(budget.Assess(25L * 1024 * 1024, 5_000, 10_000).IsWithinBudget);
    }

    [TestMethod]
    public void HundredMegapixelsAreRejectedBeforeDecode()
    {
        var budget = ClipboardImageBudgetPolicy.Create(25L * 1024 * 1024, 100_000_000);

        var assessment = budget.Assess(20L * 1024 * 1024, 10_000, 10_000);

        Assert.IsFalse(assessment.IsWithinBudget);
        Assert.AreEqual("image-budget-limit", assessment.ErrorCategory);
        Assert.AreEqual(400_000_000L, assessment.DecodedBytes);
    }

    [TestMethod]
    public void CompressedDimensionAndOverflowLimitsAreRejected()
    {
        var budget = ClipboardImageBudgetPolicy.Create(25L * 1024 * 1024, 50_000_000);

        Assert.AreEqual(
            "image-budget-limit",
            budget.Assess(26L * 1024 * 1024, 4_000, 5_000).ErrorCategory);
        Assert.AreEqual(
            "image-budget-limit",
            budget.Assess(1, ClipboardImageBudgetPolicy.DefaultMaxDimension + 1, 1).ErrorCategory);

        var overflowBudget = new ClipboardImageBudget(
            1,
            long.MaxValue,
            int.MaxValue,
            long.MaxValue,
            long.MaxValue,
            1,
            1);
        Assert.AreEqual(
            "image-budget-overflow",
            overflowBudget.Assess(1, int.MaxValue, int.MaxValue).ErrorCategory);
    }
}
