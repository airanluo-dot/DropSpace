namespace DropSpace.App.Services;

internal sealed record SmartDragProbeOptions(
    int OuterSizePixels = 144,
    int CenterHolePixels = 12,
    int HardLifetimeMilliseconds = 60,
    int MaximumSimultaneousProbes = 1)
{
    public static SmartDragProbeOptions Default { get; } = new();

    public TimeSpan HardLifetime => TimeSpan.FromMilliseconds(HardLifetimeMilliseconds);

    public void Validate()
    {
        if (OuterSizePixels is < 120 or > 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OuterSizePixels),
                "The Smart drag probe outer size must remain between 120 and 160 physical pixels.");
        }

        if (CenterHolePixels is < 8 or > 16 || CenterHolePixels >= OuterSizePixels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CenterHolePixels),
                "The Smart drag probe center hole must remain between 8 and 16 physical pixels.");
        }

        if (HardLifetimeMilliseconds is < 10 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardLifetimeMilliseconds),
                "The Smart drag probe hard lifetime must remain between 10 and 100 milliseconds.");
        }

        if (MaximumSimultaneousProbes != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumSimultaneousProbes),
                "Smart mode permits exactly one ephemeral OLE verification probe.");
        }
    }
}
