using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Return a language only when the entire requested document has that confidently identified
/// source. Unknown or mixed-language input must abstain. The identity versions the detector and policy.</summary>
public interface ILyricsSourceIdentifier
{
    string CacheIdentity { get; }
    Task<string?> IdentifyAsync(LyricsQuery query, LyricsDocument document, CancellationToken token);
}

public interface ICt2PackageResolver
{
    Task<(Ct2PackageReference Reference, Ct2PackageIdentity Identity)?> ResolveAsync(
        string sourceLanguage, string targetLanguage, CancellationToken token);
}

/// <summary>The runner checks the resolved identity again under its retained file leases before launch.</summary>
public interface ICt2InferenceRunner : IDisposable
{
    Task<IReadOnlyList<Ct2TranslationLine>> TranslateVerifiedAsync(Ct2PackageReference reference,
        Ct2PackageIdentity expectedIdentity, string source, string target, IReadOnlyList<Ct2SourceLine> lines,
        CancellationToken token);
    Task DrainCleanupAsync(CancellationToken token);
}

public sealed class AbstainingLyricsSourceIdentifier : ILyricsSourceIdentifier
{
    public string CacheIdentity => "abstain-until-reviewed-lid-v1";
    public Task<string?> IdentifyAsync(LyricsQuery query, LyricsDocument document, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}

public sealed record Ct2ReviewedRoute(string Source, string Target, Ct2PackageReference Reference);
public sealed record Ct2ResolvedLeg(string Source, string Target, Ct2PackageReference Reference, Ct2PackageIdentity Identity);

/// <summary>A snapshot of a verified direct/pivot route. No path or identity is model-generated.</summary>
public sealed class Ct2ResolvedRoute
{
    public string SourceIdentifier { get; }
    public string TargetLanguage { get; }
    public IReadOnlyList<Ct2ResolvedLeg> Legs { get; }
    public string CacheIdentity { get; }

    public Ct2ResolvedRoute(string sourceIdentifier, string targetLanguage, IEnumerable<Ct2ResolvedLeg> legs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentifier);
        var snapshot = legs.ToArray();
        var target = Ct2LyricsPackageResolver.NormalizeTarget(targetLanguage);
        if (target.Length == 0 || snapshot.Length is < 1 or > 2 || snapshot.Any(x => x is null ||
                !Ct2RoutePlanner.IsSupported(x.Source, x.Target)) || snapshot[^1].Target != target ||
            (snapshot.Length == 2 && (snapshot[0].Source is not ("ja" or "ko") || snapshot[0].Target != "en" ||
                snapshot[1].Source != "en" || snapshot[1].Target != "zh")))
            throw new InvalidDataException("Invalid CT2 route.");
        foreach (var leg in snapshot)
        {
            Ct2PrivatePackage.ValidateHash(leg.Reference.ManifestSha256);
            if (!StringComparer.OrdinalIgnoreCase.Equals(leg.Reference.ManifestSha256, leg.Identity.ManifestSha256))
                throw new InvalidDataException("CT2 route manifest identity mismatch.");
        }
        SourceIdentifier = sourceIdentifier;
        TargetLanguage = LyricsTranslationPolicy.NormalizeLanguage(targetLanguage);
        Legs = Array.AsReadOnly(snapshot);
        CacheIdentity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = "ct2-reviewed-route-v2", sourceIdentifier, target = TargetLanguage,
            legs = snapshot.Select(x => new { x.Source, x.Target,
                identity = x.Identity.CacheIdentity(x.Source, x.Target, snapshot.Length == 1 ? "direct" : "pivot") }),
        })));
    }
}

/// <summary>Only explicit out-of-band reviewed references are eligible; never discovers or downloads files.</summary>
public sealed class Ct2PrivatePackageResolver : ICt2PackageResolver
{
    private readonly IReadOnlyDictionary<(string, string), Ct2PackageReference> _routes;
    public Ct2PrivatePackageResolver(IEnumerable<Ct2ReviewedRoute> routes)
    {
        var snapshot = routes.ToArray();
        if (snapshot.Any(x => !Ct2RoutePlanner.IsSupported(x.Source, x.Target)))
            throw new InvalidDataException("Unsupported reviewed CT2 route.");
        _routes = snapshot.ToDictionary(x => (x.Source, x.Target), x => x.Reference);
    }

