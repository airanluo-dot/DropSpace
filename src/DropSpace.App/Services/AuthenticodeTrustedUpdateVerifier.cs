using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;

namespace DropSpace.App.Services;

public sealed class AuthenticodeTrustedUpdateVerifier : ITrustedUpdateVerifier
{
    // This is deliberately an exact, compiled trust policy. Artifact Signing must issue the
    // production certificate with this subject (or this allow-list must be reviewed explicitly).
    private static readonly HashSet<string> TrustedSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "CN=airanluo-dot",
    };

    public Task<TrustedUpdateVerification> VerifyPublisherAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
        {
            return Task.FromResult(new TrustedUpdateVerification(false, "The update file is missing."));
        }

        var trustStatus = VerifyEmbeddedSignature(filePath);
        if (trustStatus != 0)
        {
            return Task.FromResult(new TrustedUpdateVerification(false, $"Authenticode validation failed (0x{trustStatus:x8})."));
        }

        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            return Task.FromResult(TrustedSubjects.Contains(certificate.Subject)
                ? new TrustedUpdateVerification(true, "Authenticode signature and DropSpace publisher identity are valid.")
                : new TrustedUpdateVerification(false, "The signature is valid but the publisher is not trusted by DropSpace."));
        }
        catch (CryptographicException)
        {
            return Task.FromResult(new TrustedUpdateVerification(false, "The signer certificate could not be read."));
        }
    }

    private static uint VerifyEmbeddedSignature(string filePath)
    {
        var filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfoPointer = IntPtr.Zero;
        var trustDataPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePathPointer,
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00001000,
            };
            trustDataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, false);
            return WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, trustDataPointer);
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(trustDataPointer);
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(filePathPointer);
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern uint WinVerifyTrust(IntPtr windowHandle, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr trustData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
