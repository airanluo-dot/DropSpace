namespace DropSpace.Core.Media;

/// <summary>Retry a failed or stalled capture without treating silent audio as failure.</summary>
public static class AudioCaptureRecoveryPolicy
{
    public static bool ShouldRecover(bool wanted, AudioCaptureMode mode, TimeSpan sampleAge, TimeSpan sinceAttempt) =>
        wanted && sinceAttempt >= TimeSpan.FromSeconds(5) &&
        (mode != AudioCaptureMode.ProcessLoopback || sampleAge >= TimeSpan.FromSeconds(5));
}
