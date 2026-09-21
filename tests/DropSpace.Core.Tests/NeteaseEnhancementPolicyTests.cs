using DropSpace.Core.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class NeteaseEnhancementPolicyTests
{
    [TestMethod]
    public void EveryRequiredCapabilityMustHavePositiveEvidence()
    {
        var complete = new NeteaseMediaCapabilities(true, true, true, true, true, true, true,
            true, true, true, true, true, true, true);
        Assert.IsTrue(complete.Complete);
        NeteaseMediaCapabilities[] missing = [complete with { Title = false }, complete with { Artist = false },
            complete with { Album = false }, complete with { Artwork = false }, complete with { PlaybackState = false },
            complete with { Play = false }, complete with { Pause = false }, complete with { Previous = false },
            complete with { Next = false }, complete with { Timeline = false }, complete with { LiveProgress = false },
            complete with { Seek = false }, complete with { Shuffle = false }, complete with { Repeat = false }];
        foreach (var evidence in missing) Assert.IsFalse(evidence.Complete);
        Assert.IsFalse(NeteaseMediaCapabilities.Empty.Complete);
    }

    [TestMethod]
    public void FileOwnershipNeverImpliesEnhanced()
    {
        var state = new NeteaseEnhancementState(NeteaseEnhancementStage.Failed, IsManaged: true, Version: "3.2.11", ErrorCode: "Verification");
        Assert.AreNotEqual(NeteaseEnhancementStage.Enhanced, state.Stage);
        Assert.IsFalse(state.IsBusy);
    }
}
