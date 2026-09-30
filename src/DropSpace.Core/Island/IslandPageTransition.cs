namespace DropSpace.Core.Island;

/// <summary>Frame-driven page crossfade. Retargeting preserves every visible page's progress.</summary>
public sealed class IslandPageTransition
{
    private readonly double[] _progress = new double[Enum.GetValues<IslandPage>().Length];
    public IslandPageTransition() => _progress[(int)IslandPage.Files] = 1;
    public IslandPage Target { get; private set; } = IslandPage.Files;
    public bool IsAnimating { get; private set; }
    public double Progress(IslandPage page) => _progress[(int)page];
    public void Select(IslandPage page, bool immediate)
    {
        if (!Enum.IsDefined(page)) throw new ArgumentOutOfRangeException(nameof(page));
        Target = page;
        if (immediate)
        {
            for (var i = 0; i < _progress.Length; i++) _progress[i] = i == (int)page ? 1 : 0;
            IsAnimating = false;
        }
        else IsAnimating = _progress[(int)page] != 1;
    }
    public void Step(TimeSpan elapsed)
    {
        if (!IsAnimating || elapsed <= TimeSpan.Zero) return;
        var blend = 1 - Math.Exp(-24 * Math.Min(elapsed.TotalSeconds, 0.25));
        for (var i = 0; i < _progress.Length; i++)
            _progress[i] += ((i == (int)Target ? 1 : 0) - _progress[i]) * blend;
        if (_progress[(int)Target] >= 0.999) Select(Target, true);
    }
}
