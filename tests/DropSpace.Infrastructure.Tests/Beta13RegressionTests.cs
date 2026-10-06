using System.Net;
using System.Security.Cryptography;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta13RegressionTests
{
    [TestMethod]
    public async Task CommittedRecoveryAndPagedHistoryPreserveDeliveredFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-history-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "files"); Directory.CreateDirectory(output);
        var repository = new DownloadTaskRepository(Path.Combine(root, "journal"));
        var delivered = Path.Combine(output, "delivered.bin");
        await File.WriteAllBytesAsync(delivered, [1, 2, 3]);
        var committedId = Guid.NewGuid();
        await repository.UpsertAsync(new() { Id = committedId, Request = new(committedId, "https://example.com/file", output + Path.DirectorySeparatorChar, "delivered.bin"),
            OutputPath = delivered, State = DownloadTaskState.Failed, TotalBytes = 3, FinalSha256 = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })) });
        for (var i = 0; i < 75; i++)
        {
            var id = Guid.NewGuid();
            await repository.UpsertAsync(new() { Id = id, Request = new(id, "https://example.com/file", output, $"history-{i}.bin"),
                OutputPath = Path.Combine(output, $"history-{i}.bin"), State = DownloadTaskState.Completed });
        }
        var activeId = Guid.NewGuid();
        await repository.UpsertAsync(new() { Id = activeId, Request = new(activeId, "https://example.com/file", output, "active.bin"),
            OutputPath = Path.Combine(output, "active.bin"), State = DownloadTaskState.Paused });
        try
        {
            using var engine = new HttpRangeDownloader();
            await using var manager = new DownloadManager(engine, repository);
            var taskView = manager.Tasks;
            await manager.RestoreAsync();
            Assert.AreEqual(DownloadTaskState.Completed, manager.Tasks.Single(x => x.Id == committedId).State);
            Assert.AreSame(taskView, manager.Tasks);
            Assert.HasCount(51, manager.GetVisibleTasks(50));
            Assert.HasCount(77, manager.GetVisibleTasks(100));
            Assert.IsTrue(manager.GetVisibleTasks(0).Any(x => x.Id == activeId));
            var notifications = 0; manager.TaskChanged += (_, _) => Interlocked.Increment(ref notifications);
            await Task.Delay(300);
            Assert.AreEqual(0, notifications, "Idle history must not produce periodic progress refreshes.");
            await manager.ResumeAsync(committedId);
            await manager.RemoveHistoryAsync(committedId);
            Assert.IsFalse(manager.Tasks.Any(x => x.Id == committedId));
            Assert.IsFalse((await repository.GetAllAsync()).Any(x => x.Id == committedId));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(delivered));
            Assert.HasCount(1, Directory.GetFiles(output, "*.bin"), "Recovery/removal must neither duplicate nor remove the delivered file.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ResumedMergeReclaimsOnlyOwnAssemblyBeforeSpaceCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const int length = 4 * 1024 * 1024;
        var uri = new Uri("https://example.com/space");
        var staging = Path.Combine(root, "own.partial");
        var cache = staging + ".ranges";
        Directory.CreateDirectory(cache);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "user.bin"), [9]);
            var ranges = HttpByteRangePlanner.Create(length, 2);
            await DownloadStorage.WriteAsync(Path.Combine(cache, "identity.json"),
                new { Url = DownloadStorage.Identity(uri), ETag = "\"v1\"", Length = length, Parts = ranges.Count }, default);
            for (var i = 0; i < ranges.Count; i++)
                await File.WriteAllBytesAsync(Path.Combine(cache, $"part-{i:D2}.bin"), Enumerable.Repeat((byte)(i + 1), (int)ranges[i].Length).ToArray());
            await File.WriteAllBytesAsync(staging, new byte[length]);
            var calls = 0;
            using var client = new HttpClient(new Handler(_ =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1]) };
                response.Headers.ETag = new("\"v1\"");
                response.Content.Headers.ContentRange = new(0, 0, length);
                return response;
            }));
            var checks = 0;
            var downloader = new ParallelHttpFileDownloader(new(client, _ => true))
            {
                CheckSpace = (path, required) =>
                {
                    checks++;
                    Assert.IsFalse(File.Exists(path), "Old assembly must be reclaimed before observing free space.");
                    Assert.AreEqual((long)length, required, "Complete retained parts need only one assembly allocation.");
                    var available = 64L * 1024 * 1024 + length;
                    DownloadStorage.CheckAvailableSpace(required, available);
                    Assert.Throws<IOException>(() => DownloadStorage.CheckAvailableSpace(required, available - 1));
                    Assert.Throws<IOException>(() => DownloadStorage.CheckAvailableSpace(2L * length, available));
                }
            };
            Assert.AreEqual((long)length, await downloader.TryDownloadAsync(uri, staging, 64, new(1, TimeSpan.Zero, TimeSpan.Zero), null, default));
            Assert.AreEqual(1, checks); Assert.AreEqual(1, calls, "Validated complete parts must not be redownloaded.");
            var actual = await File.ReadAllBytesAsync(staging);
            Assert.AreEqual(length, actual.Length); Assert.AreEqual((byte)1, actual[0]); Assert.AreEqual((byte)2, actual[^1]);
            CollectionAssert.AreEqual(new byte[] { 9 }, await File.ReadAllBytesAsync(Path.Combine(root, "user.bin")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NetworkChangeRevokesOldShareAndRebindsRealReceiver()
    {
        var address = DropSpace.Infrastructure.Network.LocalNetworkInterfaceResolver.Resolve();
        IPAddress? selected = IPAddress.Loopback;
        await using var server = new DropSpace.Infrastructure.Sharing.NearbyShareServer(null,
            () => selected ?? throw new InvalidOperationException("Fixture has no network."));
        var item = new DropSpace.Infrastructure.Sharing.NearbyShareItem(Guid.NewGuid(), "fixture.txt", "text/plain", 3,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 })));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var first = await server.CreateShareAsync([item]);
        Assert.AreEqual(IPAddress.Loopback.ToString(), first.Url.Host);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync(first.Url)).StatusCode,
            "An injected loopback listener must not weaken the private receiver restriction.");
        selected = address;
        typeof(DropSpace.Infrastructure.Sharing.NearbyShareServer).GetMethod("NetworkChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(server, [null, EventArgs.Empty]);
        Assert.IsNull(server.BaseUri);
        Assert.IsFalse(server.Revoke(first.ShareId), "Network event must revoke the old token immediately.");
        var second = await server.CreateShareAsync([item]);
        Assert.AreEqual(address.ToString(), second.Url.Host);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await client.GetByteArrayAsync(new Uri(second.Url + "/file/" + item.Id.ToString("N"))));
        var oldOnNewListener = new Uri(server.BaseUri!, first.Url.PathAndQuery);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(oldOnNewListener)).StatusCode);
        selected = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.CreateShareAsync([item]));
        Assert.IsNull(server.BaseUri, "Offline must not advertise the previous endpoint.");
    }

    [TestMethod]
    public async Task QueuedTimeDoesNotConsumeTransferBudgetAndBothPhasesCancel()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var engine = new HttpRangeDownloader();
            engine.Transfers.SetLimit(1);
            using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
            var policy = new DownloadRequestPolicy(client, _ => true);
            var slot = await engine.Transfers.AcquireAsync(default);
            var transfer = engine.DownloadAsync(new("https://example.com/file"), Path.Combine(root, "file.part"), policy,
                null, default, expectedBytes: 3, transferTimeout: TimeSpan.FromMilliseconds(300), queueTimeout: TimeSpan.FromSeconds(3));
            await Task.Delay(450);
            Assert.IsFalse(transfer.IsCompleted);
            slot.Dispose();
            await transfer;
            Assert.AreEqual(3L, new FileInfo(Path.Combine(root, "file.part")).Length);

            using (await engine.Transfers.AcquireAsync(default))
            {
                using var cancel = new CancellationTokenSource();
                var queued = engine.DownloadAsync(new("https://example.com/file"), Path.Combine(root, "queue.part"), policy, null, cancel.Token);
                cancel.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
                await Assert.ThrowsAsync<DownloadQueueTimeoutException>(() => engine.DownloadAsync(new("https://example.com/file"),
                    Path.Combine(root, "timeout.part"), policy, null, default, queueTimeout: TimeSpan.FromMilliseconds(30)));
            }
            engine.Connections.SetLimit(1);
            using (await engine.Connections.AcquireAsync(default))
            {
                using var cancel = new CancellationTokenSource();
                var waitingConnection = engine.DownloadAsync(new("https://example.com/file"), Path.Combine(root, "connection.part"), policy, null, cancel.Token);
                cancel.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => waitingConnection);
                await Assert.ThrowsAsync<DownloadTransferTimeoutException>(() => engine.DownloadAsync(new("https://example.com/file"),
                    Path.Combine(root, "transfer-timeout.part"), policy, null, default, transferTimeout: TimeSpan.FromMilliseconds(30)));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task QqRequestRefusalDoesNotExpireSessionButAuthenticationFailureDoes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-QQ-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new QqMusicSession(root);
            await session.SaveAsync([new() { Name = "qqmusic_key", Value = "fixture-only", Domain = "qq.com" }]);
            session.ReportAccess(session.Generation, false, 403);
            session.EnsureUsable();
            session.ReportAccess(session.Generation, true);
            Assert.AreEqual(QqMusicSessionState.Connected, session.State);
            session.ReportAccess(session.Generation, false, 2001);
            Assert.Throws<LyricsProviderRejectedException>(() => session.EnsureUsable());
            await session.SaveAsync([new() { Name = "qqmusic_key", Value = "renewed-fixture", Domain = "qq.com" }]);
            session.EnsureUsable();
            session.ReportAccess(session.Generation, false, 429);
            Assert.Throws<LyricsProviderRejectedException>(() => session.EnsureUsable());
            Assert.AreEqual(QqMusicSessionState.Saved, session.State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LrclibInstrumentalEvidenceDoesNotHideUsableCandidate()
    {
        const string instrumental = """{"id":1,"trackName":"Song","artistName":"Artist","albumName":"Album","duration":200,"instrumental":true,"plainLyrics":null,"syncedLyrics":null}""";
        const string usable = """{"id":2,"trackName":"Song","artistName":"Artist","albumName":"Album","duration":200,"instrumental":false,"syncedLyrics":"[00:01.00]We are singing here"}""";
        var includeUsable = false;
        using var client = new HttpClient(new Handler(request => new(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/get")
                ? instrumental : "[" + instrumental + (includeUsable ? "," + usable : "") + "]")
        }));
        var provider = new LrclibLyricsProvider(new(client));
        var query = new LyricsQuery("Song", "Artist", "Album", TimeSpan.FromSeconds(200));
        var empty = await provider.QueryAsync(query, default);
        Assert.AreEqual(LyricsBodyQuality.ConfirmedInstrumental, empty.BodyQuality);
        Assert.AreEqual("1", empty.Match?.CandidateId);
        includeUsable = true;
        var found = await provider.QueryAsync(query, default);
        Assert.AreEqual(LyricsBodyQuality.Usable, found.BodyQuality);
        Assert.AreEqual("2", found.Match?.CandidateId);
        Assert.HasCount(1, found.Lines);
        Assert.AreEqual(LyricsBodyQuality.NoLyrics, LyricsDocument.Empty.BodyQuality);
    }

    [TestMethod]
    public async Task CorruptJournalDoesNotDiscardValidQueuedTask()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new DownloadTaskRepository(root);
            var id = Guid.NewGuid();
            await repo.UpsertAsync(new() { Id = id, Request = new(id, "https://example.com/file", root + Path.DirectorySeparatorChar, "file.bin"),
                OutputPath = Path.Combine(root, "file.bin"), State = DownloadTaskState.Queued });
            await File.WriteAllTextAsync(Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"), "{broken");
            var recovered = await repo.GetAllAsync();
            Assert.HasCount(1, recovered);
            Assert.AreEqual(id, recovered[0].Id);
            Assert.AreEqual(DownloadTaskState.Queued, recovered[0].State);
            Assert.IsNotNull(repo.RecoveryError);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProbeRetriesTransientStatusButDoesNotRetryForbidden()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ => new(++calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new ByteArrayContent([1]) }));
        var policy = new RetryPolicy(3, TimeSpan.Zero, TimeSpan.Zero);
        var downloader = new ParallelHttpFileDownloader(new(client, _ => true));
        var staging = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.IsNull(await downloader.TryDownloadAsync(new("https://example.com/file"), staging, 64, policy, null, default));
        Assert.AreEqual(2, calls);
        calls = 0;
        using var denied = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.Forbidden); }));
        downloader = new(new(denied, _ => true));
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.TryDownloadAsync(new("https://example.com/file"), staging, 64, policy, null, default));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task CommitRemainsSuccessfulWhenMarkerCannotBeDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-commit-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var reservations = new OutputReservationService();
            var reservation = await reservations.ReserveAsync(Guid.NewGuid(), root, "file.bin");
            var staging = Path.Combine(root, "private.partial");
            await File.WriteAllBytesAsync(staging, [1, 2, 3]);
            using (var held = new FileStream(reservation.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var committed = await reservations.CommitAsync(reservation, staging);
                Assert.IsTrue(committed.CleanupPending);
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(committed.OutputPath));
                Assert.IsFalse(File.Exists(staging));
            }
            await reservations.ReleaseAsync(reservation);
            Assert.IsFalse(File.Exists(reservation.MarkerPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MirrorResumesVerifiedPartsAndRedownloadsOnlyCorruptPart()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-Beta13-" + Guid.NewGuid().ToString("N"));
        var bodies = Enumerable.Range(1, 4).Select(i => Enumerable.Repeat((byte)i, 8192).ToArray()).ToArray();
        var full = bodies.SelectMany(bytes => bytes).ToArray();
        static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        var model = new AiLyricsModelDescriptor("fixture", "fixture", new("https://huggingface.co/test/model"), full.Length, Hash(full));
        var parts = bodies.Select((bytes, i) => new AiModelDeliveryManifest.Part(i + 1, $"part{i + 1}", bytes.Length, Hash(bytes),
            new($"https://github.com/airanluo-dot/DropSpace/releases/download/models-hy-mt2-q8-v1/part{i + 1}"))).ToArray();
        var requests = new int[4]; var fail = true;
        var handler = new Handler(request =>
        {
            var index = int.Parse(request.RequestUri!.AbsolutePath[^1..], System.Globalization.CultureInfo.InvariantCulture) - 1;
            requests[index]++;
            return new(fail && index == 2 ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            { Content = new ByteArrayContent(bodies[index]) };
        });
        try
        {
            using var service = new AiModelPackageService(root, handler, _ => model, delivery: _ => parts);
            await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync(model.Id, true, null, default));
            Assert.IsFalse(File.Exists(Path.Combine(root, model.Sha256 + ".gguf")));
            Assert.IsTrue(File.Exists(Path.Combine(root, $"{model.Sha256}.{AiModelDeliveryManifest.Identity[..16]}.part001")));
            var firstRequests = requests[0]; var secondRequests = requests[1];
            await File.WriteAllBytesAsync(Path.Combine(root, $"{model.Sha256}.{AiModelDeliveryManifest.Identity[..16]}.part002"), new byte[8192]);
            fail = false;
            var final = await service.DownloadAsync(model.Id, true, null, default);
            CollectionAssert.AreEqual(full, await File.ReadAllBytesAsync(final));
            Assert.AreEqual(firstRequests, requests[0], "Verified first part must not be fetched again.");
            Assert.IsTrue(requests[1] > secondRequests, "Only the corrupt retained part must be replaced.");
            Assert.AreEqual(0, Directory.GetFiles(root, "*.part*").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DirectoryIdentityIsExactAndPinnedMirrorIsComplete()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(DownloadStorage.SameDirectory(@"C:\Users\Test\Downloads\", @"c:\users\test\downloads"));
            Assert.IsFalse(DownloadStorage.SameDirectory(@"C:\Downloads", @"C:\Downloads-extra"));
        }
        var parts = AiModelDeliveryManifest.Find(AiLyricsModelCatalog.ExperimentalLargePlain)!;
        Assert.AreEqual(4, parts.Count);
        Assert.AreEqual(7981928896L, parts.Sum(part => part.Bytes));
        Assert.IsFalse(AiModelDeliveryManifest.Trusted(new("https://github.com/other/release")));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); var response = respond(request); response.RequestMessage = request; return Task.FromResult(response); }
    }
}
