using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class FullscreenWindowClassifierTests
{
    [DataTestMethod]
    [DataRow("Progman")]
    [DataRow("WorkerW")]
    [DataRow("Shell_TrayWnd")]
    [DataRow("Shell_SecondaryTrayWnd")]
    public void DesktopAndShellClassesNeverSuppressOverlay(string className)
    {
        var facts = FullscreenFacts() with { ClassName = className };

        Assert.IsFalse(FullscreenWindowClassifier.IsFullscreenApplication(facts));
    }

    [TestMethod]
    public void ShellIdentityNeverSuppressesOverlayEvenWithUnknownClass()
    {
        var facts = FullscreenFacts() with
        {
            IsDesktopOrShellWindow = true,
            ClassName = "FutureShellSurface",
        };

        Assert.IsFalse(FullscreenWindowClassifier.IsFullscreenApplication(facts));
    }

    [TestMethod]
    public void VisibleUncloakedUserWindowCoveringMonitorSuppressesOverlay()
    {
        Assert.IsTrue(FullscreenWindowClassifier.IsFullscreenApplication(FullscreenFacts()));
    }

    [TestMethod]
    public void HiddenCloakedIconicChildAndToolWindowsDoNotSuppressOverlay()
    {
        Assert.IsFalse(FullscreenWindowClassifier.IsFullscreenApplication(FullscreenFacts() with { IsVisible = false }));
        Assert.IsFalse(FullscreenWindowClassifier.IsFullscreenApplication(FullscreenFacts() with { IsCloaked = true }));
        Assert.IsFalse(FullscreenWindowClassifier.IsFullscreenApplication(FullscreenFacts() with { IsIconic = true }));
        Assert.IsFalse(FullscreenWindowClassifier.IsFullscreenApplication(FullscreenFacts() with { Style = 0x40000000L }));
        Assert.IsFalse(FullscreenWindowClassifier.IsFullscreenApplication(FullscreenFacts() with { ExtendedStyle = 0x80L }));
    }

    private static ForegroundWindowFacts FullscreenFacts() => new(
        IsVisible: true,
        IsCloaked: false,
        IsIconic: false,
        IsDesktopOrShellWindow: false,
        IsOnTargetMonitor: true,
        CoversTargetMonitor: true,
        Style: 0,
        ExtendedStyle: 0,
        ClassName: "ApplicationFrameWindow");
}
