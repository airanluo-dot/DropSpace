using DropSpace.Core.Policies;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LogRedactorAuthorizationRegressionTests
{
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
