using DropSpace.Core.Overlay;

namespace DropSpace.App.Services;

/// <summary>
/// A bounded, premultiplied BGRA halo with a small overlap under the island. The signed distance uses the island's
/// unchanged rounded contour; only the light outside it drifts. No bitmap background
/// or shape displacement is used, and every outermost pixel is completely transparent.
/// </summary>
internal sealed class IslandGlowRasterizer
{
    internal const double PaddingDips = OverlayPlacementPolicy.DynamicIslandTopGapDips;
    internal const double InnerOverlapDips = 3;
    private const int AroundSteps = 512;
    private const int DistanceSteps = 160;
    private const int GaussianStepsPerUnit = 256;
    private static readonly double[] GaussianSamples = CreateGaussianSamples();
    private static readonly DistanceProfile[] DistanceProfiles = CreateDistanceProfiles();
    private readonly record struct DistanceProfile(double Distance, double Coverage, double BaseLight, double CoreLight);
    private readonly record struct Sample(int Pixel, int Lookup, int SimplifiedLookup, float SimplifiedCoverage);
    private readonly Sample[] _samples;
    private readonly int[] _lookup = new int[AroundSteps * DistanceSteps];
    private readonly double[] _frameBands = new double[6];
    private double _lastPhase;
    private double _lastBrightness;
    private double _lastSimplification;
    private bool _hasRenderedFrame;

