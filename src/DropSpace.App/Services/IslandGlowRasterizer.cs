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
    private readonly record struct Sample(int Pixel, int Lookup, float SimplifiedCoverage);
    private readonly Sample[] _samples;
    private readonly int[] _lookup = new int[AroundSteps * DistanceSteps];

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
                var fromBottom = Math.Min(position, 1 - position);
                // Approximately the lower quarter of the perimeter, feathered
                // through the last third of each end instead of a radial cut.
                var coverage = Math.Clamp((.125 - fromBottom) / .04, 0, 1);
                coverage = coverage * coverage * (3 - 2 * coverage);
                samples.Add(new Sample(y * Width + x, aroundIndex * DistanceSteps + distanceIndex, (float)coverage));
            }
        }
        _samples = samples.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public int PaddingPixels { get; }
    public int[] Pixels { get; }

    public void Render(double phase, double brightness, IReadOnlyList<double>? bands = null, double simplification = 0)
    {
        brightness = double.IsFinite(brightness) ? Math.Clamp(brightness, 0, 1) : 0;
        if (brightness == 0)
        {
            Array.Clear(Pixels);
            return;
        }
        phase = double.IsFinite(phase) ? phase : 0;
        simplification = double.IsFinite(simplification) ? Math.Clamp(simplification, 0, 1) : 0;
        double Band(int i) => bands is not null && i % 6 < bands.Count && double.IsFinite(bands[i % 6])
            ? Math.Clamp(bands[i % 6], 0, 1) : 0;
        var peak = 0d;
        for (var index = 0; index < 6; index++) peak = Math.Max(peak, Band(index));
        for (var around = 0; around < AroundSteps; around++)
        {
            var theta = around * (2 * Math.PI / AroundSteps);
            // Local audio controls both the visible crest displacement and light.
            // Traveling phase distributes that real energy; silence cannot produce
            // animated waves. The island itself never changes its physical contour.
            var bandPosition = around * 6d / AroundSteps;
            var bandIndex = (int)bandPosition;
            var fraction = bandPosition - bandIndex;
            fraction = fraction * fraction * (3 - 2 * fraction);
            var pulse = Band(bandIndex) * (1 - fraction) + Band(bandIndex + 1) * fraction;
            var drive = Math.Clamp(.85 * pulse + .15 * peak, 0, 1);
            var wave = .65 * Math.Sin(theta * 5 - phase * 5.2) + .35 * Math.Sin(theta * 8 + phase * 3.4);
            // Perceptual gain preserves visible contour motion at ordinary listening
            // levels without raising the global brightness or widening the halo.
            var motion = Math.Sqrt(drive);
            var ribbonCenter = 3 + 2.2 * drive + 6 * motion * wave;
            var broadCenter = Math.Max(0, ribbonCenter * .35);
            var outerCenter = ribbonCenter + 3;
            var broadWidth = 3 + .4 * drive + .32 * Math.Max(0, ribbonCenter);
            var ribbonWidth = 1.6 + .7 * drive;
            var localLight = .28 + .72 * Math.Sqrt(drive);
            var broadColor = Palette(theta / (2 * Math.PI) - phase * 0.065);
            var ribbonColor = Palette(theta / (2 * Math.PI) + 0.08 + phase * 0.085);
            var outerColor = Palette(theta / (2 * Math.PI) + 0.24 - phase * 0.047);
            for (var distance = 0; distance < DistanceSteps; distance++)
            {
                var d = (distance + 0.5) * (PaddingDips + InnerOverlapDips) / DistanceSteps - InnerOverlapDips;
                var broad = 0.20 * Gaussian((d - broadCenter) / broadWidth);
                var ribbon = 0.52 * Gaussian((d - ribbonCenter) / ribbonWidth);
                var outer = 0.08 * Gaussian((d - outerCenter) / 3.2);
                var edge = 0.10 * Gaussian(d / 2.5);
                var total = broad + ribbon + outer + edge;
                // A smooth bounded tail reaches zero before the bitmap boundary.
                var tail = Math.Clamp((PaddingDips - 1 - d) / 5, 0, 1);
                tail = tail * tail * (3 - 2 * tail);
                var inner = Math.Clamp((d + InnerOverlapDips) / 1.5, 0, 1);
                inner = inner * inner * (3 - 2 * inner);
                var alpha = Math.Clamp(total * brightness * localLight * tail * inner, 0, 1);
                var a = (int)Math.Round(255 * alpha);
                var red = (broadColor.R * (broad + edge) + ribbonColor.R * ribbon + outerColor.R * outer) / total;
                var green = (broadColor.G * (broad + edge) + ribbonColor.G * ribbon + outerColor.G * outer) / total;
                var blue = (broadColor.B * (broad + edge) + ribbonColor.B * ribbon + outerColor.B * outer) / total;
                // Premultiplication is essential: straight-alpha RGB leaves colored
                // residue after fading and dark rectangles on a layered HWND.
                var r = Math.Min(a, (int)Math.Round(red * alpha));
                var g = Math.Min(a, (int)Math.Round(green * alpha));
                var b = Math.Min(a, (int)Math.Round(blue * alpha));
                _lookup[around * DistanceSteps + distance] = a << 24 | r << 16 | g << 8 | b;
            }
        }
        foreach (var sample in _samples)
        {
            var pixel = _lookup[sample.Lookup];
            if (simplification == 0 || sample.SimplifiedCoverage == 1) { Pixels[sample.Pixel] = pixel; continue; }
            var coverage = 1 - simplification + simplification * sample.SimplifiedCoverage;
            // Scale every premultiplied channel together; transparent endpoints
            // must not leave RGB residue when the mode switches repeatedly.
            var a = (int)Math.Round(((uint)pixel >> 24) * coverage);
            var r = (int)Math.Round(((pixel >> 16) & 255) * coverage);
            var g = (int)Math.Round(((pixel >> 8) & 255) * coverage);
            var b = (int)Math.Round((pixel & 255) * coverage);
            Pixels[sample.Pixel] = a << 24 | r << 16 | g << 8 | b;
        }
    }

    private static double HalfContourLength(double halfWidth, double halfHeight, double radius) =>
        halfWidth + halfHeight + (Math.PI / 2 - 2) * radius;

    private static double Gaussian(double value) => Math.Exp(-0.5 * value * value);

    private static (double R, double G, double B) Palette(double position)
    {
        position = (position - Math.Floor(position)) * 3;
        var segment = (int)position;
        var blend = position - segment;
        blend = blend * blend * (3 - 2 * blend);
        // The product-requested orange / pink / blue identity is decorative only.
        var from = segment switch { 0 => (255d, 147d, 78d), 1 => (255d, 79d, 171d), _ => (69d, 137d, 255d) };
        var to = segment switch { 0 => (255d, 79d, 171d), 1 => (69d, 137d, 255d), _ => (255d, 147d, 78d) };
        return (from.Item1 + (to.Item1 - from.Item1) * blend,
            from.Item2 + (to.Item2 - from.Item2) * blend,
            from.Item3 + (to.Item3 - from.Item3) * blend);
    }
}
