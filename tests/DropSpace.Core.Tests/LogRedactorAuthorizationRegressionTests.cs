using DropSpace.Core.Policies;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LogRedactorAuthorizationRegressionTests
{
    [TestMethod]
    [DataRow("https://share.example/s/id#k=confidential-share-key")]
    [DataRow("https://share.example/s/id?q=search#k=confidential-share-key")]
    public void UrlFragmentKeysAreNotWrittenToLogs(string url)
    {
        var redacted = LogRedactor.Redact("Failed to open " + url);
        Assert.IsFalse(redacted.Contains("confidential-share-key", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("#k=", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("share.example", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("Authorization: Bearer ")]
    [DataRow("Authorization=Bearer ")]
    [DataRow("authorization : bearer ")]
    [DataRow("Authorization:\tBearer\t")]
    [DataRow("Bearer ")]
    public void BearerCredentialsAreRemovedWithoutDiscardingDiagnosticContext(string prefix)
    {
        const string credential = "audit.credential_123~+/==";
        var redacted = LogRedactor.Redact($"request {prefix}{credential} status=401");

        Assert.IsFalse(redacted.Contains(credential, StringComparison.Ordinal), redacted);
        Assert.IsTrue(redacted.Contains("request", StringComparison.Ordinal), redacted);
        Assert.IsTrue(redacted.Contains("status=401", StringComparison.Ordinal), redacted);
    }
}
