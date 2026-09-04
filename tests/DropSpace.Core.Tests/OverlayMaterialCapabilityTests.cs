using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayMaterialCapabilityTests
{
    [TestMethod]
    public void HighContrastAlwaysWinsOverOptionalMaterials()
    {
        var preferences = new OverlayVisualPreferences(
            OverlayVisualPreferenceMode.Full,
            AdvancedEffectsEnabled: true,
            HighContrast: true,
            IsWindows11OrLater: true,
            DesktopAcrylicSupported: true,
            CompositionEffectsSupported: true,
            CompositionEffectsFast: true,
            TransparencyEnabled: true);

        Assert.AreEqual(OverlayMaterialTier.HighContrastSystemSurface, preferences.MaterialTier);
        Assert.IsFalse(preferences.CanUseDesktopAcrylic);
    }

    [TestMethod]
    public void WindowsTenUsesSolidFallback()
    {
        var preferences = new OverlayVisualPreferences(
            OverlayVisualPreferenceMode.Full,
            AdvancedEffectsEnabled: true,
            HighContrast: false,
            IsWindows11OrLater: false);

        Assert.AreEqual(OverlayMaterialTier.Windows10Solid, preferences.MaterialTier);
        Assert.IsFalse(preferences.CanUseDesktopAcrylic);
    }

    [TestMethod]
    public void WindowsElevenRequiresRuntimeAcrylicAndCompositionSupport()
    {
        var noAcrylic = new OverlayVisualPreferences(
            OverlayVisualPreferenceMode.Full,
            true,
            false,
            true,
            DesktopAcrylicSupported: false,
            CompositionEffectsSupported: true);
        var slowAcrylic = noAcrylic with
        {
            DesktopAcrylicSupported = true,
            CompositionEffectsFast = false,
        };
        var fastAcrylic = slowAcrylic with { CompositionEffectsFast = true };

        Assert.AreEqual(OverlayMaterialTier.Windows11Solid, noAcrylic.MaterialTier);
        Assert.AreEqual(OverlayMaterialTier.DesktopAcrylic, slowAcrylic.MaterialTier);
        Assert.AreEqual(OverlayMaterialTier.DesktopAcrylicFast, fastAcrylic.MaterialTier);
    }

    [TestMethod]
    public void DisabledTransparencyAndRemoteSessionUseSolidSurface()
    {
        var disabled = new OverlayVisualPreferences(
            OverlayVisualPreferenceMode.Full,
            AdvancedEffectsEnabled: false,
            HighContrast: false,
            IsWindows11OrLater: true,
            DesktopAcrylicSupported: true,
            CompositionEffectsSupported: true,
            CompositionEffectsFast: true,
            TransparencyEnabled: false);
        var remote = disabled with
        {
            AdvancedEffectsEnabled = true,
            TransparencyEnabled = true,
            IsRemoteSession = true,
        };

        Assert.AreEqual(OverlayMaterialTier.Windows11Solid, disabled.MaterialTier);
        Assert.AreEqual(OverlayMaterialTier.Windows11Solid, remote.MaterialTier);
    }
}
