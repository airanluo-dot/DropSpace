using DropSpace.Core.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class UpdateVersionTests
{
    [TestMethod]
    public void ReleaseVersion_UsesSemVerOrderingInsteadOfStrings()
    {
        var values = new[]
        {
            "0.1.0-preview.1", "0.1.0-preview.2", "0.1.0-preview.10",
            "0.1.0", "0.1.1-preview.1", "0.1.1", "1.0.0",
        }.Select(ReleaseVersion.Parse).ToArray();

        for (var index = 1; index < values.Length; index++)
        {
            Assert.IsTrue(values[index] > values[index - 1]);
        }
        Assert.AreEqual("0.1.0.9999", ReleaseVersion.Parse("0.1.0").ToPackageVersion().ToString());
        Assert.AreEqual(1_009_999, ReleaseVersion.Parse("0.1.0").ToVersionCode());
    }

    [TestMethod]
    [DataRow("0.1.0-preview.5", UpdateChannel.Preview, "0.1.0-preview.6|0.1.0", "0.1.0")]
    [DataRow("0.1.0", UpdateChannel.Preview, "0.1.1-preview.1", "0.1.1-preview.1")]
    [DataRow("0.1.1-preview.3", UpdateChannel.Preview, "0.1.1-preview.4|0.1.1", "0.1.1")]
    [DataRow("0.1.2-preview.5", UpdateChannel.Preview, "0.1.2|0.1.3-preview.1", "0.1.3-preview.1")]
    [DataRow("0.1.2", UpdateChannel.Stable, "0.1.3-preview.5", null)]
    [DataRow("0.1.2", UpdateChannel.Stable, "0.1.3-preview.5|0.1.3", "0.1.3")]
    [DataRow("0.2.0-preview.2", UpdateChannel.Stable, "0.1.9|0.2.0-preview.3", null)]
    public void ChannelSelection_ReturnsHighestEligibleVersionWithoutDowngrade(
        string current,
        UpdateChannel channel,
        string candidates,
        string? expected)
    {
        var releases = candidates.Split('|').Select(CreateRelease).ToArray();
        var actual = UpdateReleaseSelector.SelectHighest(ReleaseVersion.Parse(current), channel, releases);

        Assert.AreEqual(expected, actual is null ? null : ReleaseVersion.Parse(actual.TagName).ToString());
    }

    [TestMethod]
    public void DeploymentMode_UsesInstallerRegistrationBeforeSparsePackageIdentity()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DropSpace-installed"));
        Assert.AreEqual(DeploymentMode.Installer, DeploymentModeResolver.Resolve(true, root, root));
        Assert.AreEqual(DeploymentMode.Packaged, DeploymentModeResolver.Resolve(true, null, root));
        Assert.AreEqual(DeploymentMode.Portable, DeploymentModeResolver.Resolve(false, null, root));
    }

    [TestMethod]
    public void InstallerArguments_AreStructuredForSafeInnoUpdateMode()
    {
        var log = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DropSpace Updates", "install.log"));
        var arguments = UpdateInstallerArguments.Create(log);

        CollectionAssert.AreEqual(
            new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATE", $"/LOG={log}" },
            arguments.ToArray());
    }

    private static UpdateRelease CreateRelease(string value)
    {
        var version = ReleaseVersion.Parse(value);
        return new UpdateRelease(
            version.ToTagString(),
            false,
            version.IsPreview,
            DateTimeOffset.UtcNow,
            new Uri($"https://github.com/airanluo-dot/DropSpace/releases/tag/{version.ToTagString()}"),
            []);
    }
}
