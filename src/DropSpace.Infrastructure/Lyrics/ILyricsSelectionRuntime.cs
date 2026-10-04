namespace DropSpace.Infrastructure.Lyrics;

public interface ILyricsSelectionRuntime
{
    bool IsSelectionWarm(string modelHash);
    Task<bool> PrepareSelectionAsync(string verifiedModelPath, string modelHash, CancellationToken token);
    Task<string?> TryRunSelectionAsync(string modelHash, string prompt, CancellationToken token);
}
