using System.Net;
using System.Text;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class UpdateMetadataNullRegressionTests
{
    [TestMethod]
    [DataRow("website", "{\"schemaVersion\":1,\"releases\":[null]}")]
    [DataRow("website", "{\"schemaVersion\":1,\"releases\":[{\"tagName\":\"v0.3.0\",\"htmlUrl\":\"https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.0\",\"assets\":[null]}]}")]
    [DataRow("github", "[null]")]
    [DataRow("github", "[{\"tag_name\":\"v0.3.0\",\"html_url\":\"https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.0\",\"assets\":null}]")]
    [DataRow("github", "[{\"tag_name\":\"v0.3.0\",\"html_url\":\"https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.0\",\"assets\":[null]}]")]
    public async Task NullReleaseMetadataIsRejectedAndTheNextOfficialReplicaRemainsUsable(string sourceKind, string json)
    {
        using var client = new HttpClient(new ResponseHandler(json));
        IUpdateSource malformed = sourceKind == "website"
            ? new OfficialWebsiteReleaseUpdateSource(client, ReleaseVersion.Parse("0.2.0"),
                new("https://airanluo-dot.github.io/DropSpace/api/v1/releases.json"))
            : new GitHubReleaseUpdateSource(client, ReleaseVersion.Parse("0.2.0"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => malformed.GetReleasesAsync());

        var expected = new UpdateRelease("v0.3.0", false, false, null,
            new("https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.0"), []);
        foreach (var merge in new[] { false, true })
        {
            var resilient = new ResilientUpdateSource([malformed, new FixedSource(expected)],
                NullLogger<ResilientUpdateSource>.Instance, mergeReleaseMetadata: merge);
            var releases = await resilient.GetReleasesAsync();
            Assert.HasCount(1, releases);
            Assert.AreEqual(expected, releases[0]);
        }
    }

    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FixedSource(UpdateRelease release) : IUpdateSource
    {
        public Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UpdateRelease>>([release]);

        public Task<ReadOnlyMemory<byte>> GetManifestAsync(UpdateRelease selected, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
    }
}
