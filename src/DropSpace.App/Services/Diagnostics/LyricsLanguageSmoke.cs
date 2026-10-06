using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>Explicit release compatibility check, before service composition or normal activation.</summary>
internal static class LyricsLanguageSmoke
{
    public static bool IsRequested(string[] arguments) =>
        arguments.Contains("--lyrics-language-smoke", StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] arguments)
    {
        var outputFlags = arguments.Select((value, index) => (value, index))
            .Where(item => item.value.Equals("--lyrics-language-smoke-output", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (outputFlags.Length != 1 || outputFlags[0].index + 1 >= arguments.Length) return 1;
        var output = arguments[outputFlags[0].index + 1];
        if (!Path.IsPathFullyQualified(output)) return 1;
        object evidence;
        var passed = false;
        try
        {
            // Default production constructor proves the entry assembly's bundled
            // bin and the single-file native resolver; no supplied model path.
            using var identifier = new FastTextLanguageIdentifier();
            var english = Document("I will carry the sunlight across the open water.",
                "And every morning I will wait for you to come back home.");
            var enTarget = await identifier.PrepareAsync(english, "en", default);
            var changedTarget = await identifier.PrepareAsync(enTarget, "zh", default);
            var chinese = await identifier.PrepareAsync(Document("我们一起走过每一个孤独的夜晚", "我会陪着你直到明天的太阳升起"), "zh", default);
            passed = enTarget.TranslationAdmission?.Decision == LyricsWholeTrackDecision.OriginalTarget &&
                changedTarget.TranslationAdmission?.Decision == LyricsWholeTrackDecision.AllowAi &&
                changedTarget.TranslationAdmission.OriginalPrediction.CacheHit &&
                chinese.TranslationAdmission?.Decision == LyricsWholeTrackDecision.OriginalTarget &&
                identifier.ModelLoadCount == 1 && identifier.PredictionCount == 2 &&
                identifier.LoadedModelPath is not null && identifier.LoadedNativePath is not null;
            evidence = new { schemaVersion = 1, passed, mode = "bundled-offline-language-smoke",
                identifier.ModelIdentity, identifier.NativeIdentity, identifier.LoadedModelPath, identifier.LoadedNativePath,
                identifier.ModelLoadCount, identifier.PredictionCount,
                rule = LyricsLanguagePolicy.Version, preprocessing = LyricsLanguagePolicy.PreprocessingVersion,
                cases = new[] { Describe("english-target", enTarget), Describe("same-original-target-switch", changedTarget),
                    Describe("chinese-target", chinese) } };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            evidence = new { schemaVersion = 1, passed = false, mode = "bundled-offline-language-smoke",
                error = error.GetType().Name, reason = error.Message };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 1; }
        return passed ? 0 : 1;
    }

    private static object Describe(string name, LyricsDocument document) => new { name,
        document.TranslationAdmission?.Target, decision = document.TranslationAdmission?.Decision.ToString(),
        document.TranslationAdmission?.Reason, prediction = document.TranslationAdmission?.OriginalPrediction,
        eligibleAiLines = LyricsLanguagePolicy.EligibleIndices(document, document.TranslationAdmission?.Target ?? string.Empty).Length };
    private static LyricsDocument Document(params string[] lines) => new(lines.Select((text, index) =>
        new LyricsLine(TimeSpan.FromSeconds(index), TimeSpan.FromSeconds(index + 1), text, null, [])).ToArray(), LyricsProviderKind.LocalLrc);
}
