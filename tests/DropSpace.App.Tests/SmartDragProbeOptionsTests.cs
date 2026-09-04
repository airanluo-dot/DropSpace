using DropSpace.App.Services;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class SmartDragProbeOptionsTests
{
    [TestMethod]
    public void DefaultProbeOptionsStayBoundedAndSingleFlight()
    {
        var options = SmartDragProbeOptions.Default;

        options.Validate();

        Assert.AreEqual(144, options.OuterSizePixels);
        Assert.AreEqual(12, options.CenterHolePixels);
        Assert.AreEqual(1, options.MaximumSimultaneousProbes);
        Assert.AreEqual(TimeSpan.FromMilliseconds(60), options.HardLifetime);
    }

    [TestMethod]
    public void MoreThanOneProbeIsRejectedAsAnInvariantViolation()
    {
        var options = SmartDragProbeOptions.Default with { MaximumSimultaneousProbes = 2 };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => options.Validate());
    }
}
