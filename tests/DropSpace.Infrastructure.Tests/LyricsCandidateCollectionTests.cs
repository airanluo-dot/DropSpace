using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsCandidateCollectionTests
{
    [TestMethod]
    public async Task CollectionKeepsAllowedSlowerSourceAndPublishesFirstOriginalBeforeCompletion()
    {
        var query = new LyricsQuery("Song", "Artist", "Album", TimeSpan.FromSeconds(180))
        { PreferredTranslationLanguage = "zh" };
        LyricsDocument Doc(LyricsProviderKind kind) => LyricsParser.Parse("[00:01]The night is full of stars.", kind)
            .Bind(query, "Song", "Artist", "Album", 180, 12, kind.ToString());
        var late = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new Provider(LyricsProviderKind.NetEase, Task.FromResult(Doc(LyricsProviderKind.NetEase)));
        var backup = new Provider(LyricsProviderKind.QqMusic, late.Task);
        var service = new LyricsService(new([primary, backup]));
        var lookup = service.QueryDetailedAsync(query, new()
        {
            Enabled = true, SelectionMode = LyricsSelectionMode.AiRanked,
            BackupProvider = LyricsProviderKind.QqMusic, SearchRemainingProviders = false,
        }, default, reportOriginal: doc => original.TrySetResult(doc));
        Assert.AreEqual(LyricsProviderKind.NetEase, (await original.Task.WaitAsync(TimeSpan.FromSeconds(1))).Provider);
        Assert.IsFalse(lookup.IsCompleted);
        late.SetResult(Doc(LyricsProviderKind.QqMusic));
        var result = await lookup.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, result.SelectionCandidates.Candidates.Count);
        Assert.IsFalse(result.SelectionCandidates.Truncated);
        Assert.IsTrue(result.SelectionCandidates.Remaining > TimeSpan.Zero);
        Assert.AreEqual(1, primary.Calls);
        Assert.AreEqual(1, backup.Calls);
    }

    private sealed class Provider(LyricsProviderKind kind, Task<LyricsDocument> result) : ILyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public int Calls { get; private set; }
        public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        { Calls++; return await result.WaitAsync(cancellationToken); }
    }
}
