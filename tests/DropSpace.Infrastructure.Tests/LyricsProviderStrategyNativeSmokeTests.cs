using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsProviderStrategyNativeSmokeTests
{
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

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var real = new LyricsProviderRegistry(new LyricsHttpClient(client), Path.GetTempPath);

        await VerifyAsync(real, new LyricsSettings(), [LyricsProviderKind.NetEase]);
        await VerifyAsync(real, new LyricsSettings { BackupProvider = LyricsProviderKind.QqMusic },
            [LyricsProviderKind.NetEase, LyricsProviderKind.QqMusic]);
        await VerifyAsync(real, new LyricsSettings { SearchRemainingProviders = true }, OnlineProviders);
        await VerifyAsync(real, new LyricsSettings
        {
            BackupProvider = LyricsProviderKind.QqMusic,
            SearchRemainingProviders = true,
        }, OnlineProviders);
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
