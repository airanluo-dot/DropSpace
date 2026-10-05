using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DropSpace.Core.Compatibility;
using DropSpace.Infrastructure.Downloads;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Storage;
using DropSpace.Infrastructure.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class UpdateDownloadTests
{
    private readonly List<string> _roots = [];
    private readonly List<IDisposable> _resources = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var resource in _resources) resource.Dispose();
        foreach (var root in _roots.Where(Directory.Exists)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task CorrectStreamingPayload_IsAtomicallyPromotedAndPersisted()
    {
        var bytes = Enumerable.Range(0, 200_000).Select(index => (byte)(index % 251)).ToArray();
        var (downloader, candidate, paths) = Create(bytes, bytes.Length, Hash(bytes));

        var result = await downloader.DownloadAsync(candidate);

        Assert.IsTrue(File.Exists(result.FilePath));
        Assert.IsFalse(File.Exists(string.Concat(result.FilePath, ".download")));
        Assert.AreEqual(bytes.Length, new FileInfo(result.FilePath).Length);
        Assert.IsTrue(File.Exists(Path.Combine(paths.Updates, "0.1.1", "update-state.json")));
    }

    [TestMethod]
    public async Task WrongHashAndWrongSize_AreNeverPromotedOrRetained()
    {
        var bytes = Enumerable.Repeat((byte)7, 8192).ToArray();
        var wrongHash = new string('a', 64);
        var (hashDownloader, hashCandidate, hashPaths) = Create(bytes, bytes.Length, wrongHash);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => hashDownloader.DownloadAsync(hashCandidate));
        AssertNoExecutable(hashPaths);

        var (sizeDownloader, sizeCandidate, sizePaths) = Create(bytes, bytes.Length + 1, Hash(bytes));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => sizeDownloader.DownloadAsync(sizeCandidate));
        AssertNoExecutable(sizePaths);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedStream_RetriesWithValidatorAndSafelyHandlesIgnoredRange(bool ignoreRange)
    {
        var bytes = Enumerable.Range(0, 128_000).Select(i => (byte)(i % 239)).ToArray();
        var attempts = 0;
        long requestedOffset = -1;
        var (downloader, candidate, _) = Create(bytes, bytes.Length, Hash(bytes), request =>
        {
            // A real range probe precedes sequential fallback for this small file.
            if (request.Headers.Range?.Ranges.Single().To == 0)
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            attempts++;
            HttpResponseMessage response;
            if (attempts == 1)
            {
                response = new(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingReadStream(bytes, bytes.Length / 2)) };
                response.Content.Headers.ContentLength = bytes.Length;
            }
            else
            {
                requestedOffset = request.Headers.Range!.Ranges.Single().From!.Value;
                Assert.AreEqual("\"version-1\"", request.Headers.IfRange?.EntityTag?.ToString());
                response = new(ignoreRange ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(ignoreRange ? bytes : bytes[(int)requestedOffset..]) };
                if (!ignoreRange) response.Content.Headers.ContentRange = new(requestedOffset, bytes.Length - 1, bytes.Length);
            }
            response.Headers.ETag = new("\"version-1\"");
            return response;
        });
        var result = await downloader.DownloadAsync(candidate);
        Assert.AreEqual(bytes.Length / 2, requestedOffset);
        Assert.AreEqual(2, attempts);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(result.FilePath));
        Assert.IsFalse(File.Exists(result.FilePath + ".download"));
        Assert.IsFalse(File.Exists(result.FilePath + ".download.resume.json"));
    }

    [TestMethod]
    public async Task UntrustedRedirect_IsRejectedBeforeFollowingIt()
    {
        byte[] bytes = [1, 2, 3];
        var requests = 0;
        var (downloader, candidate, paths) = Create(bytes, bytes.Length, Hash(bytes), _ =>
        {
            requests++;
            return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/update.exe") } };
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => downloader.DownloadAsync(candidate));
        Assert.AreEqual(1, requests);
        AssertNoExecutable(paths);
    }

    [TestMethod]
    public async Task UpdateUsesSharedTransferQueueAndParallelRanges()
    {
        var bytes = Enumerable.Range(0, 5 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var requests = 0;
        var ranges = 0;
        var (downloader, candidate, _) = Create(bytes, bytes.Length, Hash(bytes), request =>
        {
            Interlocked.Increment(ref requests);
            var range = request.Headers.Range!.Ranges.Single();
            long from = range.From!.Value, to = range.To!.Value;
            if (to > 0) Interlocked.Increment(ref ranges);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(bytes[(int)from..((int)to + 1)]) };
            response.Headers.ETag = new("\"parallel-version\"");
            response.Content.Headers.ContentRange = new(from, to, bytes.Length);
            return response;
        });
        var shared = _resources.OfType<HttpRangeDownloader>().Last();
        shared.Transfers.SetLimit(1);
        using var otherDownload = await shared.Transfers.AcquireAsync(CancellationToken.None);
        var task = downloader.DownloadAsync(candidate);
        Assert.IsFalse(task.IsCompleted);
        Assert.AreEqual(0, requests);
        otherDownload.Dispose();
        var result = await task;
        Assert.IsGreaterThan(1, ranges);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(result.FilePath));
        Assert.AreEqual(0, shared.Connections.Active);
        Assert.AreEqual(0, shared.Transfers.Active);
    }

    [TestMethod]
    public async Task CancellationAndTransportFailure_DoNotPromotePayload()
    {
        var bytes = Enumerable.Repeat((byte)9, 1024).ToArray();
        var (cancelDownloader, cancelCandidate, cancelPaths) = Create(
            bytes,
            bytes.Length,
            Hash(bytes),
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelDownloader.DownloadAsync(cancelCandidate, cancellationToken: cancellation.Token));
        AssertNoExecutable(cancelPaths);

        var (failedDownloader, failedCandidate, failedPaths) = Create(
            bytes,
            bytes.Length,
            Hash(bytes),
            (_, _) => throw new HttpRequestException("offline"));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => failedDownloader.DownloadAsync(failedCandidate));
        AssertNoExecutable(failedPaths);
    }

    [TestMethod]
    public async Task TimeoutAndDiskFailure_LeaveCurrentVersionUntouched()
    {
        var bytes = Enumerable.Repeat((byte)3, 1024).ToArray();
        var (timeoutDownloader, timeoutCandidate, timeoutPaths) = Create(
            bytes,
            bytes.Length,
            Hash(bytes),
            (_, _) => throw new TaskCanceledException("simulated HTTP timeout"));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => timeoutDownloader.DownloadAsync(timeoutCandidate));
        AssertNoExecutable(timeoutPaths);

        var (diskDownloader, diskCandidate, diskPaths) = Create(bytes, bytes.Length, Hash(bytes));
        Directory.CreateDirectory(diskPaths.Root);
        await File.WriteAllTextAsync(diskPaths.Updates, "blocks directory creation");
        await Assert.ThrowsExactlyAsync<IOException>(() => diskDownloader.DownloadAsync(diskCandidate));
        Assert.IsFalse(Directory.Exists(diskPaths.Updates));
    }

    [TestMethod]
    public async Task DropSpaceOwnedPathContainment_RejectsManifestPathCharacters()
    {
        byte[] bytes = [byte.MaxValue];
        var (downloader, candidate, _) = Create(bytes, bytes.Length, Hash(bytes));
        var malicious = candidate with
        {
            Manifest = candidate.Manifest with
            {
                Portable = candidate.Manifest.Portable with { AssetName = "..\\DropSpace.exe" },
            },
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => downloader.DownloadAsync(malicious));
    }

    [TestMethod]
    public async Task LinkedVersionDirectoryCannotReplaceAnExternalExecutable()
    {
        byte[] bytes = [1, 2, 3];
        var (downloader, candidate, paths) = Create(bytes, bytes.Length, Hash(bytes));
        paths.EnsureCreated();
        var external = Path.Combine(paths.Root, "external-source");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "DropSpace.exe");
        await File.WriteAllTextAsync(sentinel, "preserve external executable");
        var linkedVersion = Path.Combine(paths.Updates, candidate.Manifest.Version.ToString());
        try
        {
            try { Directory.CreateSymbolicLink(linkedVersion, external); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Inconclusive($"Directory links are unavailable: {exception.GetType().Name}");
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => downloader.DownloadAsync(candidate));
            Assert.AreEqual("preserve external executable", await File.ReadAllTextAsync(sentinel));
            Assert.AreEqual(1, Directory.GetFiles(external).Length);
        }
        finally
        {
            if (Directory.Exists(linkedVersion)) Directory.Delete(linkedVersion);
        }
    }

    private (HttpUpdateDownloader Downloader, UpdateCandidate Candidate, AppStoragePaths Paths) Create(
        byte[] bytes,
        long expectedSize,
        string expectedHash,
        Func<HttpRequestMessage, HttpResponseMessage>? response = null)
    {
        return Create(bytes, expectedSize, expectedHash, (request, _) =>
            Task.FromResult(response?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            }));
    }

    private (HttpUpdateDownloader Downloader, UpdateCandidate Candidate, AppStoragePaths Paths) Create(
        byte[] bytes,
        long expectedSize,
        string expectedHash,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-update-tests", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var paths = new AppStoragePaths(root);
        var version = ReleaseVersion.Parse("0.1.1");
        var url = new Uri("https://github.com/airanluo-dot/DropSpace/releases/download/v0.1.1/DropSpace.exe");
        var asset = new UpdateReleaseAsset("DropSpace.exe", expectedSize, url);
        var release = new UpdateRelease(
            "v0.1.1",
            false,
            false,
            DateTimeOffset.UtcNow,
            new Uri("https://github.com/airanluo-dot/DropSpace/releases/tag/v0.1.1"),
            [asset]);
        var descriptor = new UpdateManifestAsset("DropSpace.exe", expectedSize, expectedHash);
        var manifest = new UpdateManifest(
            1,
            UpdateChannel.Stable,
            version,
            version.ToVersionCode(),
            DateTimeOffset.UtcNow,
            WindowsCompatibilityPolicy.MinimumSupportedWindowsBuild,
            false,
            null,
            new UpdateManifestAsset("DropSpaceSetup.exe", 1, new string('b', 64)),
            descriptor);
        var candidate = new UpdateCandidate(release, manifest, asset, DeploymentMode.Portable);
        var client = new HttpClient(new FakeHandler(response));
        var store = new UpdateStateStore(paths);
        var shared = new HttpRangeDownloader();
        var downloader = new HttpUpdateDownloader(client, paths, store, shared);
        _resources.Add(downloader);
        _resources.Add(shared);
        return (downloader, candidate, paths);
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void AssertNoExecutable(AppStoragePaths paths)
    {
        if (!Directory.Exists(paths.Updates)) return;
        Assert.IsEmpty(Directory.GetFiles(paths.Updates, "DropSpace.exe", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetFiles(paths.Updates, "*.download", SearchOption.AllDirectories));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class ThrowingReadStream(byte[] data, int failAfter) : MemoryStream(data, writable: false)
    {
        private int _read;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= failAfter) throw new IOException("Simulated interrupted stream.");
            var read = base.Read(buffer, offset, Math.Min(count, failAfter - _read));
            _read += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_read >= failAfter) throw new IOException("Simulated interrupted stream.");
            var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, failAfter - _read)], cancellationToken);
            _read += read;
            return read;
        }
    }
}
