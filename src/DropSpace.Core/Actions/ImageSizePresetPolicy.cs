namespace DropSpace.Core.Actions;

public static class ImageSizePresetPolicy
{
    public const int MaximumDimension = 16_384;
    public const long MaximumPixels = 64L * 1024 * 1024;

    public static (int Width, int Height) Scale(int width, int height, int percent)
    {
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension ||
            (long)width * height > MaximumPixels || percent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(width));
        return (Math.Max(1, (int)Math.Round(width * percent / 100d)),
            Math.Max(1, (int)Math.Round(height * percent / 100d)));
    }
}
