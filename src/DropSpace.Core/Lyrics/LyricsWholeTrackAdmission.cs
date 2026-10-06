namespace DropSpace.Core.Lyrics;

public enum LyricsWholeTrackDecision { NoContent, OriginalTarget, ProviderTarget, AllowAi }
public enum LyricsLanguageRole { Original, ProviderTranslation }

/// <summary>Actual fastText scores. Unknown describes a business mapping, not an extra model label.</summary>
public sealed record LyricsLanguagePrediction(string? Label, double Score, double RunnerUpScore = 0,
    string ModelIdentity = "", bool CacheHit = false, string? Diagnostic = null)
{
    public static LyricsLanguagePrediction Unknown(string reason) => new(null, 0, Diagnostic: reason);
}

public sealed record LyricsWholeTrackAdmission(LyricsWholeTrackDecision Decision, string Target,
    string SourceIdentity, string OriginalRevision, string TranslationRevision, string RuleVersion,
    string Reason, LyricsLanguagePrediction OriginalPrediction, LyricsLanguagePrediction? TranslationPrediction = null,
    long Generation = 0)
{
    public bool AllowsAi => Decision == LyricsWholeTrackDecision.AllowAi;
}

/// <summary>One shared offline identifier prepares original and, when needed, provider translation roles.</summary>
public interface ILyricsLanguageIdentifier
{
    Task<LyricsDocument> PrepareAsync(LyricsDocument document, string targetLanguage, CancellationToken cancellationToken);
}
