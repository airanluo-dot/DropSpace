using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DropSpace.App.Services;

/// <summary>
/// Matches update signers by cryptographic certificate identity. Subject names are
/// descriptive metadata and are intentionally not used as an update trust anchor.
/// </summary>
public sealed class TrustedPublisherIdentityPolicy
{
    private readonly HashSet<string> _approvedSpkiHashes;
    private readonly HashSet<string> _approvedThumbprints;

    public TrustedPublisherIdentityPolicy(
        IEnumerable<string>? approvedSpkiSha256 = null,
        IEnumerable<string>? approvedThumbprints = null)
    {
        _approvedSpkiHashes = NormalizeSet(approvedSpkiSha256);
        _approvedThumbprints = NormalizeSet(approvedThumbprints);
    }

    public IReadOnlySet<string> ApprovedSpkiSha256 => _approvedSpkiHashes;

    public IReadOnlySet<string> ApprovedThumbprints => _approvedThumbprints;

    public bool IsApproved(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var spkiHash = ComputeSpkiSha256(certificate);
        var thumbprint = NormalizeHex(certificate.Thumbprint);
        return _approvedSpkiHashes.Contains(spkiHash) || _approvedThumbprints.Contains(thumbprint);
    }

    public static TrustedPublisherIdentityPolicy CreateDefault()
    {
        var spkiHashes = ParseEnvironmentList("DROPSPACE_TRUSTED_PUBLISHER_SPKI_SHA256");
        var thumbprints = ParseEnvironmentList("DROPSPACE_TRUSTED_PUBLISHER_THUMBPRINTS");

        // The installed executable is the current signer anchor. Rollover values
        // are supplied separately through deployment configuration, so a future
        // certificate can be accepted only when explicitly provisioned.
        foreach (var path in CandidateInstalledExecutables())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

#pragma warning disable SYSLIB0057
                using var signedCertificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                using var certificate = X509CertificateLoader.LoadCertificate(signedCertificate.GetRawCertData());
                spkiHashes.Add(ComputeSpkiSha256(certificate));
                if (!string.IsNullOrWhiteSpace(certificate.Thumbprint))
                {
                    thumbprints.Add(NormalizeHex(certificate.Thumbprint));
                }

                break;
            }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException or IOException or UnauthorizedAccessException)
            {
                // An unsigned portable/test host is expected. It simply has no
                // implicit publisher identity and therefore fails closed.
            }
        }

        return new TrustedPublisherIdentityPolicy(spkiHashes, thumbprints);
    }

    public static string ComputeSpkiSha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    public static string NormalizeHex(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
        if (normalized.Length == 0 || normalized.Length % 2 != 0 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new FormatException("A certificate identity must contain an even number of hexadecimal characters.");
        }

        return normalized;
    }

    private static HashSet<string> NormalizeSet(IEnumerable<string>? values)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return normalized;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                normalized.Add(NormalizeHex(value));
            }
        }

        return normalized;
    }

    private static HashSet<string> ParseEnvironmentList(string variableName)
    {
        var values = Environment.GetEnvironmentVariable(variableName)?
            .Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            try
            {
                result.Add(NormalizeHex(value));
            }
            catch (FormatException)
            {
                // Invalid deployment configuration is ignored here; the resulting
                // empty/partial policy still fails closed for unattended updates.
            }
        }

        return result;
    }

    private static IEnumerable<string> CandidateInstalledExecutables()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            yield return Environment.ProcessPath;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "DropSpace.exe");
    }
}
