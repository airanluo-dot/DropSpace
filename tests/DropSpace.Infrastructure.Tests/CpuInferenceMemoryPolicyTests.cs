using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

// Tiny process fixtures do not measure the test host or allocate model memory.
internal static class TestInferenceMemory
{
    internal static CpuMemorySnapshot? Sufficient() => new(32L << 30, 32L << 30);
}

[TestClass]
public sealed class CpuInferenceMemoryPolicyTests
{
    [TestMethod]
    [DataRow(3, 4)]
    [DataRow(12, 13)]
    public void BothAvailablePhysicalAndCommitMustMeetSeparateModelThresholds(int maximumGiB, int requiredGiB)
    {
        var maximum = (long)maximumGiB << 30;
        var required = (long)requiredGiB << 30;
        Assert.AreEqual(required, CpuInferenceMemoryPolicy.RequiredAvailableBytes(maximum));
        CpuInferenceMemoryPolicy.EnsureAvailable(maximum, () => new(required, required));
        foreach (var insufficient in new[] { -1L, 0, required - 1 })
        {
            Assert.ThrowsExactly<InferenceResourcesUnavailableException>(() =>
                CpuInferenceMemoryPolicy.EnsureAvailable(maximum, () => new(insufficient, required)));
            Assert.ThrowsExactly<InferenceResourcesUnavailableException>(() =>
                CpuInferenceMemoryPolicy.EnsureAvailable(maximum, () => new(required, insufficient)));
        }
    }

    [TestMethod]
    public void UnknownAndFailedMeasurementsFailClosedWithoutDisclosingProbeDetails()
    {
        const long maximum = 3L << 30;
        Assert.ThrowsExactly<InferenceResourcesUnavailableException>(() =>
            CpuInferenceMemoryPolicy.EnsureAvailable(maximum, () => null));
        var error = Assert.ThrowsExactly<InferenceResourcesUnavailableException>(() =>
            CpuInferenceMemoryPolicy.EnsureAvailable(maximum, () => throw new IOException("private diagnostic")));
        Assert.IsFalse(error.Message.Contains("private diagnostic", StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            CpuInferenceMemoryPolicy.EnsureAvailable(maximum, () => throw new OperationCanceledException()));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(12L * 1024 * 1024 * 1024 + 1)]
    [DataRow(long.MaxValue)]
    public void InvalidProcessLimitsCannotOverflowAdmission(long maximum)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CpuInferenceMemoryPolicy.RequiredAvailableBytes(maximum));
    }
}
