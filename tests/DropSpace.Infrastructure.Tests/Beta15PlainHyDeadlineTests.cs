using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

/// <summary>Actual managed host/child ownership with the existing protocol fixture.
/// The fixture never loads model weights or a GPU; these checks are not translation-quality evidence.</summary>
[TestClass]
[DoNotParallelize]
public sealed class Beta15PlainHyDeadlineTests
{
    private static readonly LyricsQuery Query = new("Host deadline fixture", "Fixture artist", "Fixture album",
        TimeSpan.FromSeconds(9), "deadline-fixture-track");
    private static readonly string Identity = PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64));
    private const string CompletedText = "首行已完成的译文";
    private const string NativeText = "来源提供的原生译文";

    private static LyricsDocument Source => new([
        new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "I carry the sunlight across the open water.", null, []) { SourceLanguage = "en" },
        new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), "I follow the quiet footsteps along the river.", null, []) { SourceLanguage = "en" },
        new(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(9), "The dawn keeps every promise beneath the stars.", NativeText, [])
        {
            SourceLanguage = "en", TranslationOrigin = LyricsTranslationOrigin.Provider,
            TranslationLanguage = "zh-Hans", TranslationLanguageIsExplicit = true,
        },
    ], LyricsProviderKind.NetEase, new("Host deadline fixture", "Fixture artist", "Fixture album", 9, 12,
        "deadline-fixture-recording", "deadline-fixture-track"));

    [TestMethod]
    public async Task ResidentPrivateDeadlineKeepsCompletedHostRowAndNativeTranslationWithoutCachingPartialSong()
    {
        await using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var source = Source;
        var calls = 0;
        LyricsTranslationProgress? partial = null;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (update, _) => { partial = update; return Task.CompletedTask; });
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-Hans", Identity, fixture.Cache.Generation,
            (_, token) => fixture.RunPrompt(++calls == 1 ? CompletedText : "partial-and-block", token), caller.Token, progress);

        Assert.IsFalse(caller.IsCancellationRequested, "The resident's private deadline cannot cancel the song token.");
        Assert.AreEqual(2, calls);
        Assert.AreEqual(LyricsTranslationOutcome.Failed, result.Outcome);
        AssertPartial(result.Document, "worker-deadline");
        Assert.IsNotNull(partial);
        Assert.IsFalse(partial.IsCurrent, "The final partial result replaces retired progress, not a late queued snapshot.");
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(source), default));
        Assert.AreEqual("DeadlineExceeded", fixture.Runner.LastFailure?.Reason);
        await fixture.Runner.DrainCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNull(fixture.Runner.ActiveProcessId);
        Assert.AreEqual(1, LocalInferenceProcess.InferenceGate.CurrentCount);
    }

    [TestMethod]
    public async Task WholeSongDeadlineKeepsCompletedRowButIsNotReportedAsResidentDeadline()
    {
        await using var fixture = new Fixture(copyWorker: false);
        var source = Source;
        var calls = 0;
        var result = await fixture.Coordinator.TranslateWithBudgetAsync(Query, source, "zh-Hans", Identity,
            fixture.Cache.Generation, async (_, token) =>
            {
                if (++calls == 1) return CompletedText;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unreachable";
            }, default, TimeSpan.FromMilliseconds(100));
        Assert.AreEqual(2, calls);
        Assert.AreEqual(LyricsTranslationOutcome.Failed, result.Outcome);
        AssertPartial(result.Document, "translation-deadline");
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(source), default));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallerOrRuntimeLifetimeCancellationStillPropagatesAfterCompletedRow(bool runtimeLifetime)
    {
        await using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var source = Source;
        var calls = 0;
        var blockedRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.TranslationRequestSent += () => { if (calls == 2) blockedRequest.TrySetResult(); };
        LyricsTranslationProgress? partial = null;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (update, _) => { partial = update; return Task.CompletedTask; });
        var work = fixture.Coordinator.TranslateAsync(Query, source, "zh-Hans", Identity, fixture.Cache.Generation,
            (_, token) => fixture.RunPrompt(++calls == 1 ? CompletedText : "partial-and-block", token), caller.Token, progress);
        await blockedRequest.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (runtimeLifetime) fixture.Runner.Dispose(); else caller.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsNotNull(partial);
        Assert.IsFalse(partial.IsCurrent);
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(source), default));
        await fixture.Runner.DrainCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNull(fixture.Runner.ActiveProcessId);
        Assert.AreEqual(1, LocalInferenceProcess.InferenceGate.CurrentCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CacheClearOrRequestGenerationRetiresLatePrivateTimeoutInsteadOfPublishingPartial(bool clearCache)
    {
        await using var fixture = new Fixture(copyWorker: false);
        var source = Source;
        var calls = 0;
        var current = true;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => current,
            (_, _) => Task.CompletedTask);
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-Hans", Identity, fixture.Cache.Generation,
            async (_, _) =>
            {
                if (++calls == 1) return CompletedText;
                if (clearCache) await fixture.Cache.ClearAsync(default); else current = false;
                throw new TimeoutException("Controlled retired resident deadline.");
            }, default, progress);
        Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
        Assert.IsNull(result.Document.Lines[0].Secondary);
        Assert.AreEqual(NativeText, result.Document.Lines[2].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, result.Document.Lines[2].TranslationOrigin);
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(source), default));
    }

    private static void AssertPartial(LyricsDocument document, string pendingReason)
    {
        Assert.AreEqual(CompletedText, document.Lines[0].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.LocalAi, document.Lines[0].TranslationOrigin);
        Assert.AreEqual(LyricsLineTranslationState.Translated, document.Lines[0].TranslationState);
        Assert.IsNull(document.Lines[1].Secondary);
        Assert.AreEqual(LyricsLineTranslationState.Failed, document.Lines[1].TranslationState);
        Assert.AreEqual(pendingReason, document.Lines[1].TranslationReason);
        Assert.AreEqual(NativeText, document.Lines[2].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, document.Lines[2].TranslationOrigin);
        Assert.AreEqual(LyricsLineTranslationState.Translated, document.Lines[2].TranslationState);
    }

    private static string Key(LyricsDocument source) => PlainHyLyricsProtocol.CacheKey(Query, source, "zh-Hans", Identity);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-beta15-deadline-" + Guid.NewGuid().ToString("N"));
        internal AiLyricsCache Cache { get; }
        internal PlainHyLyricsCoordinator Coordinator { get; }
        internal PersistentPlainLyricsRunner Runner { get; }
        private string Executable => Path.Combine(_root, "DropSpace.ResidentWorkerFixture.exe");

        internal Fixture(bool copyWorker = true)
        {
            Directory.CreateDirectory(_root);
            if (copyWorker)
            {
                if (!OperatingSystem.IsWindows()) Assert.Inconclusive("This focused child ownership check requires Windows.");
                var payload = Path.Combine(AppContext.BaseDirectory, "ResidentFixture");
                Assert.IsTrue(File.Exists(Path.Combine(payload, "DropSpace.ResidentWorkerFixture.exe")),
                    "The existing managed resident worker fixture must be built.");
                foreach (var file in Directory.EnumerateFiles(payload, "DropSpace.ResidentWorkerFixture.*"))
                    File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
            }
            Cache = new(Path.Combine(_root, "cache"));
            Coordinator = new(Cache);
            Runner = new((_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(Executable); },
                new AiLyricsRuntimeOptions { GpuEnabled = false }, TimeSpan.FromSeconds(5),
                TestInferenceMemory.Sufficient, requestTimeout: TimeSpan.FromSeconds(2));
        }

        internal Task<string> RunPrompt(string prompt, CancellationToken token) => Runner.RunPlainAsync(Executable,
            Path.Combine(_root, "Fixture model.gguf"), prompt, Path.Combine(_root, "staging"), token,
            AiLyricsModelCatalog.ExperimentalPlain.Sha256);

        public async ValueTask DisposeAsync()
        {
            Runner.Dispose();
            await Runner.DrainCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(_root, recursive: true);
        }
    }
}
