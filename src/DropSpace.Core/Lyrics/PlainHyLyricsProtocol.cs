using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DropSpace.Core.Lyrics;

/// <summary>Frozen, opt-in Beta protocol. Model-facing text is only the evaluated official target/source
/// template. Host IDs, metadata, reviewer labels and semantic expectations never reach generation.</summary>
public static class PlainHyLyricsProtocol
{
    public const string Version = "official-plain-per-line-v1";
    public const string HostMappingVersion = "host-mapped-id-text-v1";
    public const string AcceptanceVersion = "unknown-copy-neutral-complete-song-v1";
    public const string SamplerIdentity = "seed42-temp0.1-topk20-topp0.8-minp0.05-repeat1-frequency0-presence0-t4-tb4-c4096-n2048";
    public const int MaximumPromptBytes = 1800;
    public const int MaximumOutputBytes = 16_384;
    // Processing ceiling; independent of the audio duration and external cancellation.
    public const int WholeSongSeconds = 600;
    public const string EnglishTarget = "英语";
    public const string ChineseTarget = "简体中文";
    public const string Template = "将以下文本翻译为{0}，注意只需要输出翻译后的结果，不要额外解释：\n{1}";

    public static string BuildPrompt(string source, string targetLanguage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var name = LyricsTranslationPolicy.NormalizeLanguage(targetLanguage) switch
        {
            "en" => EnglishTarget,
            "zh-Hans" => ChineseTarget,
            _ => throw new ArgumentException("Unsupported plaintext translation target.", nameof(targetLanguage)),
        };
        var prompt = string.Format(System.Globalization.CultureInfo.InvariantCulture, Template, name, source);
        // Conservative byte bound leaves room in the fixed context for template/chat wrapper and
        // 2048 generated tokens without an extra cold tokenizer process for each short lyric line.
        if (Encoding.UTF8.GetByteCount(prompt) > MaximumPromptBytes)
            throw new InvalidDataException("A lyric line exceeds the plaintext context budget.");
        return prompt;
    }

    public static bool IsCompleteLine(string text) => !string.IsNullOrWhiteSpace(text) &&
        Encoding.UTF8.GetByteCount(text) <= MaximumOutputBytes && text.Length <= 4096 &&
        !text.Any(c => char.IsControl(c) && c != '\t') && !text.Contains("```", StringComparison.Ordinal) &&
        !text.Contains('\uFFFD');

    public static string InferenceIdentity(string runtimeManifestSha256) =>
        InferenceIdentity(runtimeManifestSha256, AiLyricsModelCatalog.ExperimentalPlain.Sha256);

    public static string InferenceIdentity(string runtimeManifestSha256, string verifiedModelSha256)
    {
        if (runtimeManifestSha256.Length != 64 || !runtimeManifestSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A pinned runtime manifest identity is required.", nameof(runtimeManifestSha256));
        var model = AiLyricsModelCatalog.FindSelectableByHash(verifiedModelSha256) ??
            throw new ArgumentException("A selectable pinned plaintext model identity is required.", nameof(verifiedModelSha256));
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            backend = "hy-q8-plain-beta-v1", protocol = Version, mapping = HostMappingVersion,
            acceptance = AcceptanceVersion, template = Template, targets = new[] { EnglishTarget, ChineseTarget },
            sampler = SamplerIdentity, model = model.Sha256,
            runtime = runtimeManifestSha256.ToLowerInvariant(), maximumPromptBytes = MaximumPromptBytes,
        })));
    }

    public static string CacheKey(LyricsQuery query, LyricsDocument document, string target, string inferenceIdentity)
    {
        // Use the full established app-owned track/provider/timing identity, then independently
        // version this protocol. An old JSON strategy can never supply a plaintext cache hit.
        // Derived AI presentation is not provider source identity. A current bound projection
        // can reuse its original cache entry while keeping the model/runtime and cache fences.
        var originals = document with { Lines = document.Lines.Select(line => line.TranslationOrigin == LyricsTranslationOrigin.LocalAi
            ? line with { Secondary = null, TranslationOrigin = LyricsTranslationOrigin.None, TranslationLanguage = null,
                TranslationLanguageIsExplicit = null, LocalAiAdmissionKey = null } : line).ToArray() };
        var documentKey = LyricsTranslationPrompt.CacheKey(query, originals, target, inferenceIdentity);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { cache = "plain-hy-complete-song-v2", eligibility = LyricsLanguagePolicy.Version, sourceLanguages = document.Lines.Select(line => line.SourceLanguage),
            eligibleIds = LyricsLanguagePolicy.EligibleIndices(document, target),
            admittedSegments = LyricsLanguagePolicy.EligibleSegments(document, target), protocol = Version, documentKey })));
    }
}
