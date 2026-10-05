using System.Net;
using DropSpace.Infrastructure.Downloads;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ManagedDownloadSizeTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PackageBoundAppliesToRangeProbeAndUnknownLengthBody(bool ranges)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DropSpace-size-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(directory, "package.download");
        using var client = new HttpClient(new Handler(ranges));
        using var engine = new HttpRangeDownloader();
        try
        {
            var policy = new DownloadRequestPolicy(client, uri => uri.Host == "example.com");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => engine.DownloadAsync(
                new Uri("https://example.com/package"), staging, policy, null, CancellationToken.None,
                maximumBytes: 1024));
            Assert.IsTrue(!File.Exists(staging) || new FileInfo(staging).Length <= 1024);
            Assert.AreEqual(0, engine.Connections.Active);
            Assert.AreEqual(0, engine.Transfers.Active);
        }
        finally
        {
            HttpRangeDownloader.DeleteStagingFiles(staging);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private sealed class Handler(bool ranges) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(ranges ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
            if (ranges)
            {
                response.Headers.ETag = new("\"version\"");
                response.Content = new ByteArrayContent([1]);
                response.Content.Headers.ContentRange = new(0, 0, 8 * 1024 * 1024);
            }
            else response.Content = new UnknownLengthContent();
            return Task.FromResult(response);
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[2048]).AsTask();
    }
}
