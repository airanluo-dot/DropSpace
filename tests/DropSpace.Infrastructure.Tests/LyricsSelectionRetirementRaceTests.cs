using System.Diagnostics;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsSelectionRetirementRaceTests
{
    [TestMethod]
    public async Task CacheRetirementFencesReusedDecisionAndRejectsRetiredCallerBeforeNewAdmission()
    {
        var query = new LyricsQuery("Song", "Artist", "Album", TimeSpan.FromSeconds(30), "retirement:track");
        var document = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Original", null, [])],
            LyricsProviderKind.NetEase, new("Song", "Artist", "Album", 30, 12, "original"));
        var snapshot = new LyricsCandidateSnapshot([LyricsCandidateRules.Describe("c0", document, "zh")],
            Stopwatch.GetTimestamp() + 12 * Stopwatch.Frequency);
        var settings = new LyricsSettings { SelectionMode = LyricsSelectionMode.AiRanked };
        var runtime = new Runtime();
        var selector = new LyricsCandidateSelector(runtime);
        long ownerGeneration = 0;
        var selected = await selector.SelectAsync(query, settings, "zh", "model", snapshot, document, default,
            () => Interlocked.Read(ref ownerGeneration) == 0);
        Assert.AreEqual(LyricsSelectionOutcome.Selected, selected.Outcome);

        // The owner advances before selector.Clear. A caller with the new owner
        // generation can still reuse the previous cache in this actual interval.
        var currentOwner = Interlocked.Increment(ref ownerGeneration);
        var reused = await selector.SelectAsync(query, settings, "zh", "model", snapshot, document, default,
            () => Interlocked.Read(ref ownerGeneration) == currentOwner);
        Assert.AreEqual(LyricsSelectionOutcome.Reused, reused.Outcome);
        Assert.IsTrue(reused.IsCurrent);
        selector.Clear();
        Assert.IsFalse(reused.IsCurrent, "Cache retirement must fence this result even though its owner generation is still current.");

        // Freeze the second scheduling window after the caller captures its owner
        // generation but before selector admission. Retirement finishes while held.
        var callerCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitCaller = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = Task.Run(async () =>
        {
            var capturedOwner = Interlocked.Read(ref ownerGeneration);
            callerCaptured.SetResult();
            await admitCaller.Task;
            return await selector.SelectAsync(query, settings, "zh", "model", snapshot, document, default,
                () => Interlocked.Read(ref ownerGeneration) == capturedOwner);
        });
        await callerCaptured.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Interlocked.Increment(ref ownerGeneration);
        selector.Clear();
        admitCaller.SetResult();
        Assert.AreEqual(LyricsSelectionOutcome.Unavailable, (await stale.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
        Assert.AreEqual(1, runtime.Calls, "A retired owner must not infer or repopulate the new cache generation.");
        currentOwner = Interlocked.Read(ref ownerGeneration);
        var fresh = await selector.SelectAsync(query, settings, "zh", "model", snapshot, document, default,
            () => Interlocked.Read(ref ownerGeneration) == currentOwner);
        Assert.AreEqual(LyricsSelectionOutcome.Selected, fresh.Outcome,
            "The retired caller must not leave a success entry that the fresh owner could reuse.");
        Assert.AreEqual(2, runtime.Calls);
    }

    private sealed class Runtime : ILyricsSelectionRuntime
    {
        public int Calls { get; private set; }
        public bool CanPrepareSelection => true;
        public bool IsSelectionWarm(string modelHash) => true;
        public Task<bool> PrepareSelectionAsync(string path, string hash, CancellationToken token) => Task.FromResult(true);
        public Task<string?> TryRunSelectionAsync(string hash, string prompt, CancellationToken token)
        {
            Calls++;
            return Task.FromResult<string?>("{\"id\":\"c0\"}");
        }
    }
}
