using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DropSpace.App.Services;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class TrustedPublisherIdentityPolicyTests
{
    [TestMethod]
    public void SameSubjectWithDifferentKeyIsRejected()
    {
        using var approved = CreateCertificate();
        using var differentKey = CreateCertificate();
        var policy = new TrustedPublisherIdentityPolicy(
            [TrustedPublisherIdentityPolicy.ComputeSpkiSha256(approved)],
            [approved.Thumbprint!]);

        Assert.IsTrue(policy.IsApproved(approved));
        Assert.IsFalse(policy.IsApproved(differentKey));
    }

    [TestMethod]
    public void RolloverSpkiIdentityIsAcceptedWithoutSubjectMatching()
    {
        using var current = CreateCertificate();
        using var rollover = CreateCertificate();
        var policy = new TrustedPublisherIdentityPolicy(
            [TrustedPublisherIdentityPolicy.ComputeSpkiSha256(current), TrustedPublisherIdentityPolicy.ComputeSpkiSha256(rollover)],
            []);

        Assert.IsTrue(policy.IsApproved(current));
        Assert.IsTrue(policy.IsApproved(rollover));
    }

    [TestMethod]
    public void InvalidIdentityConfigurationIsRejectedByThePolicy()
    {
        Assert.ThrowsExactly<FormatException>(() => new TrustedPublisherIdentityPolicy(["not-a-hash"], []));
        using var certificate = CreateCertificate();
        Assert.IsFalse(new TrustedPublisherIdentityPolicy().IsApproved(certificate));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=airanluo-dot",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
