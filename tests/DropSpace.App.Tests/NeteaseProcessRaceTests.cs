using DropSpace.App.Services.NeteaseEnhancement;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NeteaseProcessRaceTests
{
    private const string Expected = @"C:\fixture\cloudmusic.exe";

    [TestMethod]
    public async Task StopReenumeratesChildrenSpawnedDuringPreviousExitWait()
    {
        int calls = 0;
        await InfLinkDeploymentService.DrainProcessGenerationsAsync(_ => Task.FromResult(++calls <= 3), CancellationToken.None);
        Assert.AreEqual(4, calls);
    }

    [TestMethod]
    public async Task ContinuouslyRespawningPlayerFailsWithinGenerationBound()
    {
        int calls = 0;
        var error = await Assert.ThrowsExactlyAsync<EnhancementDeploymentException>(() => InfLinkDeploymentService.DrainProcessGenerationsAsync(_ => { calls++; return Task.FromResult(true); }, CancellationToken.None));
        Assert.AreEqual("PlayerCloseTimedOut", error.Code);
        Assert.AreEqual(8, calls);
    }

    [TestMethod]
    public async Task StopCancellationDoesNotBecomeSuccessOrTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => InfLinkDeploymentService.DrainProcessGenerationsAsync(_ => throw new AssertFailedException(), cancellation.Token));
    }

    [TestMethod]
    public void ExitedBeforeInspectionDoesNotReadMainModule()
    {
        Assert.IsFalse(InfLinkDeploymentService.IsMatchingLiveProcess(() => true, () => throw new AssertFailedException(), Expected));
    }

    [TestMethod]
    public void ExitDuringMainModuleInspectionIsNotUnavailable()
    {
        bool exited = false;
        Assert.IsFalse(InfLinkDeploymentService.IsMatchingLiveProcess(() => exited, () =>
        {
            exited = true;
            throw new InvalidOperationException();
        }, Expected));
    }

    [TestMethod]
    public void ExitedAfterPathReadIsNotRunning()
    {
        bool exited = false;
        Assert.IsFalse(InfLinkDeploymentService.IsMatchingLiveProcess(() => exited, () => { exited = true; return Expected; }, Expected));
    }

    [TestMethod]
    public void LiveInaccessibleProcessStillFailsClosed()
    {
        var error = Assert.ThrowsExactly<EnhancementDeploymentException>(() => InfLinkDeploymentService.IsMatchingLiveProcess(() => false, () => throw new System.ComponentModel.Win32Exception(5), Expected));
        Assert.AreEqual("PlayerProcessUnavailable", error.Code);
    }

    [TestMethod]
    public void SameNameAtOtherPathDoesNotMatch()
    {
        Assert.IsFalse(InfLinkDeploymentService.IsMatchingLiveProcess(() => false, () => @"C:\other\cloudmusic.exe", Expected));
        Assert.IsTrue(InfLinkDeploymentService.IsMatchingLiveProcess(() => false, () => Expected, Expected));
    }
}
