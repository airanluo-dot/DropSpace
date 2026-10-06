using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta15CachePolicyTests
{
    [TestMethod]
    public async Task SynchronousDisableFencesInFlightAndQueuedWritesForBothStoresWithoutRetiringExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-Beta15-cache-" + Guid.NewGuid().ToString("N"));
        var source = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "A lyric line", null, [])], LyricsProviderKind.NetEase)
            .Bind(new("song", "artist", "album", TimeSpan.FromSeconds(3)), "song", "artist", "album", 3, 12, "candidate");
        var primary = new LyricsCache(root);
        var second = new LyricsCache(root); // A separate view must share the same root policy.
        var ai = new AiLyricsCache(second);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim();
        var oldAiKey = new string('a', 64);
        var lateAiKey = new string('b', 64);
        Task? inFlight = null;
        Task? queued = null;
        try
        {
            await primary.WriteDocumentAsync("old", source, primary.Generation, default);
            await ai.WriteAsync(oldAiKey, "[]", default);
            var originals = Directory.GetFiles(root);
            var generation = primary.Generation;
            var execution = primary.ExecutionGeneration;
            var observations = 0;
            inFlight = Task.Run(() => second.WriteAsync("ai", lateAiKey, "[]", LyricsTranslationOutput.MaximumOutputBytes, generation, default, () =>
            {
                if (Interlocked.Increment(ref observations) == 1)
                {
                    started.TrySetResult();
                    if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The write checkpoint was not released.");
                }
                return true;
            }));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queued = primary.WriteDocumentAsync("late", source, generation, default);
            primary.SetMaximumBytesPolicy(0);
            Assert.IsTrue(primary.Generation > generation);
            Assert.AreEqual(primary.Generation, ai.Generation);
            Assert.AreEqual(execution, primary.ExecutionGeneration);
            Assert.IsTrue(primary.AllowsExecution(generation));
            Assert.IsNull(await second.ReadDocumentAsync("old", default));
            Assert.IsNull(await ai.ReadAsync(oldAiKey, default));
            proceed.Set();
            await Task.WhenAll(inFlight, queued);
            await primary.TrimToCurrentQuotaAsync();
            CollectionAssert.AreEquivalent(originals, Directory.GetFiles(root), "Cache-off must retain old files and publish no in-flight output.");
            second.SetMaximumBytesPolicy(1_000_000_000);
            Assert.IsNotNull(await primary.ReadDocumentAsync("old", default));
            Assert.AreEqual("[]", await ai.ReadAsync(oldAiKey, default));
            Assert.IsNull(await ai.ReadAsync(lateAiKey, default));
            Assert.IsNull(await primary.ReadDocumentAsync("late", default));
        }
        finally
        {
            proceed.Set();
            if (inFlight is not null) await inFlight;
            if (queued is not null) await queued;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
