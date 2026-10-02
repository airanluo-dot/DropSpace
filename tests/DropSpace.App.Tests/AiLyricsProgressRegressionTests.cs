using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class AiLyricsProgressRegressionTests
{
    [TestMethod]
    [DataRow("request")]
    [DataRow("settings")]
    [DataRow("cache")]
    [DataRow("model")]
    [DataRow("cancel")]
    public async Task QueuedPartialRechecksRequestSettingsModelCacheAndCancellationAfterDispatch(string change)
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        LyricsTranslationProgress? queued = null;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (update, _) => { queued = update; return Task.CompletedTask; }, "queued-request");
        var work = fixture.Service.TranslateIfAvailableAsync(Fixture.Query, Fixture.Source, Fixture.Settings, "en", stop.Token, progress);
        await fixture.Backend.Started.Task;
        Assert.IsNotNull(queued);
        Assert.IsTrue(queued.IsCurrent);
        Assert.AreEqual("queued-request", queued.RequestIdentity);
        switch (change)
        {
            case "request":
                await fixture.Service.TranslateCoreAsync(Fixture.Query with { Title = "new song" }, Fixture.Source,
                    Fixture.Settings with { AiTranslationEnabled = false }, "en", default);
                break;
            case "settings": fixture.Service.InvalidateTranslation(); break;
            case "cache": await fixture.Service.ClearCacheAsync(default); break;
            case "model": await fixture.Service.DeleteModelAsync(AiLyricsModelCatalog.ExperimentalPlain.Id, default); break;
            case "cancel": stop.Cancel(); break;
        }
        Assert.IsFalse(queued.IsCurrent, "A delayed dispatcher must reject the captured event after invalidation.");
        fixture.Backend.Complete.TrySetResult();
        if (change == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        else Assert.AreSame(Fixture.Source, await work);
        Assert.AreEqual(AiLyricsTranslationState.Ready, fixture.Service.TranslationState);
    }

    [TestMethod]
    public async Task SuccessfulFinalRetiresQueuedPartialBeforeReturningDocument()
    {
        using var fixture = new Fixture();
        LyricsTranslationProgress? queued = null;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (update, _) => { queued = update; return Task.CompletedTask; });
        var work = fixture.Service.TranslateIfAvailableAsync(Fixture.Query, Fixture.Source, Fixture.Settings, "en", default, progress);
        await fixture.Backend.Started.Task;
        fixture.Backend.Complete.SetResult();
        Assert.AreSame(Fixture.Translated, await work);
        Assert.IsNotNull(queued); Assert.IsFalse(queued.IsCurrent);
        Assert.AreEqual(AiLyricsTranslationState.Completed, fixture.Service.TranslationState);
    }

    [TestMethod]
    public async Task ExternalSongFenceRejectsInferenceEvenBeforeCoordinatorRetirement()
    {
        using var fixture = new Fixture();
        var current = true;
        LyricsTranslationProgress? queued = null;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => current,
            (update, _) => { queued = update; return Task.CompletedTask; });
        var work = fixture.Service.TranslateIfAvailableAsync(Fixture.Query, Fixture.Source, Fixture.Settings, "en", default, progress);
        await fixture.Backend.Started.Task;
        current = false;
        Assert.IsNotNull(queued); Assert.IsFalse(queued.IsCurrent);
        fixture.Backend.Complete.SetResult();
        Assert.AreSame(Fixture.Source, await work);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FinalPublicationRechecksMaintenanceAfterInferenceHasAlreadyReturned(bool clear)
    {
        using var fixture = new Fixture();
        fixture.Backend.Complete.SetResult();
        var publication = await fixture.Service.TranslateForPublicationAsync(Fixture.Query, Fixture.Source,
            Fixture.Settings, "en", default);
        Assert.AreSame(Fixture.Translated, publication.Document);
        Assert.IsTrue(publication.IsCurrent);
        if (clear) await fixture.Service.ClearCacheAsync(default);
        else await fixture.Service.DeleteModelAsync(AiLyricsModelCatalog.ExperimentalPlain.Id, default);
        Assert.IsFalse(publication.IsCurrent, "A final dispatcher update must also reject a retired model/cache generation.");
    }

    [TestMethod]
    [DataRow("gpu")]
    [DataRow("model")]
    [DataRow("disable")]
    public async Task RuntimePreferenceChangeDrainsOldOwnerBeforeChangingSharedOptions(string change)
    {
        using var fixture = new Fixture();
        var previous = fixture.Service.TranslateIfAvailableAsync(Fixture.Query, Fixture.Source, Fixture.Settings, "en", default);
        await fixture.Backend.Started.Task;
        Assert.IsTrue(fixture.RuntimeOptions.GpuEnabled);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.OnDrain = () =>
        {
            Assert.IsTrue(fixture.RuntimeOptions.GpuEnabled, "Old GPU mode must stay immutable until cleanup completes.");
            drained.TrySetResult();
        };
        var nextSettings = change switch
        {
            "gpu" => Fixture.Settings with { AiLyricsGpuAccelerationEnabled = false },
            "model" => Fixture.Settings with { AiModelId = "replacement-test" },
            _ => Fixture.Settings with { AiTranslationEnabled = false },
        };
        var next = fixture.Service.TranslateIfAvailableAsync(Fixture.Query, Fixture.Source, nextSettings, "en", default);
        await drained.Task;
        fixture.Backend.Complete.TrySetResult();
        Assert.AreSame(Fixture.Source, await previous);
        await next;
        Assert.AreEqual(change != "gpu", fixture.RuntimeOptions.GpuEnabled);
        Assert.AreEqual(2, fixture.Backend.Drains);
        fixture.Backend.OnDrain = null;
        await fixture.Service.TranslateIfAvailableAsync(Fixture.Query with { Title = "another song" }, Fixture.Source, nextSettings, "en", default);
        Assert.AreEqual(2, fixture.Backend.Drains, "An ordinary song change must not reset a ready identical runtime profile.");
    }

    [TestMethod]
    public async Task StaleSettingsRequestCannotReconfigureResidentRuntime()
    {
        using var fixture = new Fixture();
        fixture.Backend.Complete.SetResult();
        await fixture.Service.TranslateIfAvailableAsync(Fixture.Query, Fixture.Source, Fixture.Settings, "en", default);
        var drains = fixture.Backend.Drains;
        var stale = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => false,
            (_, _) => throw new AssertFailedException("Stale request must not publish."));
        var result = await fixture.Service.TranslateIfAvailableAsync(Fixture.Query, Fixture.Source,
            Fixture.Settings with { AiLyricsGpuAccelerationEnabled = false }, "en", default, stale);
        Assert.AreSame(Fixture.Source, result);
        Assert.IsTrue(fixture.RuntimeOptions.GpuEnabled);
        Assert.AreEqual(drains, fixture.Backend.Drains);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-app-progress-" + Guid.NewGuid().ToString("N"));
        internal static readonly LyricsQuery Query = new("song", "artist", "", TimeSpan.FromSeconds(2));
        internal static readonly LyricsSettings Settings = new() { Enabled = true, AiTranslationEnabled = true };
        internal static readonly LyricsDocument Source = new([new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "source", null, [])], LyricsProviderKind.LocalLrc);
        internal static readonly LyricsDocument Translated = Source with { Lines = [Source.Lines[0] with
            { Secondary = "translated", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "en" }] };
        internal ProgressBackend Backend { get; } = new();
        internal AiLyricsRuntimeOptions RuntimeOptions { get; } = new();
        internal AiLyricsService Service { get; }
        internal Fixture()
        {
            var paths = new AppStoragePaths(_root);
            Service = new(paths, new LyricsCache(paths.Lyrics), NullLogger<AiLyricsService>.Instance,
                null, new Resolver(), Backend, RuntimeOptions);
        }
        public void Dispose()
        {
            Backend.Complete.TrySetResult(); Service.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class Resolver : IAiLyricsPackageResolver
    {
        public Task<AiLyricsResolvedPackage?> ResolveAsync(string selectionId, LyricsQuery query,
            LyricsDocument source, string targetLanguage, CancellationToken token) =>
            Task.FromResult<AiLyricsResolvedPackage?>(new("progress-test", "identity", "model", "runtime", null));
    }
    private sealed class ProgressBackend : IAiLyricsBackend
    {
        public string Id => "progress-test";
        internal AiLyricsResolvedPackage? Package { get; private set; }
        internal int Drains { get; private set; }
        internal Action? OnDrain { get; set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
            LyricsDocument source, string targetLanguage, CancellationToken token) => throw new AssertFailedException("Progress overload must be used.");
        public async Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
            LyricsDocument source, string targetLanguage, CancellationToken token, LyricsTranslationProgressContext progress)
        {
            Package = package;
            await progress.ReportAsync(Fixture.Translated, 0, 1, 1, token);
            Started.TrySetResult();
            await Complete.Task.WaitAsync(token);
            return new(Fixture.Translated, LyricsTranslationOutcome.Translated);
        }
        public Task DrainCleanupAsync(CancellationToken token) { Drains++; OnDrain?.Invoke(); return Task.CompletedTask; }
        public void Dispose() { }
    }
}
