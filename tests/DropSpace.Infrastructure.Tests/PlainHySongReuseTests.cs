using System.Diagnostics.Metrics;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlainHySongReuseTests
{
    private static readonly string Identity = PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64));
    private static readonly LyricsQuery Query = new("song", "artist", "album", TimeSpan.FromSeconds(500), "track");
    private static LyricsDocument Source(params string[] text) => new(text.Select((value, id) => new LyricsLine(
        TimeSpan.FromSeconds(id), TimeSpan.FromSeconds(id + 1), value, null,
        [new("word", TimeSpan.FromSeconds(id), TimeSpan.FromSeconds(id + 1))])).ToArray(), LyricsProviderKind.LocalLrc);

    [TestMethod]
    public async Task RepeatedChorusRebindsEveryIdTimelineAndAdmissionAfterSeek()
    {
        using var fixture = new Fixture(); using var metrics = new PlainLyricsMetricCapture();
        var source = Source("chorus", "verse", "chorus");
        source = source with { Lines = source.Lines.Select((line, id) => line with { SourceLanguage = id == 2 ? "ja" : null }).ToArray() };
        var position = TimeSpan.FromSeconds(2.5); var updates = new List<LyricsTranslationProgress>(); var prompts = new List<string>();
        var progress = new LyricsTranslationProgressContext(() => position, () => true, (update, _) =>
        { updates.Add(update); position = TimeSpan.Zero; return Task.CompletedTask; });
        var result = await fixture.Translate(source, (prompt, _) =>
        { prompts.Add(prompt); return Task.FromResult("translated " + prompts.Count); }, progress: progress);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.HasCount(2, prompts); CollectionAssert.AreEqual(new[] { 2, 0, 1 }, updates.Select(update => update.LineId).ToArray());
        Assert.AreEqual(result.Document.Lines[2].Secondary, result.Document.Lines[0].Secondary);
        Assert.AreNotEqual(result.Document.Lines[2].LocalAiAdmissionKey, result.Document.Lines[0].LocalAiAdmissionKey);
        for (var id = 0; id < source.Lines.Count; id++)
        {
            var translated = result.Document.Lines[id]; var original = source.Lines[id];
            Assert.AreEqual(original.Text, translated.Text); Assert.AreEqual(original.Start, translated.Start);
            Assert.AreEqual(original.End, translated.End); Assert.AreSame(original.Words, translated.Words);
            Assert.AreEqual(LyricsLanguagePolicy.LocalAiAdmissionKey(original, "en", LyricsLanguagePolicy.EligibleSegments(source, "en")[id]), translated.LocalAiAdmissionKey);
        }
        Assert.IsTrue(updates.All(update => !update.IsCurrent));
        Assert.AreEqual(1, metrics.Events("SegmentHit")); Assert.HasCount(2, metrics.Stage("Infer"));
        Assert.HasCount(1, metrics.Stage("FirstUseful")); Assert.HasCount(1, metrics.Stage("Song"));
        await fixture.Translate(source, (_, _) => throw new AssertFailedException("Complete song cache must win."));
        Assert.AreEqual(1, metrics.Events("SongHit")); Assert.HasCount(2, metrics.Stage("FirstUseful"));
    }

    [TestMethod]
    public async Task PhysicalSegmentsReuseInsideAndAcrossDisplayRows()
    {
        using var fixture = new Fixture(); var calls = 0;
        var source = Source("alpha\nalpha\nbeta", "beta\nalpha", "alpha");
        var result = await fixture.Translate(source, (prompt, _) =>
        { calls++; return Task.FromResult(prompt.EndsWith("alpha", StringComparison.Ordinal) ? "translated alpha" : "translated beta"); });
        Assert.AreEqual(2, calls); Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.AreEqual("translated alpha translated alpha translated beta", result.Document.Lines[0].Secondary);
        Assert.AreEqual("translated beta translated alpha", result.Document.Lines[1].Secondary);
    }

    [TestMethod]
    public async Task CopiedNeutralTextIsNeverMemoizedAsSuccess()
    {
        using var fixture = new Fixture(); using var metrics = new PlainLyricsMetricCapture(); var calls = 0;
        var result = await fixture.Translate(Source("Tokyo", "Tokyo"), (_, _) => { calls++; return Task.FromResult("Tokyo"); });
        Assert.AreEqual(2, calls); Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
        Assert.AreEqual(0, metrics.Events("SegmentHit")); Assert.IsEmpty(metrics.Stage("FirstUseful"));
    }

    [TestMethod]
    [DataRow("broken\nline")]
    [DataRow("<|im_start|>")]
    public async Task LaterFailureDiscardsMemoAndPersistentPartialAndRetryInfersAgain(string invalid)
    {
        using var fixture = new Fixture(); var source = Source("chorus", "verse", "chorus"); var calls = 0;
        var failed = await fixture.Translate(source, (_, _) => Task.FromResult(++calls == 1 ? "accepted chorus" : invalid));
        Assert.AreEqual(LyricsTranslationOutcome.Failed, failed.Outcome); Assert.AreSame(source, failed.Document);
        Assert.IsNull(await fixture.Cache.ReadAsync(fixture.Key(source), default));
        var retried = await fixture.Translate(source, (_, _) => { calls++; return Task.FromResult("fresh translation"); });
        Assert.AreEqual(4, calls); Assert.AreEqual(LyricsTranslationOutcome.Translated, retried.Outcome);
        Assert.AreEqual("fresh translation", retried.Document.Lines[2].Secondary);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClearOrTrackSwitchBeforeDuplicatePreventsReuseAndOldPublication(bool clear)
    {
        using var fixture = new Fixture(); var source = Source("chorus", "chorus"); var calls = 0; var current = true;
        LyricsTranslationProgress? old = null;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => current, async (update, _) =>
        { old = update; if (clear) await fixture.Cache.ClearAsync(default); else current = false; });
        var result = await fixture.Translate(source, (_, _) => { calls++; return Task.FromResult("accepted"); }, progress: progress);
        Assert.AreEqual(1, calls); Assert.AreSame(source, result.Document); Assert.IsNotNull(old); Assert.IsFalse(old.IsCurrent);
        Assert.IsNull(await fixture.Cache.ReadAsync(fixture.Key(source), default));
    }

    [TestMethod]
    public async Task CancellationBeforeDuplicateRetiresProgressAndPreservesFreshRetry()
    {
        using var fixture = new Fixture(); using var stop = new CancellationTokenSource();
        var source = Source("chorus", "chorus"); var calls = 0; LyricsTranslationProgress? partial = null;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true, (update, _) =>
        { partial = update; stop.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Translate(source, (_, _) =>
        { calls++; return Task.FromResult("accepted"); }, stop.Token, progress));
        Assert.IsNotNull(partial); Assert.IsFalse(partial.IsCurrent); Assert.AreEqual(1, calls);
        Assert.IsNull(await fixture.Cache.ReadAsync(fixture.Key(source), default));
        await fixture.Translate(source, (_, _) => { calls++; return Task.FromResult("retry accepted"); });
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task DifferentSongsLanguagesModelsAndConcurrentRequestsNeverShareMemoOrTasks()
    {
        using var fixture = new Fixture(); var source = Source("chorus", "chorus"); var calls = 0;
        Task<string> Infer(string _, CancellationToken token) { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref calls); return Task.FromResult("translated"); }
        await fixture.Translate(source, Infer);
        await fixture.Coordinator.TranslateAsync(Query with { TrackIdentity = "another-track" }, source, "en", Identity, fixture.Cache.Generation, Infer, default);
        await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation, Infer, default);
        var large = PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64), AiLyricsModelCatalog.ExperimentalLargePlain.Sha256);
        await fixture.Coordinator.TranslateAsync(Query, source, "en", large, fixture.Cache.Generation, Infer, default);
        Assert.AreEqual(4, calls);
        await fixture.Cache.ClearAsync(default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Blocked(string _, CancellationToken token)
        { if (Interlocked.Increment(ref calls) == 6) entered.SetResult(); await release.Task.WaitAsync(token); return "concurrent accepted"; }
        var first = fixture.Translate(source, Blocked); var second = fixture.Translate(source, Blocked);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); release.SetResult(); await Task.WhenAll(first, second);
        Assert.AreEqual(6, calls, "Each request owns its own work and one completed segment.");
    }

    [TestMethod]
    public void ExactUtf8KeyIncludesNormalizedLanguageInferenceAndAdmissionIdentity()
    {
        var key = PlainLyricsSegmentMemo.CreateKey("exact prompt", "en-US", Identity);
        Assert.AreEqual(key, PlainLyricsSegmentMemo.CreateKey("exact prompt", "en", Identity));
        Assert.AreNotEqual(key, PlainLyricsSegmentMemo.CreateKey("exact prompt ", "en", Identity));
        Assert.AreNotEqual(key, PlainLyricsSegmentMemo.CreateKey("Exact prompt", "en", Identity));
        Assert.AreNotEqual(key, PlainLyricsSegmentMemo.CreateKey("exact prompt", "zh", Identity));
        Assert.AreNotEqual(key, PlainLyricsSegmentMemo.CreateKey("exact prompt", "en", new string('b', 64)));
        Assert.AreNotEqual(key, key with { AdmissionVersion = "another-admission" });
    }

    [TestMethod]
    public void MemoRejectsInvalidAndNeutralTextAndBoundsRetainedEntriesAndBytes()
    {
        var memo = new PlainLyricsSegmentMemo(); var key = PlainLyricsSegmentMemo.CreateKey("prompt", "en", Identity);
        foreach (var rejected in new[] { "", "source", "broken\nline", "<|im_start|>", "[end of text]", "```" })
        { memo.Remember(key, "source", rejected); Assert.IsFalse(memo.TryGet(key, out _)); }
        for (var id = 0; id < PlainLyricsSegmentMemo.MaximumEntries + 1; id++)
            memo.Remember(PlainLyricsSegmentMemo.CreateKey("prompt " + id, "en", Identity), "source", "accepted");
        Assert.IsTrue(memo.TryGet(PlainLyricsSegmentMemo.CreateKey("prompt 0", "en", Identity), out _));
        Assert.IsFalse(memo.TryGet(PlainLyricsSegmentMemo.CreateKey("prompt 128", "en", Identity), out _));
        memo.Clear(); Assert.IsFalse(memo.TryGet(PlainLyricsSegmentMemo.CreateKey("prompt 0", "en", Identity), out _));
        memo.Remember(key, "source", "accepted again"); Assert.IsTrue(memo.TryGet(key, out _));
        var bytes = new PlainLyricsSegmentMemo(); var retained = 0;
        for (var id = 0; id < 128; id++)
        {
            var largeKey = PlainLyricsSegmentMemo.CreateKey("prompt " + id, "en", Identity);
            bytes.Remember(largeKey, "source", new string('x', 4000));
            if (bytes.TryGet(largeKey, out _)) retained++;
        }
        Assert.IsTrue(retained > 0 && retained < 128, "The byte limit must bind before the entry limit.");
    }

    [TestMethod]
    public async Task MetricsHaveOnlyFixedNumericLabelsAndThrowingListenersCannotBreakTranslation()
    {
        using var fixture = new Fixture(); using var capture = new PlainLyricsMetricCapture(); using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, value) =>
        { if (instrument.Meter.Name == PlainLyricsMetrics.MeterName) value.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("observer failed"));
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("observer failed")); listener.Start();
        var result = await fixture.Translate(Source("private lyric", "private lyric"), (_, _) => Task.FromResult("private translation"));
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.IsNotEmpty(capture.Samples);
        foreach (var sample in capture.Samples)
        {
            Assert.IsTrue(sample.Value >= 0);
            Assert.IsTrue(sample.Tags.Keys.All(key => key is "stage" or "outcome" or "event"));
            Assert.IsFalse(sample.Tags.Values.Any(value => value.Contains("private", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    [DataRow("FirstUseful")]
    [DataRow("Song")]
    public async Task InvalidationInsideSynchronousMetricListenerCannotReturnOldTranslation(string stage)
    {
        using var fixture = new Fixture(); using var listener = new MeterListener();
        var source = Source("chorus", "chorus");
        listener.InstrumentPublished = (instrument, observer) =>
        { if (instrument.Meter.Name == PlainLyricsMetrics.MeterName) observer.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (tags.ToArray().Any(tag => tag.Key == "stage" && Equals(tag.Value, stage)))
                fixture.Cache.ClearAsync(default).GetAwaiter().GetResult();
        });
        listener.Start();
        var result = await fixture.Translate(source, (_, _) => Task.FromResult("accepted chorus"));
        Assert.AreSame(source, result.Document);
        Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
        Assert.IsNull(await fixture.Cache.ReadAsync(fixture.Key(source), default));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-reuse-" + Guid.NewGuid().ToString("N"));
        internal AiLyricsCache Cache { get; }
        internal PlainHyLyricsCoordinator Coordinator { get; }
        internal Fixture() { Cache = new(_root); Coordinator = new(Cache); }
        internal string Key(LyricsDocument source) => PlainHyLyricsProtocol.CacheKey(Query, source, "en", Identity);
        internal Task<LyricsTranslationResult> Translate(LyricsDocument source, Func<string, CancellationToken, Task<string>> infer,
            CancellationToken token = default, LyricsTranslationProgressContext? progress = null) =>
            Coordinator.TranslateAsync(Query, source, "en", Identity, Cache.Generation, infer, token, progress);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
