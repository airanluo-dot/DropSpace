using System.Text.Json;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LiquidGlassSettingsTests
{
    [TestMethod]
    public void UpgradeUsesRequestedMaterialDefaultsWithoutChangingIslandLayout()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"IslandAppearance":{"Theme":2,"CompactScale":1.4,"ExpandedScale":0.8,"Resident":true},"ClipboardPaused":true}
            """)!.Validate();
        var glass = settings.IslandAppearance;
        Assert.AreEqual(IslandMaterialStyle.LiquidGlass, glass.MaterialStyle);
        Assert.AreEqual(1d, glass.LiquidGlassBlur); Assert.AreEqual(400d, glass.LiquidGlassRefraction);
        Assert.AreEqual(75d, glass.LiquidGlassDispersion); Assert.AreEqual(0d, glass.LiquidGlassDarkOverlay);
        Assert.IsTrue(glass.LiquidGlassBlackGradient); Assert.IsTrue(glass.Resident);
        Assert.AreEqual(1.4, glass.CompactScale); Assert.AreEqual(0.8, glass.ExpandedScale);
        Assert.AreEqual(ThemePreference.Dark, glass.Theme); Assert.IsTrue(settings.ClipboardPaused);
    }

    [TestMethod]
    public void InvalidOpticsRecoverAndIndependentEditsSurvivePersistence()
    {
        var baseline = new AppSettings();
        var latest = baseline with { IslandAppearance = baseline.IslandAppearance with
            { LiquidGlassBlur = 9, LiquidGlassDispersion = 20, ExpandedScale = 1.3 } };
        var edit = baseline with { IslandAppearance = baseline.IslandAppearance with
            { MaterialStyle = IslandMaterialStyle.Acrylic, LiquidGlassBlackGradient = false, LiquidGlassRefraction = 120, LiquidGlassDarkOverlay = 140 } };
        var merged = SettingsChangePolicy.Merge(baseline, edit, latest).Validate();
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(merged))!.Validate().IslandAppearance;
        Assert.AreEqual(IslandMaterialStyle.Acrylic, saved.MaterialStyle); Assert.IsFalse(saved.LiquidGlassBlackGradient);
        Assert.AreEqual(9d, saved.LiquidGlassBlur); Assert.AreEqual(20d, saved.LiquidGlassDispersion);
        Assert.AreEqual(120d, saved.LiquidGlassRefraction); Assert.AreEqual(140d, saved.LiquidGlassDarkOverlay);
        Assert.AreEqual(1.3, saved.ExpandedScale);
        var corrupt = (baseline with { IslandAppearance = saved with { MaterialStyle = (IslandMaterialStyle)99,
            LiquidGlassBlur = double.NaN, LiquidGlassRefraction = double.PositiveInfinity,
            LiquidGlassDispersion = -10, LiquidGlassDarkOverlay = 900 } }).Validate().IslandAppearance;
        Assert.AreEqual(IslandMaterialStyle.LiquidGlass, corrupt.MaterialStyle);
        Assert.AreEqual(1d, corrupt.LiquidGlassBlur); Assert.AreEqual(400d, corrupt.LiquidGlassRefraction);
        Assert.AreEqual(0d, corrupt.LiquidGlassDispersion); Assert.AreEqual(200d, corrupt.LiquidGlassDarkOverlay);
        Assert.AreEqual(1.3, corrupt.ExpandedScale);
    }
}
