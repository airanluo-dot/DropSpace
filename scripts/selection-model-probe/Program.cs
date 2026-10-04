using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

if (!OperatingSystem.IsWindows() || args.Length != 1) throw new InvalidOperationException("Windows cloud output directory required.");
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions { WriteIndented = true };
void Save(string name, object data) => File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(data, json));
using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(12));
var model = AiLyricsModelCatalog.ExperimentalPlain;
using var package = new AiModelPackageService(Path.Combine(output, "models"));
using var runner = new PersistentPlainLyricsRunner(new AiLyricsRuntimePackage(Assembly.GetExecutingAssembly(), Path.Combine(output, "runtime-cache")), new AiLyricsRuntimeOptions { GpuEnabled = false });
var capture = new RecordingRuntime(runner);
var selector = new LyricsCandidateSelector(capture);
var settings = new LyricsSettings { Enabled = true, SelectionMode = LyricsSelectionMode.AiRanked };
using var manifest = Assembly.GetExecutingAssembly().GetManifestResourceStream("DropSpace.AiLyricsRuntime.runtime-manifest.json") ?? throw new InvalidDataException("Missing bound runtime.");
using var manifestJson = JsonDocument.Parse(manifest);
Save("identity", new {
    diagnosticOnly = true, observedAtUtc = DateTimeOffset.UtcNow, model.Id, model.DownloadUri, model.Sha256, model.Bytes,
    runtime = manifestJson.RootElement.Clone(), runtimeArtifactId = Environment.GetEnvironmentVariable("RUNTIME_ARTIFACT_ID"),
    archiveSha256 = Environment.GetEnvironmentVariable("RUNTIME_ARCHIVE_SHA256"),
    artifactName = $"ai-selection-runtime-{Environment.GetEnvironmentVariable("GITHUB_RUN_ID")}-{Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")}",
    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    scope = "Three hand-annotated metadata fixtures, no provider traffic or private lyrics. CPU only; no semantic release approval.",
    selectionDeadlineMilliseconds = 500, preparationSeconds = 60, memoryPolicy = "Production 1.8B admission and resident monitor; no override",
});
try
{
    // Explicit cloud-probe invocation authorizes this official pinned public model download.
    var modelPath = await package.DownloadAsync(model.Id, consent: true, progress: null, budget.Token);
    // DownloadAsync already verifies exact catalog SHA/size before returning; avoid a redundant 1.9GB rehash.
    Save("verified-model", new { model.Id, model.Sha256, expectedBytes = model.Bytes, actualBytes = new FileInfo(modelPath).Length, verification = "AiModelPackageService.DownloadAsync verified pinned SHA256 and size" });
    foreach (var fixture in Fixtures())
    {
        await runner.DrainCleanupAsync(budget.Token);
        var preparation = Stopwatch.StartNew();
        var prepared = await runner.PrepareSelectionAsync(modelPath, model.Sha256, budget.Token);
        var preparationMilliseconds = preparation.Elapsed.TotalMilliseconds;
        capture.Reset();
        var candidates = fixture.Candidates.Select(c => LyricsCandidateRules.Describe(c.Id, c.Document, "zh")).ToArray();
        var snapshot = new LyricsCandidateSnapshot(candidates, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3);
        var timer = Stopwatch.StartNew();
        var result = await selector.SelectAsync(fixture.Query, settings, "zh", model.Sha256, snapshot, fixture.Rules, budget.Token);
        var milliseconds = timer.Elapsed.TotalMilliseconds;
        await runner.DrainCleanupAsync(budget.Token);
        if (capture.Pending is { } pending) { try { await pending.WaitAsync(budget.Token); } catch (OperationCanceledException) when (!budget.IsCancellationRequested) { } catch (Exception) when (!budget.IsCancellationRequested) { } }
        var parsed = capture.Raw is not null && LyricsCandidateSelectionProtocol.TryParse(capture.Raw, candidates, out _);
        LyricsCandidateSelectionProtocol.TryParse(capture.Raw ?? "", candidates, out var actualId);
        Save(fixture.Name, new {
            fixture.Annotation, expectedId = fixture.ExpectedId, fixture.Query,
            candidates = candidates.Select(c => new { c.Id, c.Document.Match,
                strictScore = LyricsMatcher.Score(fixture.Query, c.Document.Match!.Title, c.Document.Match.Artist, c.Document.Match.Album, c.Document.Match.DurationSeconds, c.Document.Match.ArtistAliases),
                selectionAdmissionScore = LyricsMatcher.CandidateScore(fixture.Query, c.Document.Match!.Title, c.Document.Match.Artist, c.Document.Match.Album, c.Document.Match.DurationSeconds, c.Document.Match.ArtistAliases) }),
            prepared, preparationMilliseconds, capture.Sent, capture.Raw, capture.Error, capture.Backend,
            promptSha256 = capture.PromptHash, milliseconds, outcome = result.Outcome.ToString(),
            inferenceOrFallback = capture.Raw is null ? "fallback-without-completed-inference" : "completed-inference",
            rawMatchesAnnotation = parsed && actualId == fixture.ExpectedId,
            // A correct raw answer is separate from a completed, admitted 500ms selection.
            productionSelectedId = candidates.FirstOrDefault(c => ReferenceEquals(c.Document, result.Document))?.Id,
            postOperationCleanupCompleted = true,
        });
    }
    var warm = await runner.PrepareSelectionAsync(modelPath, model.Sha256, budget.Token);
    var cancelFixture = Fixtures()[0];
    var cancelSnapshot = new LyricsCandidateSnapshot(cancelFixture.Candidates.Select(c => LyricsCandidateRules.Describe(c.Id, c.Document, "zh")).ToArray(), Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency);
    if (!LyricsCandidateSelectionProtocol.TryBuild(cancelFixture.Query, cancelSnapshot, "zh", out var prompt)) throw new InvalidDataException("Cancellation prompt invalid.");
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
    bool sent = false, canceled = false;
    // The production event occurs only after WriteLine + Flush succeeds; no timer guessing.
    void AfterSent() { sent = true; cancel.Cancel(); }
    runner.SelectionRequestSent += AfterSent;
    var cancelTime = Stopwatch.StartNew();
    try { await runner.TryRunSelectionAsync(model.Sha256, prompt, cancel.Token); }
    catch (OperationCanceledException) { canceled = true; }
    finally { runner.SelectionRequestSent -= AfterSent; }
    using var drainBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
    drainBudget.CancelAfter(TimeSpan.FromSeconds(30));
    await runner.DrainCleanupAsync(drainBudget.Token);
    var elapsed = cancelTime.Elapsed.TotalMilliseconds;
    var nextPrepare = await runner.PrepareSelectionAsync(modelPath, model.Sha256, budget.Token);
    var nextWarm = runner.IsSelectionWarm(model.Sha256);
    await runner.DrainCleanupAsync(drainBudget.Token);
    Save("sent-request-cancellation", new { warm, sent, canceled, elapsedMilliseconds = elapsed, cleanupCompleted = true,
        nextPreparationSucceeded = nextPrepare, nextSelectionWarm = nextWarm,
        meaning = "Pipe-flushed request cancellation and owned exit/gate drain. Native token decoding start is not observed; no GPU claim." });
    Save("summary", new { executionCompleted = true, semanticApproved = false, cancellationActuallyExercised = sent && canceled, nextPreparationSucceeded = nextPrepare });
    if (!sent || !canceled || !nextPrepare) return 2;
    return 0;
}
catch (Exception error)
{
    Save("execution-failure", new { executionCompleted = false, semanticApproved = false, error = error.ToString() });
    return 1;
}

