using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Observable plaintext boundary; production uses LlamaCompletionRunner's native ownership path.</summary>
public interface IPlainLyricsRunner : IDisposable
{
    Task<string> RunPlainAsync(string executablePath, string modelPath, string prompt, string stagingDirectory,
        CancellationToken cancellationToken, string verifiedModelSha256);
    Task DrainCleanupAsync(CancellationToken token);
}

/// <summary>The sole shipping Beta profile. Legacy weights are never silently used by this resolver.</summary>
public sealed class PlainHyLyricsPackageResolver(AiModelPackageService models, AiLyricsRuntimePackage runtime)
    : IAiLyricsPackageResolver
{
    public async Task<AiLyricsResolvedPackage?> ResolveAsync(string selectionId, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var descriptor = AiLyricsModelCatalog.FindSelectable(selectionId);
        if (descriptor is null) return null;
        source = LyricsLanguagePolicy.IdentifyProviderTranslations(source);
        if (LyricsTranslationPolicy.HasMatchingProviderTranslation(source, targetLanguage) ||
            LyricsLanguagePolicy.EligibleIndices(source, targetLanguage).Length == 0) return null;
        var model = await models.GetInstalledPathAsync(selectionId, token).ConfigureAwait(false);
        if (model is null) return null;
        var executable = await runtime.EnsureExecutableAsync(token).ConfigureAwait(false);
        // Plain generation uses exactly the evaluated template; no JSON or token-count subprocess.
        return new(PlainHyLyricsBackend.BackendId, PlainHyLyricsProtocol.InferenceIdentity(runtime.GetManifestCacheIdentity(), descriptor.Sha256),
            model, executable, null, ModelId: descriptor.Id, VerifiedModelSha256: descriptor.Sha256);
    }
}

/// <summary>Whole-song coordinator for host-mapped plaintext output. Unknown/same-target copied lines
/// are neutral. Shared conservative language/credit admission runs before cache and inference.</summary>
public sealed class PlainHyLyricsCoordinator(AiLyricsCache cache)
{
    private readonly object _memoGate = new();
    private readonly Dictionary<string, (long Generation, DateTimeOffset Until)> _noUseful = new(StringComparer.Ordinal);