    public async Task<(Ct2PackageReference Reference, Ct2PackageIdentity Identity)?> ResolveAsync(
        string sourceLanguage, string targetLanguage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_routes.TryGetValue((sourceLanguage, targetLanguage), out var reference)) return null;
        using var package = await Ct2PrivatePackage.OpenAsync(reference, sourceLanguage, targetLanguage, token).ConfigureAwait(false);
        return (reference, package.Identity);
    }
}

/// <summary>Candidate-only resolver. Not registered in shipping DI or in the user-visible model catalog.</summary>
public sealed class Ct2LyricsPackageResolver(string selectionId, ILyricsSourceIdentifier sourceIdentifier,
    ICt2PackageResolver packages, AiLyricsCache cache) : IAiLyricsPackageResolver
{
    public async Task<AiLyricsResolvedPackage?> ResolveAsync(string selected, LyricsQuery query,
        LyricsDocument document, string targetLanguage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var generation = cache.Generation;
        if (!StringComparer.Ordinal.Equals(selected, selectionId) || document.Lines.Count is 0 or > 500 ||
            LyricsTranslationPolicy.HasMatchingProviderTranslation(document, targetLanguage)) return null;
        var target = NormalizeTarget(targetLanguage);
        if (target.Length == 0) return null;
        var source = (await sourceIdentifier.IdentifyAsync(query, document, token).ConfigureAwait(false) ?? "")
            .Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        token.ThrowIfCancellationRequested();
        if (!Ct2RoutePlanner.IsSupported(source, target)) return null;
        var legs = new List<Ct2ResolvedLeg>();
        async Task<bool> Find(string from, string to)
        {
            var found = await packages.ResolveAsync(from, to, token).ConfigureAwait(false);
            if (found is null) return false;
            legs.Add(new(from, to, found.Value.Reference, found.Value.Identity));
            return true;
        }
        if (!await Find(source, target).ConfigureAwait(false))
        {
            if (target != "zh" || source is not ("ja" or "ko") ||
                !await Find(source, "en").ConfigureAwait(false) || !await Find("en", "zh").ConfigureAwait(false)) return null;
        }
        token.ThrowIfCancellationRequested();
        var route = new Ct2ResolvedRoute(sourceIdentifier.CacheIdentity, targetLanguage, legs);
        return new(Ct2LyricsBackend.BackendId, route.CacheIdentity, "", "", null, route, generation,
            LyricsTranslationPrompt.CacheKey(query, document, targetLanguage, route.CacheIdentity));
    }

    internal static string NormalizeTarget(string target) => LyricsTranslationPolicy.NormalizeLanguage(target) switch
    { "en" => "en", "zh-Hans" => "zh", _ => "" };
}

