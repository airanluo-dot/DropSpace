using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class SettingsValidationPolicyTests
{
    [TestMethod]
    public void HotkeysAreCanonicalizedIntoStableModifierOrder()
    {
        var settings = new AppSettings { QuickPanelHotkey = " shift + win + space " };

        var validated = settings.Validate();

        Assert.AreEqual("Win+Shift+Space", validated.QuickPanelHotkey);
        Assert.AreEqual("Ctrl+Alt+1", SettingsValidationPolicy.CanonicalizeHotkey("alt+ctrl+1"));
    }

    [TestMethod]
    public void DuplicateModifiersAndMultipleKeysAreRejected()
    {
        Assert.IsNull(SettingsValidationPolicy.CanonicalizeHotkey("Win+Win+Space"));
        Assert.IsNull(SettingsValidationPolicy.CanonicalizeHotkey("Win+Shift+A+B"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AppSettings { QuickPanelHotkey = "Win+Win+Space" }.Validate());
    }

    [TestMethod]
    public void SettingsValidationUsesSharedNumericBounds()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AppSettings { MaxImagePixels = SettingsValidationPolicy.MaximumMaxImagePixels + 1 }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AppSettings { RetentionDays = SettingsValidationPolicy.MinimumRetentionDays - 1 }.Validate());
    }
}
