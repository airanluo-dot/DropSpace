using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.App.Services.Dlc;
using DropSpace.Core.Dlc;
using DropSpace.Infrastructure.Dlc;
using DropSpace.Infrastructure.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--module-session", StringComparer.Ordinal)) return await PathologicalWorkerAsync(args);
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var selected = Option("--case");
// Case 6 never starts a worker: only managed inventory and journal recovery are exercised.
if (selected != "6" && !OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("The worker lifecycle probe requires Windows.");
var evidenceFile = Option("--evidence");
var storageRoot = Option("--root");
if (selected is not ("1" or "2" or "3" or "4" or "5" or "6") || evidenceFile is null || storageRoot is null)
{
    Console.WriteLine("Opt-in only: --case 1|2|3|4|5|6 --root <fresh probe artifacts directory> --evidence <new JSON file> [--package <pinned ZIP>] [--failure-mode cancel|crash|timeout] [--rejection-mode traversal|hash|required-capability|incompatible-update]");
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
if (selected == "6") provenance = "local synthetic inventory fixtures (MinimumWindowsBuild=0); no Windows compatibility, worker or publisher/download verification";
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
        case "6":
        {
            const string firstId = "probe.order-one", secondId = "probe.order-two";
            const string cleanedId = "probe.removing", blockedId = "probe.after-cleanup", missingId = "probe.missing";
            const string version = "1.0.0";
            await SeedInventoryAsync(firstId, []);
            await SeedInventoryAsync(secondId, []);
            await SeedInventoryAsync(blockedId, [new(cleanedId, version, "2.0.0")]);
            await SeedInventoryAsync(missingId, [new("probe.never-installed", version, "2.0.0")]);
            await SeedInventoryAsync(cleanedId, [], uninstalling: true);
            // Bind the dependency direction to the actual store enumeration, without
            // relying on insertion order or the filesystem's sorting policy.
            var observed = (await store.LoadAsync()).Select(entry => entry.Id).ToArray();
            var orderedPair = observed.Where(id => id is firstId or secondId).ToArray();
            Check(orderedPair.Length == 2, "Both installed order fixtures were enumerated");
            var dependentId = orderedPair[0];
            var dependencyId = orderedPair[1];
            await SeedInventoryAsync(dependentId, [new(dependencyId, version, "2.0.0")], writeState: false);
            var actualOrder = (await store.LoadAsync()).Select(entry => entry.Id).ToArray();
            Check(Array.IndexOf(actualOrder, dependentId) < Array.IndexOf(actualOrder, dependencyId),
                "The dependent is actually enumerated before its installed dependency");
            var preservedData = Path.Combine(store.GetDataDirectory(cleanedId), "retained.txt");
            await File.WriteAllTextAsync(preservedData, "fixture user data");
            await using var runtime = new FeatureModuleRuntime(store, catalog, NullLogger<FeatureModuleRuntime>.Instance);
            // Hold only this temporary store's gate to make the startup/mutation
            // overlap deterministic; the actual store and runtime remain unchanged.
            var storeGate = (SemaphoreSlim)typeof(ModulePackageStore).GetField("_gate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            await storeGate.WaitAsync();
            Task initializing;
            Task disabling;
            try
            {
                initializing = runtime.InitializeAsync();
                disabling = runtime.DisableAsync(dependentId);
                Check(!initializing.IsCompleted && !disabling.IsCompleted,
                    "A public mutation waits for the in-progress inventory instead of constructing another slot");
            }
            finally { storeGate.Release(); }
            await Task.WhenAll(initializing, disabling);
            Check(runtime.Snapshots.Count(snapshot => snapshot.Installation.Id == dependentId) == 1,
                "Overlapping startup and disable retain exactly one authoritative module slot");
            var dependent = One(runtime, dependentId);
            var dependency = One(runtime, dependencyId);
            Check(dependent.Manifest is not null && dependent.RunState == ModuleRunState.Stopped &&
                dependency.Manifest is not null && dependency.RunState == ModuleRunState.Stopped &&
                !dependency.Installation.Enabled,
                "A dependent-first inventory accepts its verified installed dependency even when disabled");
            var cleaned = One(runtime, cleanedId);
            Check(cleaned.Installation.Version is null && cleaned.Installation.Transaction == ModuleTransactionState.None &&
                cleaned.Installation.CandidateVersion is null && cleaned.Manifest is null &&
                !Directory.Exists(store.GetVersionDirectory(cleanedId, version)),
                "Recovered uninstall physically removes the package and clears the installed version");
            var persisted = (await store.LoadAsync()).Single(entry => entry.Id == cleanedId);
            Check(persisted.Version is null && persisted.CandidateVersion is null &&
                persisted.Transaction == ModuleTransactionState.None && File.Exists(preservedData),
                "Completed uninstall state is durable while its scoped data is retained");
            Check(One(runtime, blockedId).Manifest is null && One(runtime, blockedId).RunState == ModuleRunState.Faulted,
                "A cleaned uninstall journal cannot satisfy another module's dependency");
            Check(One(runtime, missingId).Manifest is null && One(runtime, missingId).RunState == ModuleRunState.Faulted,
                "A genuinely missing dependency is still rejected");
            Check(runtime.DiagnosticState is { WorkerOwners: 0, AliveWorkers: 0, ActiveCalls: 0, HasExpiryTimer: false },
                "Inventory recovery owns no workers, calls or expiry timer");
            break;

            async Task SeedInventoryAsync(string id, ModuleDependency[] dependencies, bool uninstalling = false, bool writeState = true)
            {
                // Inert bytes are never launched. The manifest deliberately declares
                // build 0 so this managed scenario can run on Linux without changing
                // ModuleContract's production compatibility checks.
                var worker = Encoding.UTF8.GetBytes("inert inventory-only fixture");
                var manifest = new ModuleManifest
                {
                    Id = id, Version = version, EntryPoint = "InventoryFixture.exe", Name = new("name"),
                    MinimumWindowsBuild = 0, Dependencies = dependencies,
                    Files = [new("InventoryFixture.exe", worker.Length, Convert.ToHexString(SHA256.HashData(worker)).ToLowerInvariant())],
                    Resources = ModuleContract.Languages.ToDictionary(language => language,
                        _ => new Dictionary<string, string> { ["name"] = "Inventory fixture" }),
                };
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ModulePackageStore.JsonOptions);
                var directory = store.GetVersionDirectory(id, version);
                Directory.CreateDirectory(directory);
                var archivePath = Path.Combine(directory, ".package.zip");
                await using (var archiveFile = new FileStream(archivePath, FileMode.Create, FileAccess.Write))
                using (var archive = new ZipArchive(archiveFile, ZipArchiveMode.Create))
                {
                    await using (var file = archive.CreateEntry("manifest.json").Open()) await file.WriteAsync(manifestBytes);
                    await using (var file = archive.CreateEntry("InventoryFixture.exe").Open()) await file.WriteAsync(worker);
                }
                var archiveBytes = await File.ReadAllBytesAsync(archivePath);
                var package = new OfficialModulePackage(id, version, OfficialModuleCatalog.PackageUrl(id, version),
                    archiveBytes.Length, Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant());
                await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), manifestBytes);
                await File.WriteAllBytesAsync(Path.Combine(directory, "InventoryFixture.exe"), worker);
                await File.WriteAllBytesAsync(Path.Combine(directory, ".package-record.json"),
                    JsonSerializer.SerializeToUtf8Bytes(package, ModulePackageStore.JsonOptions));
                if (writeState)
                    await store.WriteAsync(new(id, version, false, 1,
                        uninstalling ? ModuleTransactionState.Cleaning : ModuleTransactionState.None,
                        CandidateVersion: uninstalling ? version : null));
            }
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
        packageSource = selected == "6" ? "locally generated inert inventory files; no downloader or installer invoked" :
            selected is "1" or "4" ? "not used in this process-only scenario" :
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