/// <summary>Executable candidate orchestration, enabled only by explicit DI with a reviewed resolver.
/// The shipping composition root selects GGUF; its default source identifier still always abstains.</summary>
public sealed class Ct2LyricsPipeline(ICt2InferenceRunner helper, AiLyricsCache cache)
{
    public async Task<LyricsTranslationResult> TranslateAsync(Ct2ResolvedRoute route, LyricsQuery query,
        LyricsDocument original, string targetLanguage, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (cache.Generation != generation) return new(original, LyricsTranslationOutcome.NoUsefulTranslation);
        if (route.TargetLanguage != LyricsTranslationPolicy.NormalizeLanguage(targetLanguage))
            throw new InvalidDataException("CT2 resolved target changed before inference.");
        if (LyricsTranslationPolicy.HasMatchingProviderTranslation(original, targetLanguage))
            return new(original, LyricsTranslationOutcome.NoUsefulTranslation);
        var indices = Enumerable.Range(0, original.Lines.Count).Where(i => !string.IsNullOrWhiteSpace(original.Lines[i].Text)).ToArray();
        if (original.Lines.Count > 500 || indices.Length == 0) return new(original, LyricsTranslationOutcome.NoUsefulTranslation);
        // Reuse full app-owned track/provider/timing/secondary identity with an isolated route digest.
        var identity = LyricsTranslationPrompt.CacheKey(query, original, targetLanguage, route.CacheIdentity);
        var saved = await cache.ReadAsync(identity, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (cache.Generation != generation) return new(original, LyricsTranslationOutcome.NoUsefulTranslation);
        if (saved is not null && LyricsTranslationOutput.TryApply(saved, original, indices, targetLanguage, out var cached))
            return LyricsTranslationOutput.HasUsefulLocalTranslation(cached, targetLanguage)
                ? new(cached, LyricsTranslationOutcome.Translated) : new(original, LyricsTranslationOutcome.NoUsefulTranslation);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(180));
        var completed = new List<Ct2TranslationLine>(indices.Length);
        var result = original;
        // Each stdio request and response stays bounded, including every pivot leg. No partial
        // batch, leg, or song is exposed to the app or cache before all validation succeeds.
        foreach (var batch in indices.Chunk(12))
        {
            IReadOnlyList<Ct2SourceLine> input = batch.Select(i => new Ct2SourceLine(i, original.Lines[i].Text)).ToArray();
            IReadOnlyList<Ct2TranslationLine>? output = null;
            foreach (var leg in route.Legs)
            {
                budget.Token.ThrowIfCancellationRequested();
                output = await helper.TranslateVerifiedAsync(leg.Reference, leg.Identity, leg.Source, leg.Target, input, budget.Token).ConfigureAwait(false);
                budget.Token.ThrowIfCancellationRequested();
                ValidateComplete(input, output);
                // Apply to a private intermediate document only to reuse all app protocol guards,
                // including invalid Unicode, control characters, duplicate keys, and leaked fields.
                var batchJson = JsonSerializer.Serialize(output.Select(x => new { id = x.Id, text = x.Text }));
                if (!LyricsTranslationOutput.TryApply(batchJson, original, batch, targetLanguage, out _))
                    throw new InvalidDataException("CT2 route leg failed app output validation.");
                input = output.Select(x => new Ct2SourceLine(x.Id, x.Text)).ToArray();
            }
            completed.AddRange(output!);
            var json = JsonSerializer.Serialize(output!.Select(x => new { id = x.Id, text = x.Text }));
            if (!LyricsTranslationOutput.TryApply(json, result, batch, targetLanguage, out var next))
                throw new InvalidDataException("CT2 completed batch failed app output validation.");
            result = next;
        }
        budget.Token.ThrowIfCancellationRequested();
        if (cache.Generation != generation || !LyricsTranslationOutput.HasUsefulLocalTranslation(result, targetLanguage))
            return new(original, LyricsTranslationOutcome.NoUsefulTranslation);
        var completedJson = JsonSerializer.Serialize(completed.Select(x => new { id = x.Id, text = x.Text }));
        if (Encoding.UTF8.GetByteCount(completedJson) <= LyricsTranslationOutput.MaximumOutputBytes)
        {
            try { await cache.WriteAsync(identity, completedJson, generation, budget.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        budget.Token.ThrowIfCancellationRequested();
        if (cache.Generation != generation) return new(original, LyricsTranslationOutcome.NoUsefulTranslation);
        return new(result, LyricsTranslationOutcome.Translated);
    }

    private static void ValidateComplete(IReadOnlyList<Ct2SourceLine> input, IReadOnlyList<Ct2TranslationLine> output)
    {
        if (output is null || output.Count != input.Count || output.Where((line, i) => line is null ||
                line.Id != input[i].Id || string.IsNullOrWhiteSpace(line.Text) || Encoding.UTF8.GetByteCount(line.Text) > 4096 ||
                line.Text.Any(c => char.IsControl(c) && c != '\t')).Any())
            throw new InvalidDataException("A CT2 route leg returned partial, invalid, or reordered output.");
    }
}

public sealed class Ct2LyricsBackend(ICt2InferenceRunner runner, AiLyricsCache cache) : IAiLyricsBackend
{
    public const string BackendId = "ct2-candidate-v1";
    public string Id => BackendId;
    private readonly Ct2LyricsPipeline _pipeline = new(runner, cache);

    public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token)
    {
        if (package.BackendId != Id || package.Ct2Route is null || package.CacheIdentity != package.Ct2Route.CacheIdentity ||
            package.RequestIdentity != LyricsTranslationPrompt.CacheKey(query, source, targetLanguage, package.CacheIdentity))
            throw new InvalidDataException("The resolved package does not belong to this CT2 route.");
        return _pipeline.TranslateAsync(package.Ct2Route, query, source, targetLanguage,
            package.CacheGeneration ?? throw new InvalidDataException("CT2 resolution has no cache generation."), token);
    }
    public Task DrainCleanupAsync(CancellationToken token) => runner.DrainCleanupAsync(token);
    public void Dispose() => runner.Dispose();
}