    public async Task<LyricsTranslationResult?> TryGetCachedAsync(LyricsQuery query, LyricsDocument source,
        string targetLanguage, string identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source = LyricsLanguagePolicy.IdentifyProviderTranslations(source);
        if (LyricsTranslationPolicy.HasMatchingProviderTranslation(source, targetLanguage)) return null;
        var indices = LyricsLanguagePolicy.EligibleIndices(source, targetLanguage);
        if (indices.Length == 0) return null;
        var generation = cache.Generation;
        var key = PlainHyLyricsProtocol.CacheKey(query, source, targetLanguage, identity);
        if (IsNoUseful(key, generation)) return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
        var saved = await cache.ReadAsync(key, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (cache.Generation != generation || saved is null ||
            !TryApplyEligibleOutput(saved, source, indices, targetLanguage, out var result)) return null;
        if (LyricsTranslationOutput.HasUsefulLocalTranslation(result, targetLanguage))
            return new(result, LyricsTranslationOutcome.Translated);
        RememberNoUseful(key, generation);
        return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
    }

    public Task<LyricsTranslationResult> TranslateAsync(LyricsQuery query, LyricsDocument source,
        string targetLanguage, string identity, long generation, Func<string, CancellationToken, Task<string>> infer,
        CancellationToken token, LyricsTranslationProgressContext? progress = null) =>
        TranslateWithBudgetAsync(query, source, targetLanguage, identity, generation,
            infer, token, TimeSpan.FromSeconds(PlainHyLyricsProtocol.WholeSongSeconds), progress);

    internal async Task<LyricsTranslationResult> TranslateWithBudgetAsync(LyricsQuery query, LyricsDocument source,
        string targetLanguage, string identity, long generation, Func<string, CancellationToken, Task<string>> infer,
        CancellationToken token, TimeSpan songBudget, LyricsTranslationProgressContext? progress = null)
    {
        if (songBudget <= TimeSpan.Zero || songBudget > TimeSpan.FromSeconds(PlainHyLyricsProtocol.WholeSongSeconds))
            throw new ArgumentOutOfRangeException(nameof(songBudget));
        ArgumentNullException.ThrowIfNull(infer);
        token.ThrowIfCancellationRequested();
        source = LyricsLanguagePolicy.IdentifyProviderTranslations(source);
        if (cache.Generation != generation || progress?.IsCurrent == false ||
            LyricsTranslationPolicy.HasMatchingProviderTranslation(source, targetLanguage))
            return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
        var indices = LyricsLanguagePolicy.EligibleIndices(source, targetLanguage);
        if (indices.Length == 0) return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
        var key = PlainHyLyricsProtocol.CacheKey(query, source, targetLanguage, identity);
        var cached = await TryGetCachedAsync(query, source, targetLanguage, identity, token).ConfigureAwait(false);
        if (cache.Generation != generation || progress?.IsCurrent == false) return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
        if (cached is not null) return cached;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(songBudget);
        var result = source;
        var outputs = new Dictionary<int, string>(indices.Length);
        var pending = indices.ToHashSet();
        var finished = 0;
        bool IsCurrent() => Volatile.Read(ref finished) == 0 && !budget.IsCancellationRequested &&
            cache.Generation == generation && (progress?.IsCurrent ?? true);
        try
        {
            while (pending.Count > 0)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (!IsCurrent()) return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
                // Re-read after each inference: a seek reprioritizes only remaining IDs, without
                // restarting the request, losing earlier IDs or generating duplicate translations.
                var id = NextLine(source, pending, progress?.Position ?? TimeSpan.MinValue);
                var segmentOutputs = new List<string>();
                foreach (var segment in LyricsLanguagePolicy.EligibleSegments(source.Lines[id], targetLanguage))
                {
                    var prompt = PlainHyLyricsProtocol.BuildPrompt(segment, targetLanguage);
                    var segmentOutput = await infer(prompt, budget.Token).ConfigureAwait(false);
                    budget.Token.ThrowIfCancellationRequested();
                    if (!IsCurrent()) return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
                    if (!PlainHyLyricsProtocol.IsCompleteLine(segmentOutput)) return new(source, LyricsTranslationOutcome.Failed);
                    segmentOutputs.Add(segmentOutput.Trim());
                }
                var output = string.Join(" ", segmentOutputs);
                budget.Token.ThrowIfCancellationRequested();
                if (!IsCurrent()) return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
                // Preserve the complete response. Never crop extra lines, explanations or broken output
                // to manufacture a valid lyric. IDs are attached here, never generated by the model.
                if (!PlainHyLyricsProtocol.IsCompleteLine(output)) return new(source, LyricsTranslationOutcome.Failed);
                var json = JsonSerializer.Serialize(new[] { new { id, text = output } });
                if (!TryApplyEligibleOutput(json, result, [id], targetLanguage, out var next))
                    return new(source, LyricsTranslationOutcome.Failed);
                result = next;
                outputs.Add(id, output);
                pending.Remove(id);
                if (progress is not null)
                    await progress.WithFence(generation, IsCurrent).ReportAsync(result, id, outputs.Count,
                        indices.Length, budget.Token).ConfigureAwait(false);
            }
            budget.Token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
            // Priority order differs from document order. Validate the entire canonical host-ID
            // sequence again before success or persistence; a partial snapshot is never cacheable.
            var completeJson = JsonSerializer.Serialize(indices.Select(id => new { id, text = outputs[id] }));
            if (!TryApplyEligibleOutput(completeJson, source, indices, targetLanguage, out result))
                return new(source, LyricsTranslationOutcome.Failed);
            if (!LyricsTranslationOutput.HasUsefulLocalTranslation(result, targetLanguage))
            {
                RememberNoUseful(key, generation);
                return new(source, LyricsTranslationOutcome.NoUsefulTranslation);
            }
            if (Encoding.UTF8.GetByteCount(completeJson) <= LyricsTranslationOutput.MaximumOutputBytes)
            {
                try { await cache.WriteAsync(key, completeJson, generation, budget.Token, IsCurrent).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            budget.Token.ThrowIfCancellationRequested();
            return IsCurrent() ? new(result, LyricsTranslationOutcome.Translated)
                : new(source, LyricsTranslationOutcome.NoUsefulTranslation);
        }
        finally { Interlocked.Exchange(ref finished, 1); }
    }

    private static bool TryApplyEligibleOutput(string json, LyricsDocument source, IReadOnlyList<int> indices,
        string targetLanguage, out LyricsDocument result)
    {
        var projected = source with { Lines = source.Lines.Select(line => line with
            { Text = string.Join(" ", LyricsLanguagePolicy.EligibleSegments(line, targetLanguage)) }).ToArray() };
        if (!LyricsTranslationOutput.TryApply(json, projected, indices, targetLanguage, out var mapped))
        { result = source; return false; }
        result = mapped with { Lines = mapped.Lines.Select((line, id) => line with { Text = source.Lines[id].Text }).ToArray() };
        return true;
    }

    private static int NextLine(LyricsDocument source, HashSet<int> pending, TimeSpan position)
    {
        var current = -1;
        for (var index = 0; index < source.Lines.Count; index++)
            if (source.Lines[index].Start <= position) current = index;
        if (current >= 0 && pending.Contains(current) &&
            (source.Lines[current].End <= source.Lines[current].Start || position < source.Lines[current].End)) return current;
        return pending.OrderBy(id => source.Lines[id].Start > position ? 0 : 1)
            .ThenBy(id => source.Lines[id].Start).ThenBy(id => id).First();
    }

    private bool IsNoUseful(string key, long generation)
    {
        lock (_memoGate)
        {
            if (_noUseful.TryGetValue(key, out var value) && value.Generation == generation && value.Until > DateTimeOffset.UtcNow) return true;
            _noUseful.Remove(key);
            return false;
        }
    }
    private void RememberNoUseful(string key, long generation)
    {
        lock (_memoGate)
        {
            if (cache.Generation != generation) return;
            if (_noUseful.Count >= 64) _noUseful.Remove(_noUseful.MinBy(x => x.Value.Until).Key);
            _noUseful[key] = (generation, DateTimeOffset.UtcNow.AddMinutes(10));
        }
    }
}

public sealed class PlainHyLyricsBackend(PlainHyLyricsCoordinator coordinator, IPlainLyricsRunner runner,
    AiLyricsRuntimePackage runtime, string stagingDirectory) : IAiLyricsBackend
{
    public const string BackendId = "hy-q8-plain-beta-v1";
    public string Id => BackendId;

    public Task<LyricsTranslationResult?> TryGetCachedResultAsync(string selectionId, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source = LyricsLanguagePolicy.IdentifyProviderTranslations(source);
        var model = AiLyricsModelCatalog.FindSelectable(selectionId);
        if (model is null ||
            LyricsTranslationPolicy.HasMatchingProviderTranslation(source, targetLanguage) ||
            LyricsLanguagePolicy.EligibleIndices(source, targetLanguage).Length == 0)
            return Task.FromResult<LyricsTranslationResult?>(null);
        string identity;
        try { identity = PlainHyLyricsProtocol.InferenceIdentity(runtime.GetManifestCacheIdentity(), model.Sha256); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { return Task.FromResult<LyricsTranslationResult?>(null); }
        return coordinator.TryGetCachedAsync(query, source, targetLanguage, identity, token);
    }

    public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token) =>
        TranslateAsync(package, query, source, targetLanguage, token, null);

    public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token, LyricsTranslationProgressContext? progress)
    {
        token.ThrowIfCancellationRequested();
        source = LyricsLanguagePolicy.IdentifyProviderTranslations(source);
        if (LyricsTranslationPolicy.HasMatchingProviderTranslation(source, targetLanguage) ||
            LyricsLanguagePolicy.EligibleIndices(source, targetLanguage).Length == 0)
            return Task.FromResult(new LyricsTranslationResult(source, LyricsTranslationOutcome.NoUsefulTranslation));
        // Unannotated legacy packages remain the original 1.8B profile only. New resolutions
        // carry both catalog ID and verified bytes; neither may silently select another model.
        var model = package.ModelId is null && package.VerifiedModelSha256 is null
            ? AiLyricsModelCatalog.ExperimentalPlain : AiLyricsModelCatalog.FindSelectable(package.ModelId ?? string.Empty);
        if (model is null || (package.VerifiedModelSha256 is not null &&
                !string.Equals(package.VerifiedModelSha256, model.Sha256, StringComparison.OrdinalIgnoreCase)) ||
            (package.ModelId is not null && package.VerifiedModelSha256 is null) ||
            package.BackendId != Id || package.Ct2Route is not null ||
            package.CacheIdentity != PlainHyLyricsProtocol.InferenceIdentity(runtime.GetManifestCacheIdentity(), model.Sha256))
            throw new InvalidDataException("Resolved model/runtime does not belong to the plaintext Beta profile.");
        return coordinator.TranslateAsync(query, source, targetLanguage, package.CacheIdentity,
            package.CacheGeneration ?? throw new InvalidDataException("Missing cache generation."),
            (prompt, cancellation) => runner.RunPlainAsync(package.RuntimePath, package.ModelPath, prompt,
                stagingDirectory, cancellation, model.Sha256), token, progress);
    }
    public Task DrainCleanupAsync(CancellationToken token) => runner.DrainCleanupAsync(token);
    public void Dispose() => runner.Dispose();
}
