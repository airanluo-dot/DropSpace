using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsProviderStrategyNativeSmokeTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly LyricsProviderKind[] OnlineProviders =
    [
        LyricsProviderKind.NetEase,
        LyricsProviderKind.QqMusic,
        LyricsProviderKind.Kugou,
        LyricsProviderKind.Lrclib,
        LyricsProviderKind.Amll,
    ];

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task LiveProviderRequestsHonorPreferredBackupAndRemainingSelection()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DROPSPACE_LYRICS_PROVIDER_NATIVE"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set DROPSPACE_LYRICS_PROVIDER_NATIVE=1 to exercise the live online providers.");
        }

        using var transport = new RecordingHttpHandler();
        using var client = new HttpClient(transport)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var real = new LyricsProviderRegistry(new LyricsHttpClient(client), Path.GetTempPath);

        try
        {
            await VerifyAsync(real, new LyricsSettings { Enabled = true }, [LyricsProviderKind.NetEase]);
            await VerifyAsync(real, new LyricsSettings { Enabled = true, BackupProvider = LyricsProviderKind.QqMusic },
                [LyricsProviderKind.NetEase, LyricsProviderKind.QqMusic]);
            await VerifyAsync(real, new LyricsSettings { Enabled = true, SearchRemainingProviders = true }, OnlineProviders);
            await VerifyAsync(real, new LyricsSettings
            {
                Enabled = true,
                BackupProvider = LyricsProviderKind.QqMusic,
                SearchRemainingProviders = true,
            }, OnlineProviders);
        }
        finally { TestContext.WriteLine(System.Text.Json.JsonSerializer.Serialize(transport.Events)); }
    }

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task PublicSongRequestReturnsActualTimedLyrics()
    {
        if (Environment.GetEnvironmentVariable("DROPSPACE_LYRICS_PROVIDER_NATIVE") != "1")
        { Assert.Inconclusive("Set DROPSPACE_LYRICS_PROVIDER_NATIVE=1 for the real public-song provider gate."); return; }
        using var transport = new RecordingHttpHandler();
        using var client = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        var registry = new LyricsProviderRegistry(new LyricsHttpClient(client), Path.GetTempPath);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var document = await registry.Get(LyricsProviderKind.Lrclib).QueryAsync(
                new LyricsQuery("Shape of You", "Ed Sheeran", "", TimeSpan.FromSeconds(234)), deadline.Token);
            TestContext.WriteLine($"Public catalog probe: provider=Lrclib; lines={document.Lines.Count}; timed={document.Lines.Count(line => line.Start > TimeSpan.Zero)}. Lyric text is not logged.");
            Assert.IsTrue(document.Lines.Count > 0, "The real public-song gate requires returned lyrics, not merely a completed HTTP attempt.");
            Assert.IsTrue(document.Lines.Any(line => line.Start > TimeSpan.Zero));
        }
        finally { TestContext.WriteLine(System.Text.Json.JsonSerializer.Serialize(transport.Events)); }
    }

    private sealed class RecordingHttpHandler : DelegatingHandler
    {
        internal readonly List<object> Events = [];
        internal RecordingHttpHandler() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                lock (Events) Events.Add(new { host = request.RequestUri!.Host, path = request.RequestUri.AbsolutePath,
                    status = (int)response.StatusCode, milliseconds = clock.ElapsedMilliseconds });
                return response;
            }
            catch (Exception error)
            {
                lock (Events) Events.Add(new { host = request.RequestUri!.Host, path = request.RequestUri.AbsolutePath,
                    error = error.GetType().Name, hresult = $"0x{error.HResult:X8}", milliseconds = clock.ElapsedMilliseconds });
                throw;
            }
        }
    }

    private static async Task VerifyAsync(
        LyricsProviderRegistry real,
        LyricsSettings settings,
        IReadOnlyCollection<LyricsProviderKind> expected)
    {
        var wrappers = Enum.GetValues<LyricsProviderKind>()
            .Select(kind => new RecordingProvider(real.Get(kind)))
            .ToArray();
        var service = new LyricsService(new LyricsProviderRegistry(wrappers));
        var nonce = Guid.NewGuid().ToString("N");
        var result = await service.QueryDetailedAsync(
            new LyricsQuery($"DropSpace source-order probe {nonce}", "DropSpace test artist", string.Empty,
                TimeSpan.FromSeconds(197), nonce),
            settings,
            CancellationToken.None);

        Assert.AreNotEqual(LyricsQueryStatus.Found, result.Status);
        foreach (var wrapper in wrappers)
        {
            Assert.AreEqual(expected.Contains(wrapper.Kind) ? 1 : 0, wrapper.Calls, wrapper.Kind.ToString());
        }
    }

    private sealed class RecordingProvider(ILyricsProvider inner) : ILyricsProvider
    {
        public LyricsProviderKind Kind => inner.Kind;
        public int Calls { get; private set; }

        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.QueryAsync(query, cancellationToken);
        }
    }
}
