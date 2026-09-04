namespace DropSpace.App.Services;

internal sealed record SmartDragProbeOptions(
    int OuterSizePixels = SmartDragRuntimePolicy.DefaultProbeOuterSizePixels,
    int CenterHolePixels = SmartDragRuntimePolicy.DefaultProbeCenterHolePixels,
    int HardLifetimeMilliseconds = SmartDragRuntimePolicy.DefaultProbeHardLifetimeMilliseconds,
    int MaximumSimultaneousProbes = SmartDragRuntimePolicy.MaximumSimultaneousProbes)
{
    public static SmartDragProbeOptions Default { get; } = new();

    public TimeSpan HardLifetime => TimeSpan.FromMilliseconds(HardLifetimeMilliseconds);

    public void Validate()
    {
        if (OuterSizePixels is < SmartDragRuntimePolicy.MinimumProbeOuterSizePixels or > SmartDragRuntimePolicy.MaximumProbeOuterSizePixels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OuterSizePixels),
                "The Smart drag probe outer size must remain between 120 and 160 physical pixels.");
        }

        if (CenterHolePixels is < SmartDragRuntimePolicy.MinimumProbeCenterHolePixels or > SmartDragRuntimePolicy.MaximumProbeCenterHolePixels || CenterHolePixels >= OuterSizePixels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CenterHolePixels),
                "The Smart drag probe center hole must remain between 8 and 16 physical pixels.");
        }

        if (HardLifetimeMilliseconds is < SmartDragRuntimePolicy.MinimumProbeHardLifetimeMilliseconds or > SmartDragRuntimePolicy.MaximumProbeHardLifetimeMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardLifetimeMilliseconds),
                "The Smart drag probe hard lifetime must remain between 10 and 100 milliseconds.");
        }

        if (MaximumSimultaneousProbes != SmartDragRuntimePolicy.MaximumSimultaneousProbes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumSimultaneousProbes),
                "Smart mode permits exactly one ephemeral OLE verification probe.");
        }
    }
}
