using DropSpace.Core.Policies;
namespace DropSpace.Core.Tests;
[TestClass]
public sealed class LogUserInfoRegressionTests
{
    [TestMethod]
    public void UrlCredentialsAreRedactedAlongsideQueryAndFragment()
    {
        var output = LogRedactor.Redact("Failure https://alice:p%40ss@example.com/path?token=abc#key");
        Assert.IsFalse(output.Contains("alice", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("p%40ss", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("abc", StringComparison.Ordinal));
        StringAssert.Contains(output, "example.com/path");
    }
}
