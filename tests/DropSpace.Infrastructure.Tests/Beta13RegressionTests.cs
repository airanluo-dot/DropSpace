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
