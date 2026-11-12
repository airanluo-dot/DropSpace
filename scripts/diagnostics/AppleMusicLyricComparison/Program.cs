using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

if (args.Length != 3) throw new ArgumentException("Usage: ReadLyricFrames <frames-directory> <output.jsonl> <OCR-language>");
var frames = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var language = new Language(args[2]);
var engine = OcrEngine.TryCreateFromLanguage(language) ?? throw new InvalidOperationException(
    $"Windows OCR does not have {language.LanguageTag}. Installed: {string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag))}");
Console.WriteLine($"Using Windows OCR {engine.RecognizerLanguage.LanguageTag}.");

using var writer = new StreamWriter(output, false);
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var line in File.ReadLines(Path.Combine(frames, "pairs.jsonl")))
{
    using var item = JsonDocument.Parse(line);
    foreach (var app in new[] { "apple", "drop" })
    {
        if (!item.RootElement.TryGetProperty(app, out var picture) || picture.ValueKind != JsonValueKind.Object) continue;
        var name = picture.GetProperty("File").GetString();
        if (string.IsNullOrEmpty(name) || !name.Equals(Path.GetFileName(name), StringComparison.Ordinal) ||
            !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
        var path = Path.Combine(frames, name);
        if (!File.Exists(path)) continue;
        using var image = new Bitmap(path);
        if (image.Width > OcrEngine.MaxImageDimension || image.Height > OcrEngine.MaxImageDimension) continue;
        var pixels = new LyricPixels(image);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.Normalized.AsBuffer(), BitmapPixelFormat.Gray8, image.Width, image.Height);
        var result = await engine.RecognizeAsync(bitmap);
        var lines = result.Lines.Select(row => new { words = row.Words.Select(word => pixels.Measure(word, app == "apple")) });
        writer.WriteLine(JsonSerializer.Serialize(new { file = name, app, h = image.Height, lines }));
    }
}

internal sealed class LyricPixels
{
    private readonly int _width, _height;
    private readonly byte[] _brightness;
    private readonly int[] _background;
    internal byte[] Normalized { get; }

    internal LyricPixels(Bitmap image)
    {
        _width = image.Width;
        _height = image.Height;
        _brightness = new byte[_width * _height];
        _background = new int[_height];
        Normalized = new byte[_brightness.Length];
        var data = image.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new byte[_width * 4];
            var row = new byte[_width];
            for (int y = 0; y < _height; ++y)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, raw, 0, raw.Length);
                int offset = y * _width;
                for (int x = 0; x < _width; ++x)
                {
                    int p = 4 * x;
                    row[x] = _brightness[offset + x] = (byte)((54 * raw[p + 2] + 183 * raw[p + 1] + 19 * raw[p]) / 256);
                }
                Array.Sort(row);
                bool dark = row[_width / 2] < 128;
                _background[y] = dark ? row[_width / 5] : row[Math.Min(_width - 1, _width * 4 / 5)];
                for (int x = 0; x < _width; ++x)
                {
                    int contrast = dark ? _brightness[offset + x] - _background[y] : _background[y] - _brightness[offset + x];
                    Normalized[offset + x] = (byte)(255 - Math.Clamp((contrast - 3) * 18, 0, 255));
                }
            }
        }
        finally { image.UnlockBits(data); }
    }

    internal object Measure(OcrWord word, bool apple)
    {
        var box = word.BoundingRect;
        int left = Math.Clamp((int)box.Left, 0, _width), right = Math.Clamp((int)Math.Ceiling(box.Right), left, _width);
        int top = Math.Clamp((int)box.Top, 0, _height), bottom = Math.Clamp((int)Math.Ceiling(box.Bottom), top, _height);
        var profile = new char[right - left];
        for (int x = left; x < right; ++x)
        {
            int ink = 0, bright = 0;
            for (int y = top; y < bottom; ++y)
            {
                int b = _brightness[y * _width + x], background = _background[y];
                bool dark = background < 128;
                if ((dark ? b - background : background - b) < 14) continue;
                ink++;
                if ((dark ? b : 255 - b) >= (apple ? 155 : 190)) bright++;
            }
            profile[x - left] = ink < 2 ? '0' : bright >= 2 ? '2' : '1';
        }
        return new { t = word.Text, y = top, h = bottom - top, profile = new string(profile) };
    }
}
