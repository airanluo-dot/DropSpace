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
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequiresConsentBeforeAnyNetworkRequest(bool largeModel)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        var model = largeModel ? AiLyricsModelCatalog.ExperimentalLargePlain : Model;
        using var service = new AiModelPackageService(root, handler, _ => model);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(model.Id, false, null, CancellationToken.None));
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

    [TestMethod]
    public void LargerModelHasABoundedBudgetWithoutChangingTheDefault()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(30), AiModelPackageService.DownloadBudget(AiLyricsModelCatalog.ExperimentalPlain));
        Assert.AreEqual(TimeSpan.FromHours(2), AiModelPackageService.DownloadBudget(AiLyricsModelCatalog.ExperimentalLargePlain));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(2_147_483_648L)]
    [DataRow(5_000_000_000L)]
    public void SevenBRangeAndLengthValidationKeepFull64BitValues(long offset)
    {
        var bytes = AiLyricsModelCatalog.ExperimentalLargePlain.Bytes;
        using var response = RangeResponse(offset, bytes);
        Assert.AreEqual(offset, AiModelPackageService.ValidateDownloadResponse(response, offset, bytes));
        Assert.AreEqual(bytes - offset, response.Content.Headers.ContentLength);
    }

    [TestMethod]
    [DataRow("start")]
    [DataRow("end")]
    [DataRow("total")]
    [DataRow("length")]
    public void SevenBResumeRejectsMismatched64BitHeaders(string changed)
    {
        var bytes = AiLyricsModelCatalog.ExperimentalLargePlain.Bytes;
        const long offset = 5_000_000_000;
        using var response = RangeResponse(offset, bytes);
        response.Content.Headers.ContentRange = changed switch
        {
            "start" => new(offset + 1, bytes - 1, bytes),
            "end" => new(offset, bytes - 2, bytes),
            "total" => new(offset, bytes - 1, bytes + 1),
            _ => response.Content.Headers.ContentRange,
        };
        if (changed == "length") response.Content.Headers.ContentLength = bytes - offset - 1;
        Assert.ThrowsExactly<InvalidDataException>(() => AiModelPackageService.ValidateDownloadResponse(response, offset, bytes));
    }

    [TestMethod]
    public async Task SevenBTruncatedResponseKeepsOnlyReceivedBytesForResume()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var model = AiLyricsModelCatalog.ExperimentalLargePlain;
        var partial = Path.Combine(root, model.Sha256 + ".partial");
        try
        {
            await File.WriteAllBytesAsync(partial, [1]);
            var handler = new Handler(request =>
            {
                Assert.AreEqual(1L, request.Headers.Range!.Ranges.Single().From);
                return RangeResponse(1, model.Bytes, [2, 3]);
            });
            using var service = new AiModelPackageService(root, handler, _ => model, _ => model.Bytes + 128L * 1024 * 1024);
            await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => service.DownloadAsync(model.Id, true, null, default));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(partial));
            Assert.IsFalse(File.Exists(Path.Combine(root, model.Sha256 + ".gguf")));
            Assert.AreEqual(1, handler.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SevenBReservesRuntimeSpaceBeforeRequestingOrChangingPartialFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var model = AiLyricsModelCatalog.ExperimentalLargePlain;
        var partial = Path.Combine(root, model.Sha256 + ".partial");
        const long runtimeBytes = 100L * 1024 * 1024;
        try
        {
            await File.WriteAllBytesAsync(partial, [1]);
            var handler = new Handler(_ => throw new AssertFailedException("Insufficient space must fail before a network request."));
            using var service = new AiModelPackageService(root, handler, _ => model,
                _ => model.Bytes - 1 + runtimeBytes + 64L * 1024 * 1024 - 1);
            await Assert.ThrowsExactlyAsync<IOException>(() => service.DownloadAsync(model.Id, true, null, default, runtimeBytes));
            CollectionAssert.AreEqual(new byte[] { 1 }, await File.ReadAllBytesAsync(partial));
            Assert.AreEqual(0, handler.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SevenBIgnoredRangeRechecksSpaceBeforeDiscardingPartialBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var model = AiLyricsModelCatalog.ExperimentalLargePlain;
        var partial = Path.Combine(root, model.Sha256 + ".partial");
        try
        {
            await File.WriteAllBytesAsync(partial, [1, 2, 3]);
            var handler = new Handler(request =>
            {
                Assert.AreEqual(3L, request.Headers.Range!.Ranges.Single().From);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentLength = model.Bytes;
                return response;
            });
            using var service = new AiModelPackageService(root, handler, _ => model,
                _ => model.Bytes - 3 + 64L * 1024 * 1024);
            await Assert.ThrowsExactlyAsync<IOException>(() => service.DownloadAsync(model.Id, true, null, default));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(partial));
            Assert.AreEqual(1, handler.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static HttpResponseMessage RangeResponse(long offset, long totalBytes, byte[]? payload = null)
    {
        var content = new ByteArrayContent(payload ?? []);
        content.Headers.ContentRange = new ContentRangeHeaderValue(offset, totalBytes - 1, totalBytes);
        content.Headers.ContentLength = totalBytes - offset;
        return new(HttpStatusCode.PartialContent) { Content = content };
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
