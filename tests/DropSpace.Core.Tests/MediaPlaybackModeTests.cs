using DropSpace.Core.Media;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class MediaPlaybackModeTests
{
    [TestMethod]
    public void PlaybackModesDefaultToUnknownAndUnsupported()
    {
        Assert.IsNull(MediaSessionSnapshot.Empty.ShuffleActive);
        Assert.IsNull(MediaSessionSnapshot.Empty.RepeatMode);
        Assert.IsFalse(MediaSessionSnapshot.Empty.CanChangeShuffle);
        Assert.IsFalse(MediaSessionSnapshot.Empty.CanChangeRepeat);
    }

    [TestMethod]
    public void PlaybackModeChangesDoNotCreateANewTrackOrRestartLyrics()
    {
        var original = MediaSessionSnapshot.Empty with { SessionId = "session", TrackTitle = "Track", Artist = "Artist" };
        var changed = original with { ShuffleActive = true, RepeatMode = MediaRepeatMode.List, CanChangeShuffle = true, CanChangeRepeat = true };
        Assert.AreEqual(original.TrackIdentity, changed.TrackIdentity);
        Assert.IsTrue(original.IsSameTrack(changed));
    }
}
