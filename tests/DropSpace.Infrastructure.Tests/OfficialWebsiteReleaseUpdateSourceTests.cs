using System.Net;
using System.Text;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class OfficialWebsiteReleaseUpdateSourceTests
{
    [TestMethod]
    public async Task WebsiteContract_MapsOnlyMatchingOfficialGitHubAssets()
    {
        var json = """
            {
              "schemaVersion": 1,
              "generatedAt": "2026-08-12T00:00:00Z",
              "source": "github-releases",
              "releases": [{
                "tagName": "v0.2.0-preview.2",
                "name": "DropSpace v0.2.0-preview.2",
                "body": "Preview",
                "isDraft": false,
                "isPrerelease": true,
                "publishedAt": "2026-08-12T00:00:00Z",
                "htmlUrl": "https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.0-preview.2",
                "assets": [
                  { "name": "DropSpaceSetup.exe", "size": 42, "downloadUrl": "https://github.com/airanluo-dot/DropSpace/releases/download/v0.2.0-preview.2/DropSpaceSetup.exe" },
                  { "name": "update-manifest.json", "size": 43, "downloadUrl": "https://github.com/airanluo-dot/DropSpace/releases/download/v0.2.0-preview.2/update-manifest.json" }
                ]
              }]
            }
            """;
        var source = Create(json);

        var releases = await source.GetReleasesAsync();

        Assert.AreEqual(1, releases.Count);
        Assert.IsTrue(releases[0].IsPrerelease);
        Assert.AreEqual(42, releases[0].Assets.Single(asset => asset.Name == "DropSpaceSetup.exe").Size);
    }

    [TestMethod]
    public async Task WebsiteContract_RejectsWrongSchemaAndArbitraryExecutableHost()
    {
        var wrongSchema = Create("""{"schemaVersion":2,"releases":[]}""");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => wrongSchema.GetReleasesAsync());

        var attacker = Create("""
            {"schemaVersion":1,"releases":[{
              "tagName":"v0.2.0-preview.2","isDraft":false,"isPrerelease":true,
              "publishedAt":"2026-08-12T00:00:00Z",
              "htmlUrl":"https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.0-preview.2",
              "assets":[{"name":"DropSpaceSetup.exe","size":42,"downloadUrl":"https://attacker.invalid/update.exe"}]
            }]}
            """);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => attacker.GetReleasesAsync());
    }

    [TestMethod]
    public void EndpointAllowlist_ContainsOnlyTheOfficialGitHubPagesRoute()
    {
        Assert.IsTrue(OfficialWebsiteReleaseUpdateSource.IsOfficialEndpoint(
            new Uri("https://airanluo-dot.github.io/DropSpace/api/v1/releases.json")));
        Assert.IsFalse(OfficialWebsiteReleaseUpdateSource.IsOfficialEndpoint(
            new Uri("https://airanluo-dot.github.io.attacker.invalid/DropSpace/api/v1/releases.json")));
        Assert.IsFalse(OfficialWebsiteReleaseUpdateSource.IsOfficialEndpoint(
            new Uri("https://airanluo-dot.github.io/DropSpace/api/v2/releases.json")));
        Assert.IsFalse(OfficialWebsiteReleaseUpdateSource.IsOfficialEndpoint(
            new Uri("https://dropspace.pages.dev/api/v1/releases.json")));
    }

    [TestMethod]
    public async Task ResilientSource_FallsBackAfterPrimaryNetworkFailure()
    {
        var expected = new UpdateRelease(
            "v0.2.0-preview.2",
            false,
            true,
            DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
            new Uri("https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.0-preview.2"),
            []);
        var source = new ResilientUpdateSource(
            [new FailingSource(), new FixedSource([expected])],
            NullLogger<ResilientUpdateSource>.Instance);

        var releases = await source.GetReleasesAsync();

        Assert.AreEqual(1, releases.Count);
        Assert.AreEqual(expected, releases[0]);
    }

    [TestMethod]
    public async Task ReplicatedWebsiteSource_MergesNewerMirrorInsteadOfAcceptingStaleSuccess()
    {
        var oldRelease = Release("v0.2.0-preview.1");
        var newRelease = Release("v0.2.0-preview.2");
        var source = new ResilientUpdateSource(
            [new FixedSource([oldRelease]), new FixedSource([oldRelease, newRelease])],
            NullLogger<ResilientUpdateSource>.Instance,
            mergeReleaseMetadata: true);

        var releases = await source.GetReleasesAsync();

        Assert.AreEqual(2, releases.Count);
        Assert.IsTrue(releases.Any(release => release.TagName == newRelease.TagName));
    }

    private static OfficialWebsiteReleaseUpdateSource Create(string json) => new(
        new HttpClient(new ResponseHandler(json)),
        ReleaseVersion.Parse("0.2.0-preview.1"),
        new Uri("https://airanluo-dot.github.io/DropSpace/api/v1/releases.json"));

    private static UpdateRelease Release(string tag) => new(
        tag,
        false,
        true,
        DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
        new Uri($"https://github.com/airanluo-dot/DropSpace/releases/tag/{tag}"),
        []);

    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FailingSource : IUpdateSource
    {
        public Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("simulated primary outage");

        public Task<ReadOnlyMemory<byte>> GetManifestAsync(UpdateRelease release, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("simulated primary outage");
    }

    private sealed class FixedSource(IReadOnlyList<UpdateRelease> releases) : IUpdateSource
    {
        public Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(releases);

        public Task<ReadOnlyMemory<byte>> GetManifestAsync(UpdateRelease release, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
    }
}