static Fixture[] Fixtures()
{
    // Human expectations are fixed before inference. Unknown duration stays zero;
    // no fabricated aliases/durations are injected to admit a preferred answer.
    LyricsDocument Doc(string id, string title, string artist, string album) => new([], LyricsProviderKind.NetEase,
        new LyricsMatchInfo(title, artist, album, 0, 0, id));
    Fixture Build(string name, LyricsQuery query, string? expected, string annotation, params (string Id, LyricsDocument Document)[] candidates) =>
        new(name, query with { TrackIdentity = "probe:" + name, CollectSelectionCandidates = true }, expected, annotation, candidates, LyricsDocument.Empty);
    return [
        Build("cross-script", new("小幸运", "田馥甄", "", TimeSpan.Zero), "c1",
            "Traditional/simplified whole-credit and title spellings refer to the same song/artist; another artist is excluded. Metadata fixture, not a provider capture.",
            ("c1", Doc("probe-1", "小幸運", "田馥甄", "")), ("c2", Doc("probe-2", "小幸运", "Other Artist", ""))),
        Build("same-title-version", new("Hello", "Adele", "25", TimeSpan.Zero), "c3",
            "Adele's studio recording on 25; Lionel Richie's same-title song and an explicitly marked live version are excluded. Live album left unknown. Excluded alternatives deliberately exercise model/host rejection, not collection admission.",
            ("c3", Doc("probe-3", "Hello", "Adele", "25")), ("c4", Doc("probe-4", "Hello", "Lionel Richie", "Can't Slow Down")),
            ("c5", Doc("probe-5", "Hello (Live)", "Adele", ""))),
        Build("uncertain-abstention", new("Hello", "", "", TimeSpan.Zero), null,
            "No artist, album or duration identifies the recording. Both real same-title artists remain possible; abstention is expected. Synthetic incomplete query, no claim about provider admission.",
            ("c6", Doc("probe-6", "Hello", "Adele", "25")), ("c7", Doc("probe-7", "Hello", "Lionel Richie", "Can't Slow Down")))
    ];
}
sealed record Fixture(string Name, LyricsQuery Query, string? ExpectedId, string Annotation,
    (string Id, LyricsDocument Document)[] Candidates, LyricsDocument Rules);
sealed class RecordingRuntime(PersistentPlainLyricsRunner runner) : ILyricsSelectionRuntime
{
    public string? Raw, Error, PromptHash, Backend;
    public bool Sent;
    public Task<string?>? Pending;
    public bool CanPrepareSelection => runner.CanPrepareSelection;
    public bool IsSelectionWarm(string hash) => runner.IsSelectionWarm(hash);
    public Task<bool> PrepareSelectionAsync(string path, string hash, CancellationToken token) => runner.PrepareSelectionAsync(path, hash, token);
    public void Reset() { Raw = Error = PromptHash = Backend = null; Sent = false; Pending = null; }
    public Task<string?> TryRunSelectionAsync(string hash, string prompt, CancellationToken token) => Pending = ExecuteAsync(hash, prompt, token);
    private async Task<string?> ExecuteAsync(string hash, string prompt, CancellationToken token)
    {
        PromptHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
        void Mark() => Sent = true;
        runner.SelectionRequestSent += Mark;
        try { Raw = await runner.TryRunSelectionAsync(hash, prompt, token); Backend = runner.LastExecutionBackend; return Raw; }
        catch (Exception error) { Error = error.GetType().Name; throw; }
        finally { runner.SelectionRequestSent -= Mark; }
    }
}
