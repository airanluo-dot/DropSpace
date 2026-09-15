using System.Text.Json;
using DropSpace.Core.Updates;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class BetaMigrationTests
{
    [TestMethod]
    public void LegacyAndBetaSequenceUsesNumericOrderBeforeStable()
    {
        var tags = new[] { "v0.3.0-preview.22", "v0.3.0-preview.23", "v0.3.0-beta.24", "v0.3.0-beta.25", "v0.3.0" };
        for (var i = 0; i < tags.Length; i++)
        {
            var parsed = ReleaseVersion.Parse(tags[i]);
            Assert.AreEqual(tags[i], parsed.ToTagString());
            if (i > 0) Assert.IsTrue(parsed > ReleaseVersion.Parse(tags[i - 1]));
        }
        Assert.AreEqual(0, ReleaseVersion.Parse("0.3.0-preview.24").CompareTo(ReleaseVersion.Parse("0.3.0-beta.24")));
        Assert.AreEqual(new Version(0, 3, 0, 24), ReleaseVersion.Parse(tags[2]).ToPackageVersion());
    }

    [TestMethod]
    [DataRow("\"Preview\"")]
    [DataRow("\"preview\"")]
    [DataRow("1")]
    [DataRow("\"Beta\"")]
    public void LegacyChannelReadsAsBetaAndWritesNewName(string json)
    {
        var channel = JsonSerializer.Deserialize<UpdateChannel>(json);
        Assert.AreEqual(UpdateChannel.Beta, channel);
        Assert.AreEqual("\"Beta\"", JsonSerializer.Serialize(channel));
    }

    [TestMethod]
    public void BetaChannelSelectsBeta24ForLegacyInstalledVersion()
    {
        var release = new UpdateRelease("v0.3.0-beta.24", false, true, DateTimeOffset.UtcNow, new Uri("https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.0-beta.24"), []);
        Assert.AreSame(release, UpdateReleaseSelector.SelectHighest(ReleaseVersion.Parse("v0.3.0-preview.23"), UpdateChannel.Beta, [release]));
        Assert.IsNull(UpdateReleaseSelector.SelectHighest(ReleaseVersion.Parse("v0.3.0-preview.23"), UpdateChannel.Stable, [release]));
    }
}
