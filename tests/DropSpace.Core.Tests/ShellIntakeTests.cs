using DropSpace.Core.Shell;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class ShellIntakeTests
{
    [TestMethod]
    public void ParserAcceptsStaticExplorerCommandWithUnicodeAndSpaces()
    {
        var result = ShellIntakeCommandLineParser.Parse(
        [
            "DropSpace.exe",
            "--shell-add",
            "--source",
            "explorer-context-menu",
            "C:\\Users\\爱然\\My Documents\\one.txt",
            "D:\\另一个文件.txt",
        ]);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ShellIntakeSource.ExplorerContextMenu, result.Request!.Source);
        CollectionAssert.AreEqual(
            new[] { "C:\\Users\\爱然\\My Documents\\one.txt", "D:\\另一个文件.txt" },
            result.Request.Paths.ToArray());
    }

    [TestMethod]
    public void ParserAcceptsDelimiterAndDeduplicatesPathsCaseInsensitively()
    {
        var result = ShellIntakeCommandLineParser.Parse(
        [
            "DropSpace.exe",
            "--shell-add",
            "--source",
            "sendto",
            "--",
            "C:\\Temp\\File.txt",
            "c:\\temp\\file.txt",
            "--looks-like-an-option.txt",
        ]);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ShellIntakeSource.SendTo, result.Request!.Source);
        CollectionAssert.AreEqual(
            new[] { "C:\\Temp\\File.txt", "--looks-like-an-option.txt" },
            result.Request.Paths.ToArray());
    }

    [TestMethod]
    [DataRow("missing-source")]
    [DataRow("invalid-source")]
    [DataRow("missing-paths")]
    public void ParserRejectsMalformedShellCommands(string expectedCategory)
    {
        var arguments = expectedCategory switch
        {
            "missing-source" => new[] { "DropSpace.exe", "--shell-add" },
            "invalid-source" => new[] { "DropSpace.exe", "--shell-add", "--source", "unknown", "x.txt" },
            _ => new[] { "DropSpace.exe", "--shell-add", "--source", "sendto" },
        };

        var result = ShellIntakeCommandLineParser.Parse(arguments);

        Assert.IsTrue(result.IsShellIntake);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(expectedCategory, result.ErrorCategory);
    }

    [TestMethod]
    public void ParserIgnoresNonShellCommands()
    {
        var result = ShellIntakeCommandLineParser.Parse(["DropSpace.exe", "--startup"]);

        Assert.IsFalse(result.IsShellIntake);
        Assert.IsFalse(result.Succeeded);
    }
}
