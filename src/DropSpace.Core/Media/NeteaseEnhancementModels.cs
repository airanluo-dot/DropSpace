namespace DropSpace.Core.Media;

public enum NeteaseEnhancementStage
{
    NotInstalled, Detecting, Preparing, Installing, Restarting, Verifying,
    Installed, Enhanced, Removing, Removed, Failed,
}

public sealed record NeteaseEnhancementState(
    NeteaseEnhancementStage Stage,
    bool IsManaged = false,
    string? Version = null,
    string? ErrorCode = null)
{
    public bool IsBusy => Stage is NeteaseEnhancementStage.Detecting or NeteaseEnhancementStage.Preparing or
        NeteaseEnhancementStage.Installing or NeteaseEnhancementStage.Restarting or
        NeteaseEnhancementStage.Verifying or NeteaseEnhancementStage.Removing;
}

/// <summary>Observed Windows SMTC evidence, never inferred from installation files.</summary>
public sealed record NeteaseMediaCapabilities(
    bool Title, bool Artist, bool Album, bool Artwork, bool PlaybackState,
    bool Play, bool Pause, bool Previous, bool Next, bool Timeline,
    bool LiveProgress, bool Seek)
{
    public bool Complete => Title && Artist && Album && Artwork && PlaybackState &&
        Play && Pause && Previous && Next && Timeline && LiveProgress && Seek;
    public static NeteaseMediaCapabilities Empty { get; } = new(false, false, false, false,
        false, false, false, false, false, false, false, false);
}

public interface INeteaseEnhancementService : IAsyncDisposable
{
    NeteaseEnhancementState Current { get; }
    event EventHandler<NeteaseEnhancementState>? Changed;
    Task InspectAsync(CancellationToken cancellationToken = default);
    Task EnhanceAsync(bool reinstall = false, CancellationToken cancellationToken = default);
    Task RemoveAsync(CancellationToken cancellationToken = default);
}
