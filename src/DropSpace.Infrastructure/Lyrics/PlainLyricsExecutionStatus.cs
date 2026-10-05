using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

public enum PlainLyricsExecutionPhase { Starting, Executing }

// Immutable observations. Preferences are identity fences, never evidence of GPU execution.
public sealed record PlainLyricsExecutionStatus(string Backend, string ModelSha256,
    bool GpuEnabled, LyricsGpuBackend BackendPreference, long PreferenceGeneration, bool UsedCpuFallback,
    DateTimeOffset CompletedAt, string Operation);

public sealed record PlainLyricsCurrentExecution(string? Backend, string ModelSha256,
    bool GpuEnabled, LyricsGpuBackend BackendPreference, long PreferenceGeneration,
    PlainLyricsExecutionPhase Phase, string Operation);

public sealed record PlainLyricsRuntimeFailure(string RequestedBackend, string AttemptedBackend,
    string ModelSha256, bool GpuEnabled, LyricsGpuBackend BackendPreference, long PreferenceGeneration,
    DateTimeOffset OccurredAt, string Reason, int? ExitCode, bool TerminatedByHost, bool CpuFallbackAttempted);
