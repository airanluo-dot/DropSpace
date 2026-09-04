namespace DropSpace.Core.Transfer;

/// <summary>
/// Bounded memory and dimension policy for clipboard image decoding. A compressed payload
/// limit alone is insufficient because a highly-compressible image can expand dramatically.
/// </summary>
public readonly record struct ClipboardImageBudget(
    long MaxCompressedBytes,
    long MaxPixels,
    int MaxDimension,
    long MaxDecodedBytes,
    long MaxWorkingSetBytes,
    int CodecWorkingSetNumerator,
    int CodecWorkingSetDenominator)
{
    public bool IsValid =>
        MaxCompressedBytes > 0 &&
        MaxPixels > 0 &&
        MaxDimension > 0 &&
        MaxDecodedBytes > 0 &&
        MaxWorkingSetBytes >= MaxDecodedBytes &&
        CodecWorkingSetNumerator > 0 &&
        CodecWorkingSetDenominator > 0;

    public ClipboardImageBudgetAssessment Assess(long compressedBytes, long width, long height)
    {
        if (!IsValid || compressedBytes <= 0 || compressedBytes > MaxCompressedBytes ||
            width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
        {
            return ClipboardImageBudgetAssessment.Rejected(
                compressedBytes,
                width,
                height,
                "image-budget-limit");
        }

        long pixels;
        long decodedBytes;
        long estimatedWorkingSet;
        try
        {
            pixels = checked(width * height);
            decodedBytes = checked(pixels * BytesPerBgra8Pixel);
            var codecBytes = checked(decodedBytes * CodecWorkingSetNumerator / CodecWorkingSetDenominator);
            estimatedWorkingSet = checked(compressedBytes + decodedBytes + codecBytes);
        }
        catch (OverflowException)
        {
            return ClipboardImageBudgetAssessment.Rejected(
                compressedBytes,
                width,
                height,
                "image-budget-overflow");
        }

        if (pixels > MaxPixels || decodedBytes > MaxDecodedBytes || estimatedWorkingSet > MaxWorkingSetBytes)
        {
            return new ClipboardImageBudgetAssessment(
                false,
                compressedBytes,
                width,
                height,
                pixels,
                decodedBytes,
                estimatedWorkingSet,
                "image-budget-limit");
        }

        return new ClipboardImageBudgetAssessment(
            true,
            compressedBytes,
            width,
            height,
            pixels,
            decodedBytes,
            estimatedWorkingSet,
            null);
    }

    private const int BytesPerBgra8Pixel = 4;
}

public readonly record struct ClipboardImageBudgetAssessment(
    bool IsWithinBudget,
    long CompressedBytes,
    long Width,
    long Height,
    long Pixels,
    long DecodedBytes,
    long EstimatedWorkingSetBytes,
    string? ErrorCategory)
{
    public static ClipboardImageBudgetAssessment Rejected(
        long compressedBytes,
        long width,
        long height,
        string errorCategory) =>
        new(
            false,
            compressedBytes,
            width,
            height,
            0,
            0,
            0,
            errorCategory);
}

public static class ClipboardImageBudgetPolicy
{
    public const int DefaultMaxDimension = 32_768;
    public const long DefaultMaxDecodedBytes = 256L * 1024 * 1024;
    public const long DefaultMaxWorkingSetBytes = 512L * 1024 * 1024;
    public const int CodecWorkingSetNumerator = 1;
    public const int CodecWorkingSetDenominator = 2;

    public static ClipboardImageBudget Create(long maxCompressedBytes, long maxPixels) =>
        new(
            maxCompressedBytes,
            maxPixels,
            DefaultMaxDimension,
            DefaultMaxDecodedBytes,
            DefaultMaxWorkingSetBytes,
            CodecWorkingSetNumerator,
            CodecWorkingSetDenominator);
}
