namespace DropSpace.App.Services;

internal static class SmartDragRuntimePolicy
{
    public const int DefaultProbeOuterSizePixels = 144;
    public const int DefaultProbeCenterHolePixels = 12;
    public const int DefaultProbeHardLifetimeMilliseconds = 60;
    public const int MaximumSimultaneousProbes = 1;

    public const int MinimumProbeOuterSizePixels = 120;
    public const int MaximumProbeOuterSizePixels = 160;
    public const int MinimumProbeCenterHolePixels = 8;
    public const int MaximumProbeCenterHolePixels = 16;
    public const int MinimumProbeHardLifetimeMilliseconds = 10;
    public const int MaximumProbeHardLifetimeMilliseconds = 100;

    public const int PointerReleaseGraceMilliseconds = 350;
    public const int SessionTimeoutSeconds = 30;
    public const int ProbeCleanupWatchdogMilliseconds = 250;
    public const int ObserverShutdownTimeoutSeconds = 2;
    public const int WaitPollSliceMilliseconds = 20;

    public const double SlowVelocityPixelsPerSecond = 500;
    public const double MediumVelocityPixelsPerSecond = 1_500;
    public const double FastVelocityPixelsPerSecond = 3_000;

    public static TimeSpan PointerReleaseGrace =>
        TimeSpan.FromMilliseconds(PointerReleaseGraceMilliseconds);

    public static TimeSpan SessionTimeout =>
        TimeSpan.FromSeconds(SessionTimeoutSeconds);

    public static TimeSpan ProbeCleanupWatchdog =>
        TimeSpan.FromMilliseconds(ProbeCleanupWatchdogMilliseconds);

    public static TimeSpan ObserverShutdownTimeout =>
        TimeSpan.FromSeconds(ObserverShutdownTimeoutSeconds);

    public static TimeSpan WaitPollSlice =>
        TimeSpan.FromMilliseconds(WaitPollSliceMilliseconds);
}
