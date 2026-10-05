namespace DropSpace.Infrastructure.Lyrics;

public interface ILyricsSelectionRuntime
{
    // Cheap readiness hint before expensive model verification. Prepare still
    // owns the atomic admission check after verification.
    bool CanPrepareSelection { get; }
    // Protocol support is separate from an evaluated recording-identity profile.
    // Test runtimes may explicitly model a qualified profile without claiming model ability.
    bool IsSelectionProfileQualified(string modelHash) => false;
    bool IsSelectionWarm(string modelHash);
    Task<bool> PrepareSelectionAsync(string verifiedModelPath, string modelHash, CancellationToken token);
    Task<string?> TryRunSelectionAsync(string modelHash, string prompt, CancellationToken token);
}
