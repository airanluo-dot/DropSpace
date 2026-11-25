using Windows.UI;

namespace DropSpace.App.Services.Media;

internal static class ArtworkSpectrumColor
{
    // Group accent pixels by hue so variations in shade vote for the same color.
    // An achromatic cover (or a few colored compression artifacts) uses its dominant gray.
    internal static Color? Select(ReadOnlySpan<byte> bgra)
    {
        var hues = new Bucket[18];
        var neutrals = new Bucket[8];
        var coverage = 0;
        var accentCoverage = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var b = bgra[i];
            var g = bgra[i + 1];
            var r = bgra[i + 2];
            var alpha = bgra[i + 3];
            var maximum = Math.Max(r, Math.Max(g, b));
            var chroma = maximum - Math.Min(r, Math.Min(g, b));
            coverage += alpha;
            neutrals[(r + 2 * g + b) >> 7].Add(r, g, b, alpha);
            if (chroma < 24 || chroma * 5 < maximum) continue;

            var hue = maximum == r ? (g - b) / (double)chroma :
                maximum == g ? 2 + (b - r) / (double)chroma : 4 + (r - g) / (double)chroma;
            var band = ((int)Math.Floor(hue * 3 + .5) + hues.Length) % hues.Length;
            hues[band].Add(r, g, b, (alpha * chroma + 254) / 255);
            accentCoverage += alpha;
        }
        if (coverage == 0) return null;
        var palette = accentCoverage * 100 >= coverage * 3 ? hues : neutrals;
        var dominant = palette.MaxBy(bin => bin.Weight);
        return dominant.Average;
    }

    private struct Bucket
    {
        public int Weight;
        private int _red, _green, _blue;
        public void Add(byte r, byte g, byte b, int weight)
        {
            Weight += weight;
            _red += r * weight;
            _green += g * weight;
            _blue += b * weight;
        }
        public readonly Color Average => Color.FromArgb(255, (byte)(_red / Weight),
            (byte)(_green / Weight), (byte)(_blue / Weight));
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
        // Scale the original hue first. Add white only when a channel reaches its
        // limit before the color is bright enough (notably deep blue on dark glass).
        var peak = Math.Max(r, Math.Max(g, b));
        var gain = luminance == 0 ? 1 : Math.Min(target / luminance, 1 / peak);
        var raised = luminance * gain;
        var white = dark && raised < target ? (target - raised) / (1 - raised) : 0;
        byte Channel(double value)
        {
            value *= gain;
            value += (1 - value) * white;
            var s = value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
            return (byte)Math.Round(255 * s);
        }
        return Color.FromArgb(255, Channel(r), Channel(g), Channel(b));
    }
}
