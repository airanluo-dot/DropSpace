namespace DropSpace.Core.Media;

// Fixed six-capsule port of apple-like-waves' CapsuleSpringFollower. A closed-form
// step with a constant target is equivalent to composing its 120 Hz substeps.
public sealed class CapsuleSpectrumMotion
{
    private readonly double[] _positions = new double[SpectrumAnalyzer.BandCount];
    private readonly double[] _velocities = new double[SpectrumAnalyzer.BandCount];
    public IReadOnlyList<double> Positions => _positions;
    public bool IsMoving { get; private set; }

    public void Reset()
    {
        Array.Clear(_positions);
        Array.Clear(_velocities);
        IsMoving = false;
    }

    public bool Advance(IReadOnlyList<double> bands, bool playing, bool reducedMotion, double elapsed)
    {
        var dt = Math.Clamp(elapsed, 0, 1d / 30);
        var omega = 8.95 / (playing ? .325 : .60);
        var damping = playing ? .83 : 1;
        var decay = Math.Exp(-damping * omega * dt);
        var frequency = omega * Math.Sqrt(1 - damping * damping);
        var cosine = Math.Cos(frequency * dt);
        var sine = Math.Sin(frequency * dt);
        var changed = false;
        IsMoving = false;
        for (var i = 0; i < _positions.Length; i++)
        {
            var target = playing ? bands[i] : 0;
            var displacement = _positions[i] - target;
            var velocity = _velocities[i];
            var slope = velocity + damping * omega * displacement;
            var wave = playing ? displacement * cosine + slope / frequency * sine : displacement + slope * dt;
            var derivative = playing ? slope * cosine - frequency * displacement * sine : slope;
            var offset = decay * wave;
            velocity = decay * (derivative - damping * omega * wave);
            if (reducedMotion || (Math.Abs(offset) <= .0008 && Math.Abs(velocity) <= .0016))
                offset = velocity = 0;
            else
                IsMoving = true;
            var next = target + offset;
            changed |= next != _positions[i];
            _positions[i] = next;
            _velocities[i] = velocity;
        }
        return changed;
    }
}
