using System.Text;
using System.Text.Json.Nodes;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class UpdateManifestTests
{
    private static readonly UpdateManifestParser Parser = new();

    [TestMethod]
    public void ValidManifest_IsAcceptedWithoutExecutableUrls()
    {
        var (release, json) = CreateValid();
        var manifest = Parser.ParseAndValidate(Encoding.UTF8.GetBytes(json.ToJsonString()), release);

        Assert.AreEqual(ReleaseVersion.Parse("0.1.1"), manifest.Version);
        Assert.AreEqual("DropSpaceSetup.exe", manifest.Installer.AssetName);
    }

    [TestMethod]
    [DataRow("schemaVersion", "2")]
    [DataRow("version", "\"invalid\"")]
    [DataRow("channel", "\"preview\"")]
    [DataRow("versionCode", "-1")]
    [DataRow("minimumWindowsBuild", "22000")]
    public void InvalidReleaseMetadata_FailsClosed(string property, string replacementJson)
    {
        var (release, json) = CreateValid();
        json[property] = JsonNode.Parse(replacementJson);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Parser.ParseAndValidate(Encoding.UTF8.GetBytes(json.ToJsonString()), release));
    }

    [TestMethod]
    [DataRow("size", "0")]
    [DataRow("size", "-1")]
    [DataRow("sha256", "\"xyz\"")]
    [DataRow("assetName", "\"Other.exe\"")]
    public void InvalidInstallerDescriptor_FailsClosed(string property, string replacementJson)
    {
        var (release, json) = CreateValid();
        json["installer"]![property] = JsonNode.Parse(replacementJson);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Parser.ParseAndValidate(Encoding.UTF8.GetBytes(json.ToJsonString()), release));
    }

    [TestMethod]
    public void UnknownUrlField_MalformedJson_AndOversize_FailClosed()
    {
        var (release, json) = CreateValid();
        json["downloadUrl"] = "https://attacker.invalid/update.exe";
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Parser.ParseAndValidate(Encoding.UTF8.GetBytes(json.ToJsonString()), release));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Parser.ParseAndValidate(Encoding.UTF8.GetBytes("{"), release));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Parser.ParseAndValidate(new byte[UpdateManifestParser.MaximumManifestBytes + 1], release));
    }

    [TestMethod]
    public void TagMismatch_MissingPortable_DuplicateInstaller_AndUnexpectedExecutable_FailClosed()
    {
        var (release, json) = CreateValid();
        var mismatched = release with { TagName = "v0.1.2" };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Parser.ParseAndValidate(Encoding.UTF8.GetBytes(json.ToJsonString()), mismatched));

        json.Remove("portable");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Parser.ParseAndValidate(Encoding.UTF8.GetBytes(json.ToJsonString()), release));

        var (validRelease, validJson) = CreateValid();
        var duplicate = validRelease with { Assets = [.. validRelease.Assets, validRelease.Assets.Single(asset => asset.Name == "DropSpaceSetup.exe")] };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Parser.ParseAndValidate(Encoding.UTF8.GetBytes(validJson.ToJsonString()), duplicate));

        var unexpected = validRelease with
        {
            Assets = [.. validRelease.Assets, new UpdateReleaseAsset("Unexpected.exe", 1, Official("v0.1.1", "Unexpected.exe"))],
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Parser.ParseAndValidate(Encoding.UTF8.GetBytes(validJson.ToJsonString()), unexpected));
    }

    private static (UpdateRelease Release, JsonObject Json) CreateValid()
    {
        const string installerHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string portableHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var release = new UpdateRelease(
            "v0.1.1",
            false,
            false,
            DateTimeOffset.Parse("2026-08-10T00:00:00Z"),
            new Uri("https://github.com/airanluo-dot/DropSpace/releases/tag/v0.1.1"),
            [
                new("DropSpaceSetup.exe", 100, Official("v0.1.1", "DropSpaceSetup.exe")),
                new("DropSpace.exe", 200, Official("v0.1.1", "DropSpace.exe")),
                new("update-manifest.json", 300, Official("v0.1.1", "update-manifest.json")),
            ]);
        var json = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["channel"] = "stable",
            ["version"] = "0.1.1",
            ["versionCode"] = ReleaseVersion.Parse("0.1.1").ToVersionCode(),
            ["publishedAt"] = "2026-08-10T00:00:00Z",
            ["minimumWindowsBuild"] = 26100,
            ["mandatory"] = false,
            ["summary"] = "Stable update",
            ["installer"] = new JsonObject { ["assetName"] = "DropSpaceSetup.exe", ["size"] = 100, ["sha256"] = installerHash },
            ["portable"] = new JsonObject { ["assetName"] = "DropSpace.exe", ["size"] = 200, ["sha256"] = portableHash },
        };
        return (release, json);
    }

    private static Uri Official(string tag, string name) =>
        new($"https://github.com/airanluo-dot/DropSpace/releases/download/{tag}/{name}");
}
