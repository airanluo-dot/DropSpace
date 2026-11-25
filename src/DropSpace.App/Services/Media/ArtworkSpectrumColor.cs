using Windows.UI;

namespace DropSpace.App.Services.Media;

internal static class ArtworkSpectrumColor
{
    // A 3-bit-per-channel histogram groups similar thumbnail pixels. Count opaque,
    // colorful pixels more heavily so a small highlight cannot outweigh a broad color field.
    internal static Color? Select(ReadOnlySpan<byte> bgra)
    {
        var bins = new (int Weight, int R, int G, int B)[512];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var b = bgra[i];
            var g = bgra[i + 1];
            var r = bgra[i + 2];
            var alpha = bgra[i + 3];
            var chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            var weight = alpha * (32 + chroma) / 255;
            ref var bin = ref bins[((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5)];
            bin.Weight += weight;
            bin.R += r * weight;
            bin.G += g * weight;
            bin.B += b * weight;
        }
        var dominant = bins.MaxBy(bin => bin.Weight);
        return dominant.Weight == 0 ? null : Color.FromArgb(255, (byte)(dominant.R / dominant.Weight),
            (byte)(dominant.G / dominant.Weight), (byte)(dominant.B / dominant.Weight));
    }

    internal static Color ForTheme(Color color, bool dark)
    {
        static double Linear(byte channel)
        {
            var s = channel / 255d;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        var r = Linear(color.R);
        var g = Linear(color.G);
        var b = Linear(color.B);
        var luminance = .2126 * r + .7152 * g + .0722 * b;
        // Bring only hard-to-see colors toward the foreground, in linear light.
        var target = dark ? Math.Max(.20, luminance) : Math.Min(.22, luminance);
        if (target == luminance) return color;
        var foreground = dark ? 1d : 0d;
        var mix = (target - luminance) / (foreground - luminance);
        byte Channel(double value)
        {
            value += (foreground - value) * mix;
            var s = value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
            return (byte)Math.Round(255 * s);
        }
        return Color.FromArgb(255, Channel(r), Channel(g), Channel(b));
    }
}
