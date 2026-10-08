using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.App.Services.Dlc;
using DropSpace.Core.Dlc;
using DropSpace.Infrastructure.Dlc;
using DropSpace.Infrastructure.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--module-session", StringComparer.Ordinal)) return await PathologicalWorkerAsync(args);
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The worker lifecycle probe requires Windows.");
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var selected = Option("--case");
var evidenceFile = Option("--evidence");
var storageRoot = Option("--root");
if (selected is not ("1" or "2" or "3" or "4" or "5") || evidenceFile is null || storageRoot is null)
{
    Console.WriteLine("Opt-in only: --case 1|2|3|4|5 --root <fresh probe artifacts directory> --evidence <new JSON file> [--package <pinned ZIP>] [--failure-mode cancel|crash|timeout] [--rejection-mode traversal|hash|required-capability|incompatible-update]");
    return 2;
}
var repositoryDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (repositoryDirectory is not null && (!File.Exists(Path.Combine(repositoryDirectory.FullName, "RELEASE_VERSION")) ||
    !Directory.Exists(Path.Combine(repositoryDirectory.FullName, "src", "DropSpace.Core"))))
    repositoryDirectory = repositoryDirectory.Parent;
var repositoryRoot = repositoryDirectory?.FullName ?? throw new InvalidOperationException("Run the probe from its repository build output.");
var allowedRoot = Path.Combine(repositoryRoot, "scripts", "feature-module-probe", "artifacts") + Path.DirectorySeparatorChar;
storageRoot = Path.GetFullPath(storageRoot);
evidenceFile = Path.GetFullPath(evidenceFile);
if (!storageRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) ||
    !evidenceFile.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) ||
    evidenceFile.StartsWith(storageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
    Directory.Exists(storageRoot) || File.Exists(evidenceFile))
    throw new InvalidOperationException("Use a fresh root and evidence file within scripts/feature-module-probe/artifacts. Existing evidence is never overwritten.");
