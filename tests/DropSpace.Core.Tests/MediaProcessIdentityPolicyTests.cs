using DropSpace.Core.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class MediaProcessIdentityPolicyTests
{
    [TestMethod]
    public void AppleAudioUsesThePackagedLibraryServerWithoutRedirectingOtherApps()
    {
        Assert.AreEqual("AppleInc.AppleMusicWin_nzyj5cx40ttqa!LibraryServer", MediaProcessIdentityPolicy.AudioIdentity("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"));
        Assert.AreEqual("cloudmusic.exe", MediaProcessIdentityPolicy.AudioIdentity("cloudmusic.exe"));
        Assert.AreEqual("Other.AppleMusic!App", MediaProcessIdentityPolicy.AudioIdentity("Other.AppleMusic!App"));
    }
}
