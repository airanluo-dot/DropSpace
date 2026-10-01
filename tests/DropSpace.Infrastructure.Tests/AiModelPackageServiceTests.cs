using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AiModelPackageServiceTests
{
    private static readonly byte[] Payload = [1, 2, 3];
    private static readonly AiLyricsModelDescriptor Model = new("test", "test", new("https://huggingface.co/test/model"), 3,
        Convert.ToHexString(SHA256.HashData(Payload)));

    [TestMethod]
    public async Task DeleteRemovesOnlySelectedModelAndItsPartialDownload()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new AiModelPackageService(root, new Handler(_ => throw new AssertFailedException("Delete cannot use network.")), _ => Model);
            await File.WriteAllBytesAsync(Path.Combine(root, Model.Sha256 + ".gguf"), Payload);
            await File.WriteAllBytesAsync(Path.Combine(root, Model.Sha256 + ".partial"), [1]);
            await File.WriteAllBytesAsync(Path.Combine(root, "other.gguf"), [9]);
            await service.DeleteAsync(Model.Id, CancellationToken.None);
            Assert.IsNull(await service.GetInstalledPathAsync(Model.Id, CancellationToken.None));
            Assert.IsFalse(File.Exists(Path.Combine(root, Model.Sha256 + ".partial")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "other.gguf")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RequiresConsentBeforeAnyNetworkRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var service = new AiModelPackageService(root, handler, _ => Model);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync("test", false, null, CancellationToken.None));
        Assert.AreEqual(0, handler.Count);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task VerifiedDownloadIsInstalledAndReusedWithoutNetwork()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
            using var service = new AiModelPackageService(root, handler, _ => Model);
            var path = await service.DownloadAsync("test", true, null, CancellationToken.None);
            CollectionAssert.AreEqual(Payload, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(path, await service.DownloadAsync("test", true, null, CancellationToken.None));
            Assert.AreEqual(1, handler.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ResumeValidatesRangeAndFullHash()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, Model.Sha256 + ".partial"), [1]);
            var handler = new Handler(request =>
            {
                Assert.AreEqual(1L, request.Headers.Range!.Ranges.Single().From);
                var content = new ByteArrayContent([2, 3]);
                content.Headers.ContentRange = new ContentRangeHeaderValue(1, 2, 3);
                return new(HttpStatusCode.PartialContent) { Content = content };
            });
            using var service = new AiModelPackageService(root, handler, _ => Model);
            var path = await service.DownloadAsync("test", true, null, CancellationToken.None);
            CollectionAssert.AreEqual(Payload, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RejectedRangeRetriesOnceFromZero()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, Model.Sha256 + ".partial"), [1]);
            var handler = new Handler(request => request.Headers.Range is not null
                ? new(HttpStatusCode.RequestedRangeNotSatisfiable)
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
            using var service = new AiModelPackageService(root, handler, _ => Model);
            var path = await service.DownloadAsync("test", true, null, CancellationToken.None);
            CollectionAssert.AreEqual(Payload, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(2, handler.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UntrustedRedirectAndWrongHashNeverInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Handler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new("https://example.invalid/model");
                return response;
            });
            using (var service = new AiModelPackageService(root, handler, _ => Model))
                await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync("test", true, null, CancellationToken.None));
            Assert.AreEqual(1, handler.Count);
            using (var service = new AiModelPackageService(root, new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9, 9]) }), _ => Model))
                await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync("test", true, null, CancellationToken.None));
            Assert.IsFalse(File.Exists(Path.Combine(root, Model.Sha256 + ".gguf")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DisposeCancelsActiveAndQueuedDownloads()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var handler = new BlockingHandler();
        using var service = new AiModelPackageService(root, handler, _ => Model);
        try
        {
            var active = service.DownloadAsync("test", true, null, CancellationToken.None);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = service.DownloadAsync("test", true, null, CancellationToken.None);
            service.Dispose();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await active.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, handler.Count);
            Assert.IsFalse(File.Exists(Path.Combine(root, Model.Sha256 + ".gguf")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(respond(request));
        }
    }
}
