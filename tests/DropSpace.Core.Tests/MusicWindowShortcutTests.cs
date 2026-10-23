using System.Text.Json;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class MusicWindowShortcutTests
{
    [TestMethod]
    public void ExistingSettingsKeepAnUnboundMusicActionAndExistingQuickPanel()
    {
        var settings=JsonSerializer.Deserialize<AppSettings>("{\"Version\":15,\"QuickPanelHotkey\":\"Ctrl+Alt+F11\"}")!.Validate();
        Assert.AreEqual(string.Empty,settings.MusicWindowHotkey);
        Assert.AreEqual("Ctrl+Alt+F11",settings.QuickPanelHotkey);
    }

    [TestMethod]
    public void MusicGestureUsesTheExistingCanonicalKeyPolicy()
    {
        var settings=(new AppSettings { MusicWindowHotkey=" alt + ctrl + m " }).Validate();
        Assert.AreEqual("Ctrl+Alt+M",settings.MusicWindowHotkey);
        Assert.AreEqual("Win+Shift+Space",settings.QuickPanelHotkey);
        Assert.AreEqual(string.Empty,(settings with { MusicWindowHotkey="  " }).Validate().MusicWindowHotkey);
    }

    [TestMethod]
    [DataRow("Win+Shift+Space")]
    [DataRow("Ctrl+1")]
    [DataRow("Ctrl+Ctrl+M")]
    [DataRow("M")]
    public void ConflictingOrInvalidMusicBindingsAreRejected(string gesture)
    {
        Assert.ThrowsExactly<ArgumentException>(()=>(new AppSettings { MusicWindowHotkey=gesture }).Validate());
    }

    [TestMethod]
    public void MainShortcutCannotOverrideTheBoundMusicAction()
    {
        var settings=new AppSettings
        {
            MusicWindowHotkey="Ctrl+Alt+M",
            KeyboardShortcuts=new() { ["Search"]="Alt+Ctrl+M" },
        };
        Assert.ThrowsExactly<ArgumentException>(()=>KeyboardShortcutPolicy.Validate(settings,includeGlobal:true));
    }

    [TestMethod]
    public void ClearingMusicBindingAndConcurrentEditsMergeIndependently()
    {
        var baseline=new AppSettings { MusicWindowHotkey="Ctrl+Alt+M" };
        var latest=baseline with { Theme=ThemePreference.Dark };
        var cleared=SettingsChangePolicy.Merge(baseline,baseline with { MusicWindowHotkey=string.Empty },latest).Validate();
        Assert.AreEqual(string.Empty,cleared.MusicWindowHotkey);
        Assert.AreEqual(ThemePreference.Dark,cleared.Theme);
        Assert.AreEqual(baseline.QuickPanelHotkey,cleared.QuickPanelHotkey);
        var retained=SettingsChangePolicy.Merge(baseline,baseline with { RetentionDays=14 },baseline with { MusicWindowHotkey="Ctrl+Alt+N" }).Validate();
        Assert.AreEqual("Ctrl+Alt+N",retained.MusicWindowHotkey);
        Assert.AreEqual(14,retained.RetentionDays);
    }
}
