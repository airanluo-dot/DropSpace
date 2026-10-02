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
    private const int AroundSteps = 256;
    private const int DistanceSteps = 160;
    private readonly record struct Sample(int Pixel, int Lookup);
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
                var around = (Math.Atan2(dy / halfHeight, dx / halfWidth) + Math.PI) / (2 * Math.PI);
                var aroundIndex = Math.Min(AroundSteps - 1, (int)(around * AroundSteps));
                var distanceIndex = Math.Clamp((int)((distance + InnerOverlapDips) * DistanceSteps / (PaddingDips + InnerOverlapDips)), 0, DistanceSteps - 1);
                samples.Add(new Sample(y * Width + x, aroundIndex * DistanceSteps + distanceIndex));
            }
        }
        _samples = samples.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public int PaddingPixels { get; }
    public int[] Pixels { get; }

    public void Render(double phase, double brightness, IReadOnlyList<double>? bands = null)
    {
        brightness = double.IsFinite(brightness) ? Math.Clamp(brightness, 0, 1) : 0;
        if (brightness == 0)
        {
            Array.Clear(Pixels);
            return;
        }
        phase = double.IsFinite(phase) ? phase : 0;
        for (var around = 0; around < AroundSteps; around++)
        {
            var theta = around * (2 * Math.PI / AroundSteps);
            // Three broad overlapping ribbons plus a soft edge wash, with different
            // travel speeds. Their widths breathe outside the fixed physical contour.
            // Interpolate the same six real FFT bands used by the meter around the contour.
            var bandPosition = around * 6d / AroundSteps;
            var bandIndex = (int)bandPosition;
            double Band(int i) => bands is not null && i % 6 < bands.Count && double.IsFinite(bands[i % 6])
                ? Math.Clamp(bands[i % 6], 0, 1) : 0;
            var pulse = Band(bandIndex) * (1 - (bandPosition - bandIndex)) + Band(bandIndex + 1) * (bandPosition - bandIndex);
            var broadCenter = 4 + 1.2 * Math.Sin(theta * 2 - phase * 0.8) + 2 * pulse;
            var ribbonCenter = 1 + Math.Sin(theta * 3 + phase * 1.1) + 1.5 * pulse;
            var outerCenter = 8 + 1.5 * Math.Sin(theta - phase * 0.7) + 2 * pulse;
            var broadWidth = 4 + 0.5 * Math.Cos(theta * 2 + phase * 0.5) + pulse;
            var broadColor = Palette(theta / (2 * Math.PI) - phase * 0.045);
            var ribbonColor = Palette(theta / (2 * Math.PI) + 0.08 + phase * 0.060);
            var outerColor = Palette(theta / (2 * Math.PI) + 0.24 - phase * 0.033);
            for (var distance = 0; distance < DistanceSteps; distance++)
            {
                var d = (distance + 0.5) * (PaddingDips + InnerOverlapDips) / DistanceSteps - InnerOverlapDips;
                var broad = 0.32 * Gaussian((d - broadCenter) / broadWidth);
                var ribbon = 0.38 * Gaussian((d - ribbonCenter) / 2.4);
                var outer = 0.18 * Gaussian((d - outerCenter) / 3.2);
                var edge = 0.18 * Gaussian(d / 2.5);
                var total = broad + ribbon + outer + edge;
                // A smooth bounded tail reaches zero before the bitmap boundary.
                var tail = Math.Clamp((PaddingDips - 1 - d) / 5, 0, 1);
                tail = tail * tail * (3 - 2 * tail);
                var inner = Math.Clamp((d + InnerOverlapDips) / 1.5, 0, 1);
                inner = inner * inner * (3 - 2 * inner);
                var alpha = Math.Clamp(total * brightness * tail * inner, 0, 1);
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
        foreach (var sample in _samples) Pixels[sample.Pixel] = _lookup[sample.Lookup];
    }

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
