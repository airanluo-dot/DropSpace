namespace DropSpace.Infrastructure.Lyrics;

public interface ILyricsSelectionRuntime
{
    // Cheap readiness hint before expensive model verification. Prepare still
    // owns the atomic admission check after verification.
    bool CanPrepareSelection { get; }
    bool IsSelectionWarm(string modelHash);
    Task<bool> PrepareSelectionAsync(string verifiedModelPath, string modelHash, CancellationToken token);
    Task<string?> TryRunSelectionAsync(string modelHash, string prompt, CancellationToken token);
}
