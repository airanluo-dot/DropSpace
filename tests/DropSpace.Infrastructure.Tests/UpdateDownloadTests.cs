using System.Net;
using System.Security.Cryptography;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Storage;
using DropSpace.Infrastructure.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class UpdateDownloadTests
{
    private readonly List<string> _roots = [];

    [TestCleanup]
    public void Cleanup()
    {
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
    public async Task WrongHash_WrongSize_AndInterruptedStream_AreNeverPromoted()
    {
        var bytes = Enumerable.Repeat((byte)7, 8192).ToArray();
        var wrongHash = new string('a', 64);
        var (hashDownloader, hashCandidate, hashPaths) = Create(bytes, bytes.Length, wrongHash);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => hashDownloader.DownloadAsync(hashCandidate));
        AssertNoExecutable(hashPaths);

        var (sizeDownloader, sizeCandidate, sizePaths) = Create(bytes, bytes.Length + 1, Hash(bytes));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => sizeDownloader.DownloadAsync(sizeCandidate));
        AssertNoExecutable(sizePaths);

        var (interruptedDownloader, interruptedCandidate, interruptedPaths) = Create(
            bytes,
            bytes.Length,
            Hash(bytes),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowingReadStream(bytes, bytes.Length / 2)),
            });
        await Assert.ThrowsExactlyAsync<IOException>(() => interruptedDownloader.DownloadAsync(interruptedCandidate));
        AssertNoExecutable(interruptedPaths);
    }

    [TestMethod]
    public async Task CancellationAndTransportFailure_CleanPartialPayload()
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
            26100,
            false,
            null,
            new UpdateManifestAsset("DropSpaceSetup.exe", 1, new string('b', 64)),
            descriptor);
        var candidate = new UpdateCandidate(release, manifest, asset, DeploymentMode.Portable);
        var client = new HttpClient(new FakeHandler(response));
        var store = new UpdateStateStore(paths);
        return (new HttpUpdateDownloader(client, paths, store), candidate, paths);
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