var started = DateTimeOffset.UtcNow;
var failureMode = Option("--failure-mode");
var rejectionMode = Option("--rejection-mode");
var provenance = "production embedded official catalog";
#if MODULE_PROBE_FIXTURE_CATALOG
provenance = "probe-only independently hash-pinned fixture catalog; not public publisher/download verification";
#endif
string? errorCategory = null;
var passed = false;
var assertions = new List<string>();
void Check(bool condition, string text)
{
    if (!condition) throw new InvalidOperationException(text);
    assertions.Add(text);
}
using var downloads = new HttpRangeDownloader();
using var store = new ModulePackageStore(storageRoot, downloads);
await using var catalog = new OfficialModuleCatalog();
async Task SeedArchiveAsync(OfficialModulePackage package, string file)
{
    var source = new FileInfo(file);
    await using var stream = File.OpenRead(file);
    var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    Check(source.Length == package.Bytes && digest == package.Sha256.ToLowerInvariant(), "Local cache exactly matches independently embedded package bytes/SHA");
    var staging = Path.Combine(storageRoot, "staging", package.Id, package.Version);
    Directory.CreateDirectory(staging);
    File.Copy(file, Path.Combine(staging, "package.zip"), overwrite: false);
}
async Task PreparePinnedAsync(OfficialModulePackage package)
{
    if (Option("--package") is { } cached) await SeedArchiveAsync(package, cached);
    await store.PrepareAsync(package, new Dictionary<string, ModuleInstallation>());
}
ModuleSnapshot One(FeatureModuleRuntime runtime, string id) => runtime.Snapshots.Single(snapshot => snapshot.Installation.Id == id);
try
{
    switch (selected)
    {
        case "1":
        {
            var before = Process.GetProcessesByName("DropSpace.Module.Worker").Length;
            await using var runtime = new FeatureModuleRuntime(store, catalog, NullLogger<FeatureModuleRuntime>.Instance);
            await runtime.InitializeAsync();
            Check(!Directory.Exists(storageRoot), "No installed modules create no storage directory");
            Check(runtime.Snapshots.All(snapshot => snapshot.RunState == ModuleRunState.Stopped && !snapshot.Installation.Enabled), "No installed modules start no worker");
            Check(Process.GetProcessesByName("DropSpace.Module.Worker").Length == before, "Worker process count stays unchanged");
            break;
        }
        case "2":
        {
            var package = catalog.Packages.Single(package => package.Id == "dropspace.sample" && package.Version == "1.0.0");
            if (Option("--package") is { } cached) await SeedArchiveAsync(package, cached);
            await using var runtime = new FeatureModuleRuntime(store, catalog, NullLogger<FeatureModuleRuntime>.Instance);
            await runtime.InitializeAsync();
            await runtime.InstallAsync(package.Id);
            Check(One(runtime, package.Id).RunState == ModuleRunState.Running, "Actual install completes worker handshake and runs");
            var result = await runtime.InvokeAsync(package.Id, "show", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
            Check(result.Result.GetProperty("messageKey").GetString() == "message" && runtime.IslandContents.Count == 1, "Actual action returns declared message/island content");
            await runtime.WriteSettingAsync(package.Id, "show-island", "false", CancellationToken.None);
            var marker = Path.Combine(store.GetDataDirectory(package.Id), "retained-probe.txt");
            await File.WriteAllTextAsync(marker, "Probe-owned data preservation marker");
            await runtime.DisableAsync(package.Id);
            await runtime.DisableAsync(package.Id);
            Check(One(runtime, package.Id).RunState == ModuleRunState.Stopped && runtime.IslandContents.Count == 0, "Repeated disable stops once and withdraws island contributions");
            await runtime.EnableAsync(package.Id);
            result = await runtime.InvokeAsync(package.Id, "show", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
            Check(result.Island is null && (await runtime.ReadSettingsAsync(package.Id, CancellationToken.None))["show-island"] == "false", "Reactivation restores acknowledged host-owned settings");
            await runtime.UninstallAsync(package.Id);
            await runtime.UninstallAsync(package.Id);
            Check(One(runtime, package.Id).Installation.Version is null && !Directory.Exists(store.GetVersionDirectory(package.Id, package.Version)), "Repeated uninstall records actual physical package removal");
            Check(File.Exists(marker), "Uninstall retains module-owned user data");
            break;
        }
        case "3":
        {
            var package = catalog.Packages.Single(package => package.Id == "dropspace.sample" && package.Version == "1.0.0");
            await PreparePinnedAsync(package);
            await store.WriteAsync(new(package.Id, package.Version, false, 1, ModuleTransactionState.Activating, package.Version, "1.0.1"));
            var candidate = store.GetVersionDirectory(package.Id, "1.0.1");
            Directory.CreateDirectory(candidate);
            await File.WriteAllTextAsync(Path.Combine(candidate, "interrupted.txt"), "Probe-owned uncommitted candidate");
            var marker = Path.Combine(store.GetDataDirectory(package.Id), "retained-probe.txt");
            await File.WriteAllTextAsync(marker, "Probe-owned recovery marker");
            await using (var recovering = new FeatureModuleRuntime(store, catalog, NullLogger<FeatureModuleRuntime>.Instance))
            {
                await recovering.InitializeAsync();
                Check(!Directory.Exists(candidate) && One(recovering, package.Id).Installation.Version == package.Version, "Interrupted activation removes only candidate and retains committed version");
            }
            var workerPath = Path.Combine(store.GetVersionDirectory(package.Id, package.Version), "DropSpace.Module.Worker.exe");
            await using (var locked = new FileStream(workerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var cleaning = new FeatureModuleRuntime(store, catalog, NullLogger<FeatureModuleRuntime>.Instance))
            {
                await cleaning.InitializeAsync();
                await cleaning.UninstallAsync(package.Id);
                Check(One(cleaning, package.Id).Installation.Transaction == ModuleTransactionState.PendingCleanup && File.Exists(workerPath), "Locked package records real pending cleanup");
            }
            await using (var resumed = new FeatureModuleRuntime(store, catalog, NullLogger<FeatureModuleRuntime>.Instance))
            {
                await resumed.InitializeAsync();
                Check(One(resumed, package.Id).Installation.Transaction == ModuleTransactionState.None && !Directory.Exists(store.GetVersionDirectory(package.Id, package.Version)), "Next startup recovers cleanup after lock release");
                Check(File.Exists(marker), "Interrupted install/uninstall recovery retains scoped data");
            }
            break;
        }
        case "4":
        {
            if (failureMode is not ("cancel" or "crash" or "timeout")) throw new ArgumentException("An explicit failure mode is required.");
            Directory.CreateDirectory(storageRoot);
            var manifest = new ModuleManifest { Id = "probe.pathological", Version = "1.0.0", EntryPoint = "DropSpace.FeatureModuleProbe.exe", Name = new("probe") };
            await using var worker = await ModuleWorkerClient.StartAsync(Environment.ProcessPath!, storageRoot, manifest, CancellationToken.None);
            using var cancel = new CancellationTokenSource();
            if (failureMode == "cancel") cancel.CancelAfter(TimeSpan.FromMilliseconds(150));
            var rejected = false;
            try { await worker.RequestAsync("action", JsonSerializer.SerializeToElement(new { action = failureMode }), cancel.Token); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException) { rejected = true; }
            Check(rejected, "Actual pathological process failure rejects the request");
            Check(await worker.StopAsync(), "Owned pathological process exits within bounded stop policy");
            Check(!worker.IsAlive, "Retired session cannot deliver a late result into a live worker");
            break;
        }
        case "5":
        {
#if !MODULE_PROBE_FIXTURE_CATALOG
            throw new InvalidOperationException("Case 5 requires an explicitly built probe fixture catalog; production trust is not mutated.");
#else
            if (rejectionMode is not ("traversal" or "hash" or "required-capability" or "incompatible-update")) throw new ArgumentException("An explicit rejection mode is required.");
            var previousFile = Option("--previous-descriptor") ?? throw new ArgumentException("Provide the separately prepared previous probe descriptor.");
            var previous = JsonSerializer.Deserialize<OfficialModulePackage>(await File.ReadAllBytesAsync(previousFile), ModulePackageStore.JsonOptions)
                ?? throw new InvalidDataException("InvalidProbeDescriptor");
            OfficialModuleCatalog.Require(previous);
            var candidate = catalog.Packages.Single(package => package.Id == "dropspace.sample" && package.Version == "1.0.1");
            await PreparePinnedAsync(previous);
            await store.WriteAsync(new(previous.Id, previous.Version, true, 1));
            await using var runtime = new FeatureModuleRuntime(store, catalog, NullLogger<FeatureModuleRuntime>.Instance);
            await runtime.InitializeAsync();
            Check(One(runtime, previous.Id).RunState == ModuleRunState.Running, "Previous compatible version really runs before rejected update");
            var fixture = Option("--candidate") ?? throw new ArgumentException("Provide the exact fixture ZIP declared by the probe-only catalog.");
            await SeedArchiveAsync(candidate, fixture);
            var rejected = false;
            try { await runtime.InstallAsync(candidate.Id); }
            catch (Exception exception) when (exception is InvalidDataException or IOException) { rejected = true; }
            Check(rejected, "Malformed/unsupported candidate activation is rejected");
            Check(One(runtime, previous.Id).Installation.Version == previous.Version && One(runtime, previous.Id).RunState == ModuleRunState.Running,
                "Rejected candidate preserves the installed running compatible version");
            var result = await runtime.InvokeAsync(previous.Id, "show", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
            Check(result.Result.GetProperty("messageKey").GetString() == "message", "Existing version remains usable after update rejection");
            break;
#endif
        }
    }
    passed = true;
}
catch (Exception exception)
{
    errorCategory = exception.GetType().Name;
    Console.Error.WriteLine(exception.Message);
}
finally
{
    Directory.CreateDirectory(Path.GetDirectoryName(evidenceFile)!);
    await using var file = new FileStream(evidenceFile, FileMode.CreateNew, FileAccess.Write);
    await JsonSerializer.SerializeAsync(file, new
    {
        scenario = selected, failureMode, rejectionMode, startedAtUtc = started, finishedAtUtc = DateTimeOffset.UtcNow,
        passed, errorCategory, actualFunctionalScenarios = 1, provenance,
        packageSource = selected is "1" or "4" ? "not used in this process-only scenario" :
            Option("--package") is null ? "anonymous official downloader" : "locally cached ZIP verified against independent embedded catalog",
        assertions,
    }, new JsonSerializerOptions { WriteIndented = true });
}
return passed ? 0 : 1;

static async Task<int> PathologicalWorkerAsync(string[] args)
{
    var index = Array.IndexOf(args, "--module-session");
    if (index < 0 || index + 1 >= args.Length) return 2;
    var session = args[index + 1];
    Console.InputEncoding = new UTF8Encoding(false);
    Console.OutputEncoding = new UTF8Encoding(false);
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var request = JsonSerializer.Deserialize<ModuleEnvelope>(line)!;
        JsonElement result;
        if (request.Method == "hello") result = JsonSerializer.SerializeToElement(new
        { moduleId = "probe.pathological", moduleVersion = "1.0.0", protocol = 1, dataVersion = 1 });
        else if (request.Method == "stop") result = JsonSerializer.SerializeToElement(new { stopped = true });
        else
        {
            var mode = request.Payload.GetProperty("action").GetString();
            if (mode == "crash") return 9;
            await Task.Delay(TimeSpan.FromSeconds(15)); // deliberately ignores cancellation in fixture only
            result = JsonSerializer.SerializeToElement(new { late = true });
        }
        var response = new ModuleEnvelope(1, session, request.Id, "result", JsonSerializer.SerializeToElement(new ModuleReply(result)));
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
        await Console.Out.FlushAsync();
        if (request.Method == "stop") return 0;
    }
    return 0;
}
