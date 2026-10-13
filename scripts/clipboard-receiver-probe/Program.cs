using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DropSpace.App.Services;
using DropSpace.Core.Models;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Network;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

var root = Path.Combine(Path.GetTempPath(), "DropSpace-clipboard-receiver-probe", Guid.NewGuid().ToString("N"));
try
{
    var paths = new AppStoragePaths(root);
    var settings = new JsonSettingsService(paths);
    var enabled = new AppSettings { EnableCrossDeviceClipboard = true };
    var disabled = enabled with { EnableCrossDeviceClipboard = false };
    var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
    var repository = new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
    var identities = new DeviceIdentityStore(paths);

    // The real store's DPAPI persistence is Windows-only. Seed only its private
    // cache with an ephemeral certificate; production GetOrCreateAsync still runs.
    // No identity file, OS certificate store, host listener or peer secret is used.
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var certificateRequest = new CertificateRequest("CN=DropSpace-Receiver-Probe", key, HashAlgorithmName.SHA256);
    using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    var identity = new DeviceIdentity(Guid.NewGuid(), "Receiver probe", DevicePlatform.Windows, certificate,
        Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant());
    var cache = typeof(DeviceIdentityStore).GetField("_cached", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Identity fixture cache boundary changed.");
    cache.SetValue(identities, identity);

    var secrets = new DeviceSecretStore(paths);
    await using var pairing = new DropLinkPairingService(identities, secrets);
    var transfers = new TransferRepository(database);
    var client = new DropLinkClient(identities, secrets, pairing, transfers);
    await using var host = new DropLinkHost(identities, secrets, pairing, transfers, NullLogger<DropLinkHost>.Instance,
        new DropLinkNonceCache(), new StagingLeaseStore(paths, NullLogger<StagingLeaseStore>.Instance));
    var capture = new ClipboardCaptureService(repository);
    await using var receiver = new CrossDeviceClipboardService(capture, new FilePayloadStore(paths), identities,
        client, host, settings, new DeviceHandoffService(), NullLogger<CrossDeviceClipboardService>.Instance);

    await SetEnabledAsync(true);
    var oldReceiver = SnapshotReceiver(host);
    var envelope = ClipboardEnvelopePolicy.CreateText(Guid.NewGuid(), 1, "clipboard receiver fixture one");
    await SetEnabledAsync(false);
    Check(!receiver.IsEnabled && capture.CaptureHandlerCount == 0, "disable did not retire local capture ownership");
    await ExpectUnavailableAsync(oldReceiver, envelope);
    Check(capture.ImportAttempts == 0, "disabled receiver attempted an import");

    await SetEnabledAsync(true);
    await ExpectUnavailableAsync(oldReceiver, envelope);
    Check(capture.ImportAttempts == 0, "re-enable revived the old receiver");
    var currentReceiver = SnapshotReceiver(host);
    await currentReceiver(envelope, CancellationToken.None);
    await currentReceiver(envelope, CancellationToken.None);
    Check(capture.ImportAttempts == 1 && capture.SuccessfulImports == 1, "duplicate request imported more than once");
    Check(await repository.CountAsync(ItemSource.Clipboard) == 1, "new active receiver did not create the temporary history row");

    await currentReceiver(ClipboardEnvelopePolicy.CreateText(identity.DeviceId, 2, "local-origin fixture"), CancellationToken.None);
    Check(capture.ImportAttempts == 1, "same-origin envelope was imported");
    await SetEnabledAsync(false);
    await SetEnabledAsync(true);
    await ExpectUnavailableAsync(currentReceiver, envelope);
    var newestReceiver = SnapshotReceiver(host);
    await newestReceiver(envelope, CancellationToken.None);
    Check(capture.ImportAttempts == 1 && capture.SuccessfulImports == 1, "toggle forgot an accepted event receipt");

    var retryEnvelope = ClipboardEnvelopePolicy.CreateText(envelope.OriginDeviceId, 3, "clipboard receiver fixture two");
    capture.FailNextImport = true;
    try
    {
        await newestReceiver(retryEnvelope, CancellationToken.None);
        throw new InvalidOperationException("The injected import failure was hidden.");
    }
    catch (IOException) { }
    Check(capture.ImportAttempts == 2 && capture.SuccessfulImports == 1, "failed import committed a row");
    await newestReceiver(retryEnvelope, CancellationToken.None);
    await newestReceiver(retryEnvelope, CancellationToken.None);
    Check(capture.ImportAttempts == 3 && capture.SuccessfulImports == 2, "failed event was not retryable or successful retry duplicated");
    Check(await repository.CountAsync(ItemSource.Clipboard) == 2, "temporary history does not match the two accepted events");
    Check(!host.IsRunning && host.Endpoint is null, "probe unexpectedly started a network listener");
    Check(!File.Exists(Path.Combine(paths.Data, "device-identity.bin")), "probe unexpectedly persisted a protected identity");
    Console.WriteLine("PASS one receiver lifecycle scenario: copied callbacks retire across disable/re-enable; accepted event retries stay idempotent; failed imports can retry; two temporary SQLite imports, no server or system clipboard.");

    async Task SetEnabledAsync(bool value)
    {
        var next = value ? enabled : disabled;
        await settings.SaveAsync(next);
        await receiver.UpdateSettingsAsync(await settings.LoadAsync());
        if (value) Check(capture.CaptureHandlerCount == 1, "enable registered duplicate local capture handlers");
    }
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static Func<ClipboardEnvelope, CancellationToken, Task> SnapshotReceiver(DropLinkHost host)
{
    var field = typeof(DropLinkHost).GetField("ClipboardReceived", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Host event snapshot boundary changed.");
    var handlers = (field.GetValue(host) as Delegate)?.GetInvocationList()
        ?? throw new InvalidOperationException("No receiver was subscribed.");
    Check(handlers.Length == 1, "host has duplicate receiver subscriptions");
    return (Func<ClipboardEnvelope, CancellationToken, Task>)handlers[0];
}

static async Task ExpectUnavailableAsync(Func<ClipboardEnvelope, CancellationToken, Task> callback, ClipboardEnvelope envelope)
{
    try
    {
        await callback(envelope, CancellationToken.None);
        throw new InvalidOperationException("A retired receiver reported successful acceptance.");
    }
    catch (ClipboardReceiverUnavailableException)
    {
        Check(ClipboardReceiverUnavailableException.ErrorCategory == "clipboard-receiver-unavailable", "receiver rejection category changed");
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
