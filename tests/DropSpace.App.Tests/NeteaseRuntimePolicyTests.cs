using DropSpace.App.Services.NeteaseEnhancement;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NeteaseRuntimePolicyTests
{
    [TestMethod]
    [DataRow("http://aka.ms/vs/17/release/VC_redist.x64.exe")]
    [DataRow("https://aka.ms.evil.example/payload.exe")]
    [DataRow("https://aka.ms:8443/payload.exe")]
    [DataRow("https://user@aka.ms/payload.exe")]
    [DataRow("https://example.com/VC_redist.x64.exe")]
    public void PrerequisiteRejectsUntrustedDownloadLocations(string url) => Assert.IsFalse(NeteaseRuntimeInstaller.IsOfficial(new Uri(url)));

    [TestMethod]
    public void PrerequisiteAllowsOnlyOfficialHttpsHosts()
    {
        Assert.IsTrue(NeteaseRuntimeInstaller.IsOfficial(new Uri("https://aka.ms/vs/17/release/VC_redist.x64.exe")));
        Assert.IsTrue(NeteaseRuntimeInstaller.IsOfficial(new Uri("https://download.visualstudio.microsoft.com/download/prerequisite.exe")));
    }
}
