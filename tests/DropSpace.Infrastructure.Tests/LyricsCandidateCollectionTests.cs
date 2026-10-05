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

    [TestMethod]
    public async Task TranslationAfterFormerReservedWindowSurvivesAndSelectionGetsIndependentBudget()
    {
        var query = new LyricsQuery("Song", "Artist", "Album", TimeSpan.FromSeconds(180))
        { PreferredTranslationLanguage = "zh" };
        var original = LyricsParser.Parse("[00:01]The night is full of stars.", LyricsProviderKind.NetEase)
            .Bind(query, "Song", "Artist", "Album", 180, 12, "original");
        var translated = original with { Provider = LyricsProviderKind.QqMusic,
            Match = original.Match! with { CandidateId = "translated" },
            Lines = original.Lines.Select(line => line with { Secondary = "夜空布满繁星。",
                TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh-Hans",
                TranslationLanguageIsExplicit = true }).ToArray() };
        var pending = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new LyricsService(new([new Provider(LyricsProviderKind.NetEase, Task.FromResult(original)),
            new Provider(LyricsProviderKind.QqMusic, pending.Task)]));
        var lookup = service.QueryDetailedAsync(query, new() { Enabled = true,
            SelectionMode = LyricsSelectionMode.AiRanked, BackupProvider = LyricsProviderKind.QqMusic,
            SearchRemainingProviders = false }, default, reportOriginal: _ => preview.TrySetResult());
        await preview.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(2700);
        pending.SetResult(translated);
        var result = await lookup.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("夜空布满繁星。", result.Document.Lines[0].Secondary,
            "Native translation arriving between 2.5 and 3 seconds must survive AI collection.");
        Assert.IsTrue(result.SelectionCandidates.Remaining > TimeSpan.FromSeconds(10),
            "The frozen snapshot's background AI budget must start after source lookup, not inside it.");
    }

    private sealed class Provider(LyricsProviderKind kind, Task<LyricsDocument> result) : ILyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public int Calls { get; private set; }
        public async Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        { Calls++; return await result.WaitAsync(cancellationToken); }
    }
}