    public IslandGlowRasterizer(int surfaceWidth, int surfaceHeight, int topRadius, int bottomRadius, double scale)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || !double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(surfaceWidth));
        PaddingPixels = checked((int)Math.Ceiling(PaddingDips * scale));
        Width = checked(surfaceWidth + 2 * PaddingPixels);
        Height = checked(surfaceHeight + 2 * PaddingPixels);
        Pixels = new int[checked(Width * Height)];
        var samples = new List<Sample>();
        var halfWidth = surfaceWidth / 2d;
        var halfHeight = surfaceHeight / 2d;
        topRadius = Math.Clamp(topRadius, 0, Math.Min(surfaceWidth, surfaceHeight) / 2);
        bottomRadius = Math.Clamp(bottomRadius, 0, Math.Min(surfaceWidth, surfaceHeight) / 2);
        var bottomQuarter = HalfContourLength(halfWidth, halfHeight, bottomRadius);
        var topQuarter = HalfContourLength(halfWidth, halfHeight, topRadius);
        var perimeter = 2 * (bottomQuarter + topQuarter);
        for (var y = 1; y < Height - 1; y++)
        {
            var dy = y + 0.5 - PaddingPixels - halfHeight;
            var radius = dy < 0 ? topRadius : bottomRadius;
            var qy = Math.Abs(dy) - halfHeight + radius;
            for (var x = 1; x < Width - 1; x++)
            {
                var dx = x + 0.5 - PaddingPixels - halfWidth;
                var qx = Math.Abs(dx) - halfWidth + radius;
                var ox = Math.Max(0, qx);
                var oy = Math.Max(0, qy);
                var distance = (Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius) / scale;
                // The body is above this light. Only a finite inner overlap closes the edge gap.
                if (distance <= -InnerOverlapDips || distance >= PaddingDips - 1) continue;
                // Project onto the nearest point of the actual rounded contour.
                // The same perimeter position applies across its outward normal:
                // a lower-border mask must never become a cone from the center.
                var localArc = qx > 0 && qy > 0
                    ? halfWidth - radius + radius * Math.Atan2(qx, qy)
                    : qx > qy
                        ? halfWidth - radius + Math.PI * radius / 2 + halfHeight - radius - Math.Abs(dy)
                        : Math.Abs(dx);
                var arc = dy >= 0
                    ? dx >= 0 ? localArc : perimeter - localArc
                    : dx >= 0 ? bottomQuarter + topQuarter - localArc : bottomQuarter + topQuarter + localArc;
                var position = Math.Clamp(arc / perimeter, 0, 1);
                var around = (position + .75) % 1;
                var aroundIndex = Math.Min(AroundSteps - 1, (int)(around * AroundSteps));
                var distanceIndex = Math.Clamp((int)((distance + InnerOverlapDips) * DistanceSteps / (PaddingDips + InnerOverlapDips)), 0, DistanceSteps - 1);
                // The source covers the entire bottom tangent and 45 degrees of
                // each lower corner, counted from its lowest point. Project that
                // source downward; the endpoint feather belongs to the source,
                // not a hard cut through the resulting light field.
                var tangent = halfWidth - bottomRadius;
                var sourceEndX = tangent + bottomRadius / Math.Sqrt(2);
                var sourceX = Math.Min(Math.Abs(dx), sourceEndX);
                var cornerX = Math.Max(0, sourceX - tangent);
                var sourceY = halfHeight - bottomRadius + Math.Sqrt(Math.Max(0, bottomRadius * bottomRadius - cornerX * cornerX));
                var sourceArc = sourceX <= tangent ? sourceX : tangent + bottomRadius * Math.Asin(cornerX / bottomRadius);
                var projectedDistance = (dy - sourceY) / scale;
                var projectedArc = dx >= 0 ? sourceArc : perimeter - sourceArc;
                var projectedAround = (projectedArc / perimeter + .75) % 1;
                var projectedAroundIndex = Math.Min(AroundSteps - 1, (int)(projectedAround * AroundSteps));
                var projectedDistanceIndex = Math.Clamp((int)((projectedDistance + InnerOverlapDips) * DistanceSteps /
                    (PaddingDips + InnerOverlapDips)), 0, DistanceSteps - 1);
                var bottomCornerEnd = tangent + Math.PI * bottomRadius / 4;
                var feather = Math.Max(scale, Math.PI * bottomRadius / 16);
                var coverage = Math.Clamp((bottomCornerEnd + feather / 2 - sourceArc) / feather, 0, 1);
                coverage = coverage * coverage * (3 - 2 * coverage);
                var lateral = Math.Max(0, Math.Abs(dx) - sourceEndX) / scale;
                coverage *= Gaussian(lateral / (1.8 + .45 * Math.Max(0, projectedDistance)));
                if (projectedDistance <= -InnerOverlapDips || projectedDistance >= PaddingDips - 1) coverage = 0;
                samples.Add(new Sample(y * Width + x, aroundIndex * DistanceSteps + distanceIndex,
                    projectedAroundIndex * DistanceSteps + projectedDistanceIndex, (float)coverage));
            }
        }
        _samples = samples.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public int PaddingPixels { get; }
    public int[] Pixels { get; }

    public bool Render(double phase, double brightness, IReadOnlyList<double>? bands = null, double simplification = 0)
    {
        brightness = double.IsFinite(brightness) ? Math.Clamp(brightness, 0, 1) : 0;
        if (brightness == 0)
        {
            if (_hasRenderedFrame && _lastBrightness == 0) return false;
            Array.Clear(Pixels);
            _lastBrightness = 0;
            _hasRenderedFrame = true;
            return true;
        }
        phase = double.IsFinite(phase) ? phase : 0;
        simplification = double.IsFinite(simplification) ? Math.Clamp(simplification, 0, 1) : 0;
        // Sub-byte opacity changes need no new bitmap/upload. This also lets
        // settled reduced-motion frames remain idle while their envelope runs.
        brightness = Math.Round(brightness * 1024) / 1024;
        simplification = Math.Round(simplification * 1024) / 1024;
        var sameBands = true;
        for (var i = 0; i < _frameBands.Length; i++)
        {
            var value = bands is not null && i < bands.Count && double.IsFinite(bands[i]) ? Math.Clamp(bands[i], 0, 1) : 0;
            sameBands &= _frameBands[i] == value;
            _frameBands[i] = value;
        }
        if (_hasRenderedFrame && sameBands && phase == _lastPhase && brightness == _lastBrightness && simplification == _lastSimplification)
            return false;
        _lastPhase = phase;
        _lastBrightness = brightness;
        _lastSimplification = simplification;
        _hasRenderedFrame = true;
        double Band(int i) => _frameBands[i % 6];
        var peak = 0d;
        for (var index = 0; index < 6; index++) peak = Math.Max(peak, Band(index));
        var colorTravel = phase * .095;
        var centers = (Orange: .08 + colorTravel + .035 * Math.Sin(phase * .6),
            Pink: .42 + colorTravel + .045 * Math.Sin(phase * .55 + 2),
            Blue: .76 + colorTravel + .04 * Math.Sin(phase * .5 + 4));
        var waveCenters = (Orange: .1 + phase * .07 + .1 * Math.Sin(phase * .35),
            Pink: .43 + phase * .08 + .085 * Math.Sin(phase * .41 + 2),
            Blue: .74 + phase * .055 + .09 * Math.Sin(phase * .3 + 4));
        var bloomCenters = (Orange: .04 + phase * .045 + .07 * Math.Sin(phase * .28 + 1),
            Pink: .38 + phase * .05 + .08 * Math.Sin(phase * .32 + 3),
            Blue: .72 + phase * .04 + .07 * Math.Sin(phase * .26 + 5));
        for (var around = 0; around < AroundSteps; around++)
        {
            var theta = around * (2 * Math.PI / AroundSteps);
            // Audio changes the thickness and diffusion of light around a fixed
            // edge. A broad wave sits inside that light, rather than becoming a
            // separate, displaced outline. Silence has no traveling thickness wave.
            var bandPosition = around * 6d / AroundSteps;
            var bandIndex = (int)bandPosition;
            var fraction = bandPosition - bandIndex;
            fraction = fraction * fraction * (3 - 2 * fraction);
            var pulse = Band(bandIndex) * (1 - fraction) + Band(bandIndex + 1) * fraction;
            var drive = Math.Clamp(.55 * pulse + .45 * peak, 0, 1);
            var motion = Math.Sqrt(drive);
            var wave = .7 * Math.Sin(theta * 2 - phase * 1.1) + .3 * Math.Sin(theta * 3 - phase * .7);
            var crest = .5 + .5 * wave;
            // The wave fills outward from the fixed edge. Its broad crests vary
            // in thickness; there is no displaced ribbon center or moving contour.
            var waveWidth = 1.25 + 10 * motion * crest;
            var bloomWidth = 3.2 + 7 * motion * crest;
            var localLight = .7 + .3 * motion;
            var waveStrength = Math.Min(.95, 1.65 * brightness * localLight *
                (.2 + .8 * motion) * (.65 + .35 * motion * crest));
            // Broad fields drift at separate speeds in the wave and diffusion.
            // Their overlapping colors accumulate softly outside the fixed core.
            var colorPosition = theta / (2 * Math.PI);
            var baseColor = Palette(colorPosition, centers);
            var waveColor = Palette(colorPosition, waveCenters);
            var bloomColor = Palette(colorPosition, bloomCenters);
            var haloColor = Palette(colorPosition - .08, bloomCenters);
            var coreColor = (R: baseColor.R * .88 + 255 * .12,
                G: baseColor.G * .88 + 255 * .12, B: baseColor.B * .88 + 255 * .12);
            for (var distance = 0; distance < DistanceSteps; distance++)
            {
                var profile = DistanceProfiles[distance];
                var d = profile.Distance;
                // Independent premultiplied layers, from the widest diffusion to
                // the attached base light. Gaussian widths are in DIPs at every DPI.
                double alpha = 0, red = 0, green = 0, blue = 0;
                Composite(ref alpha, ref red, ref green, ref blue, haloColor,
                    .16 * brightness * localLight * Gaussian(d / (8.5 + 1.5 * motion)));
                Composite(ref alpha, ref red, ref green, ref blue, bloomColor,
                    .32 * brightness * localLight * (.35 + .65 * motion * crest) * Gaussian(d / bloomWidth));
                Composite(ref alpha, ref red, ref green, ref blue, waveColor,
                    waveStrength * Gaussian(d / waveWidth));
                Composite(ref alpha, ref red, ref green, ref blue, baseColor,
                    .95 * brightness * profile.BaseLight);
                Composite(ref alpha, ref red, ref green, ref blue, coreColor,
                    .42 * brightness * profile.CoreLight);
                var coverage = profile.Coverage;
                alpha *= coverage;
                var a = (int)Math.Round(255 * alpha);
                // Premultiplication is essential: straight-alpha RGB leaves colored
                // residue after fading and dark rectangles on a layered HWND.
                var r = Math.Min(a, (int)Math.Round(red * coverage));
                var g = Math.Min(a, (int)Math.Round(green * coverage));
                var b = Math.Min(a, (int)Math.Round(blue * coverage));
                _lookup[around * DistanceSteps + distance] = a << 24 | r << 16 | g << 8 | b;
            }
        }
        foreach (var sample in _samples)
        {
            var pixel = _lookup[sample.Lookup];
            if (simplification == 0) { Pixels[sample.Pixel] = pixel; continue; }
            var projected = _lookup[sample.SimplifiedLookup];
            var coverage = simplification * sample.SimplifiedCoverage;
            // Scale every premultiplied channel together; transparent endpoints
            // must not leave RGB residue when the mode switches repeatedly.
            var a = (int)Math.Round(((uint)pixel >> 24) * (1 - simplification) + ((uint)projected >> 24) * coverage);
            var r = (int)Math.Round(((pixel >> 16) & 255) * (1 - simplification) + ((projected >> 16) & 255) * coverage);
            var g = (int)Math.Round(((pixel >> 8) & 255) * (1 - simplification) + ((projected >> 8) & 255) * coverage);
            var b = (int)Math.Round((pixel & 255) * (1 - simplification) + (projected & 255) * coverage);
            Pixels[sample.Pixel] = a << 24 | r << 16 | g << 8 | b;
        }
        return true;
    }

    private static double HalfContourLength(double halfWidth, double halfHeight, double radius) =>
        halfWidth + halfHeight + (Math.PI / 2 - 2) * radius;

    private static double Gaussian(double value)
    {
        value = Math.Abs(value);
        if (value >= 8) return 0; // Below byte precision, including the bounded tail.
        var position = value * GaussianStepsPerUnit;
        var index = (int)position;
        var blend = position - index;
        return GaussianSamples[index] + (GaussianSamples[index + 1] - GaussianSamples[index]) * blend;
    }

    private static double[] CreateGaussianSamples()
    {
        var samples = new double[8 * GaussianStepsPerUnit + 1];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = i / (double)GaussianStepsPerUnit;
            samples[i] = Math.Exp(-.5 * value * value);
        }
        return samples;
    }

    private static DistanceProfile[] CreateDistanceProfiles()
    {
        var profiles = new DistanceProfile[DistanceSteps];
        for (var i = 0; i < profiles.Length; i++)
        {
            var distance = (i + .5) * (PaddingDips + InnerOverlapDips) / DistanceSteps - InnerOverlapDips;
            var tail = Math.Clamp((PaddingDips - 1 - distance) / 6, 0, 1);
            var inner = Math.Clamp((distance + InnerOverlapDips) / 1.5, 0, 1);
            var coverage = tail * tail * (3 - 2 * tail) * inner * inner * (3 - 2 * inner);
            profiles[i] = new(distance, coverage, Gaussian(distance / 1.6), Gaussian(distance / .9));
        }
        return profiles;
    }

    private static void Composite(ref double alpha, ref double red, ref double green, ref double blue,
        (double R, double G, double B) color, double opacity)
    {
        var remaining = 1 - opacity;
        alpha = opacity + alpha * remaining;
        red = color.R * opacity + red * remaining;
        green = color.G * opacity + green * remaining;
        blue = color.B * opacity + blue * remaining;
    }

    private static (double R, double G, double B) Palette(double position, (double Orange, double Pink, double Blue) centers)
    {
        position -= Math.Floor(position);
        static double Weight(double position, double center, double width)
        {
            center -= Math.Floor(center);
            var distance = Math.Abs(position - center);
            return Gaussian(Math.Min(distance, 1 - distance) / width);
        }
        // Large overlapping color fields move independently along the perimeter.
        // Normalization keeps a continuous base underneath the soft thickness wave.
        var orange = Weight(position, centers.Orange, .14);
        var pink = Weight(position, centers.Pink, .16);
        var blue = Weight(position, centers.Blue, .17);
        var total = orange + pink + blue;
        return ((255 * orange + 255 * pink + 69 * blue) / total,
            (147 * orange + 79 * pink + 137 * blue) / total,
            (78 * orange + 171 * pink + 255 * blue) / total);
    }
}
