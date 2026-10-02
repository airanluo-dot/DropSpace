using DropSpace.App.Services.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MusicVisualSmokeContractTests
{
    private static string FreshRoot() => Path.Combine(Path.GetTempPath(), MusicVisualSmokeOptions.RootPrefix + Guid.NewGuid().ToString("N"));
    private static readonly string[] Arguments = [MusicVisualSmokeOptions.Switch, "--test-mode", "--smoke-language", "en-US"];

    [TestMethod]
    public void AcceptsOnlyFreshDirectTemporaryRoot()
    {
        var root = FreshRoot();
        var options = MusicVisualSmokeOptions.Parse(Arguments, root, Path.GetTempPath());
        Assert.AreEqual(root, options.Root);
        Assert.AreEqual("en-US", options.Language);
        Assert.IsFalse(Directory.Exists(root), "Input validation must not create data.");
    }

    [TestMethod]
    public void RejectsMissingTestModeAndMissingDiagnosticSwitch()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MusicVisualSmokeOptions.Parse([MusicVisualSmokeOptions.Switch], FreshRoot(), Path.GetTempPath()));
        Assert.ThrowsExactly<ArgumentException>(() => MusicVisualSmokeOptions.Parse(["--test-mode"], FreshRoot(), Path.GetTempPath()));
    }

    [TestMethod]
    public void RejectsNormalDataRootNestedRootAndMissingRoot()
    {
        foreach (var root in new[] { null, "", "relative-path", Path.Combine(Path.GetTempPath(), "DropSpace"), Path.Combine(FreshRoot(), "nested") })
            Assert.ThrowsExactly<ArgumentException>(() => MusicVisualSmokeOptions.Parse(Arguments, root, Path.GetTempPath()));
    }

    [TestMethod]
    public void RejectsExistingDataWithoutChangingIt()
    {
        var root = FreshRoot();
        Directory.CreateDirectory(root);
        var sentinel = Path.Combine(root, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        try
        {
            Assert.ThrowsExactly<IOException>(() => MusicVisualSmokeOptions.Parse(Arguments, root, Path.GetTempPath()));
            Assert.AreEqual("keep", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void RejectsUnsupportedOrMissingLanguage()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MusicVisualSmokeOptions.Parse([MusicVisualSmokeOptions.Switch, "--test-mode", "--smoke-language", "invalid"], FreshRoot(), Path.GetTempPath()));
        Assert.ThrowsExactly<ArgumentException>(() => MusicVisualSmokeOptions.Parse([MusicVisualSmokeOptions.Switch, "--test-mode", "--smoke-language"], FreshRoot(), Path.GetTempPath()));
    }

    [TestMethod]
    public void BlankTransparentBlackAndUniformReadbacksCannotPass()
    {
        Assert.IsFalse(MusicVisualSmokeOptions.HasPixelContent([]));
        Assert.IsFalse(MusicVisualSmokeOptions.HasPixelContent([0, 0, 0]));
        Assert.IsFalse(MusicVisualSmokeOptions.HasPixelContent(new byte[256]));
        Assert.IsFalse(MusicVisualSmokeOptions.HasPixelContent(Enumerable.Repeat((byte)255, 256).ToArray()));
        var black = new byte[256];
        for (var index = 3; index < black.Length; index += 4) black[index] = 255;
        Assert.IsFalse(MusicVisualSmokeOptions.HasPixelContent(black));
    }

    [TestMethod]
    public void NonuniformOpaqueReadbackPassesOnlyThePixelPresenceCheck()
    {
        var pixels = new byte[256];
        for (var index = 0; index < pixels.Length; index += 4) { pixels[index] = (byte)index; pixels[index + 3] = 255; }
        Assert.IsTrue(MusicVisualSmokeOptions.HasPixelContent(pixels));
    }
}
