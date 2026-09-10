using DropSpace.Core.Actions;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class SettingsAndImageActionPolicyTests
{
    [TestMethod]
    public void StaleSettingsFormMergesOnlyFieldsItChanged()
    {
        var baseline = new AppSettings { Theme = ThemePreference.System, RetentionDays = 30 };
        var requested = baseline with { Theme = ThemePreference.Dark };
        var latest = baseline with { RetentionDays = 14, ClipboardPaused = true };

        var merged = SettingsChangePolicy.Merge(baseline, requested, latest);

        Assert.AreEqual(ThemePreference.Dark, merged.Theme);
        Assert.AreEqual(14, merged.RetentionDays);
        Assert.IsTrue(merged.ClipboardPaused);
    }

    [TestMethod]
    [DataRow(100, 1920, 1080, 1920, 1080)]
    [DataRow(75, 1920, 1080, 1440, 810)]
    [DataRow(50, 1920, 1080, 960, 540)]
    [DataRow(25, 1920, 1080, 480, 270)]
    public void ImagePresetsScaleWithinBounds(int percent, int width, int height, int expectedWidth, int expectedHeight)
    {
        var result = ImageSizePresetPolicy.Scale(width, height, percent);
        Assert.AreEqual(expectedWidth, result.Width);
        Assert.AreEqual(expectedHeight, result.Height);
    }

    [TestMethod]
    public void ImagePresetsRejectOversizedSourcesAndKeepTinyImagesValid()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ImageSizePresetPolicy.Scale(16_384, 16_384, 100));
        Assert.AreEqual((1, 1), ImageSizePresetPolicy.Scale(1, 1, 25));
    }
}
