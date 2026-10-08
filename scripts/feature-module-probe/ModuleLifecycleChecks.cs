using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.App.Services.Dlc;
using DropSpace.Core.Dlc;
using DropSpace.Infrastructure.Dlc;
using DropSpace.Infrastructure.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ModuleLifecycleChecks
{
#if !MODULE_PROBE_FIXTURE_CATALOG
    public static Task<int> RunAsync(string[] args) =>
        Task.FromException<int>(new InvalidOperationException("Lifecycle checks require the isolated fixture catalog."));
#else
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string Option(string key) => args[Array.IndexOf(args, key) + 1];
        var scenario = Option("--lifecycle-check");
        var fixtures = Path.GetFullPath(Option("--fixtures"));
        var artifacts = Path.GetFullPath(Path.Combine(fixtures, ".."));
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "RELEASE_VERSION"))) repository = repository.Parent;
        var allowed = Path.GetFullPath(Path.Combine(repository?.FullName ?? throw new InvalidOperationException("Repository checkout required."),
            "scripts", "feature-module-probe", "artifacts")) + Path.DirectorySeparatorChar;
        if (!fixtures.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(fixtures))
            throw new InvalidOperationException("Fixtures must stay inside the probe artifacts directory.");
        var known = new[] { "uninstall-crash", "cleanup-retry", "update-cleanup", "capabilities", "dependency-order",
            "dependency-corrupt", "dependency-cycle", "dependency-race", "job-stop", "job-parent-crash",
            "uninstall-withdrawal", "uninstall-dependent-rejection" };
        if (!known.Contains(scenario, StringComparer.Ordinal)) throw new ArgumentException("Unknown focused lifecycle scenario");
        var root = Path.Combine(artifacts, "lifecycle-" + scenario);
        if (Directory.Exists(root) || File.Exists(root + ".json")) throw new InvalidOperationException("Fresh scenario root and receipt required.");
        using var downloads = new HttpRangeDownloader();
        using var store = new ModulePackageStore(root, downloads);
        await using var catalog = new OfficialModuleCatalog();
        var assertions = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions.Add(message);
        }
        var descriptors = JsonSerializer.Deserialize<OfficialModulePackage[]>(
            await File.ReadAllBytesAsync(Path.Combine(fixtures, "descriptors.json")), ModulePackageStore.JsonOptions)!;
        OfficialModulePackage Package(string id, string version = "1.0.0") => descriptors.Single(p => p.Id == id && p.Version == version);
        ModuleSnapshot Snapshot(FeatureModuleRuntime runtime, string id) => runtime.Snapshots.Single(s => s.Installation.Id == id);
        FeatureModuleRuntime Runtime() => new(store, catalog, NullLogger<FeatureModuleRuntime>.Instance);
        async Task SeedAsync(OfficialModulePackage package)
        {
            var file = Path.Combine(fixtures, package.Id + "-" + package.Version + "-win-x64.zip");
            using var stream = File.OpenRead(file);
            Check(stream.Length == package.Bytes && Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase),
                "Fixture archive matches the separately embedded descriptor");
            var staging = Path.Combine(root, "staging", package.Id, package.Version);
            Directory.CreateDirectory(staging);
            File.Copy(file, Path.Combine(staging, "package.zip"), false);
        }
        async Task PrepareAsync(string id, string version = "1.0.0")
        {
            await SeedAsync(Package(id, version));
            var installed = new Dictionary<string, ModuleInstallation> { ["probe.b"] = new("probe.b", "1.0.0", false, 1),
                ["probe.c"] = new("probe.c", "1.0.0", false, 1), ["probe.d"] = new("probe.d", "1.0.0", false, 1) };
            await store.PrepareAsync(Package(id, version), installed);
        }
        async Task WaitFileAsync(string file)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(file)) await Task.Delay(10, timeout.Token);
        }
        bool passed = false;
        string? error = null;
        var started = DateTimeOffset.UtcNow;
        try
        {
            switch (scenario)
            {
                case "uninstall-crash":
                {
                    // Legacy journal at the precise crash window: files removed, current version not cleared.
                    var marker = Path.Combine(store.GetDataDirectory("probe.b"), "retained.txt");
                    await File.WriteAllTextAsync(marker, "preserve");
                    await store.WriteAsync(new("probe.b", "1.0.0", false, 1, ModuleTransactionState.Cleaning, CandidateVersion: "1.0.0"));
                    await using (var runtime = Runtime())
                    {
                        await runtime.InitializeAsync();
                        var state = Snapshot(runtime, "probe.b");
                        Check(state.Installation.Version is null && state.Installation.Transaction == ModuleTransactionState.None &&
                            state.RunState == ModuleRunState.Stopped, "Deleted current version is recovered as uninstalled, not faulted");
                    }
                    await using (var runtime = Runtime())
                    {
                        await runtime.InitializeAsync();
                        Check(Snapshot(runtime, "probe.b").Installation.Version is null, "Recovery is durable on a second startup");
                    }
                    Check(await File.ReadAllTextAsync(marker) == "preserve", "Recovery retains user data");
                    break;
                }
                case "cleanup-retry":
                {
                    var directory = store.GetVersionDirectory("probe.b", "1.0.0");
                    Directory.CreateDirectory(directory);
                    var file = Path.Combine(directory, "held.txt");
                    await File.WriteAllTextAsync(file, "locked");
                    await store.WriteAsync(new("probe.b", "1.0.0", false, 1, ModuleTransactionState.PendingCleanup, CandidateVersion: "1.0.0"));
                    await using var runtime = Runtime();
                    using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        await runtime.InitializeAsync();
                        Check(Snapshot(runtime, "probe.b").Installation.Version is null &&
                            Snapshot(runtime, "probe.b").Installation.Transaction == ModuleTransactionState.PendingCleanup,
                            "Locked uninstall keeps a cleanup obligation and clears installed version");
                    }
                    await runtime.RetryCleanupAsync("probe.b");
                    Check(!Directory.Exists(directory) && Snapshot(runtime, "probe.b").Installation.Transaction == ModuleTransactionState.None,
                        "Retry removes released files and finishes removal without resurrection");
                    break;
                }
                case "update-cleanup":
                {
                    await PrepareAsync("probe.b");
                    var old = store.GetVersionDirectory("probe.b", "0.9.0");
                    Directory.CreateDirectory(old);
                    await File.WriteAllTextAsync(Path.Combine(old, "old.txt"), "retired");
                    await store.WriteAsync(new("probe.b", "1.0.0", false, 1, ModuleTransactionState.Cleaning, CandidateVersion: "0.9.0"));
                    await using var runtime = Runtime();
                    await runtime.InitializeAsync();
                    Check(Snapshot(runtime, "probe.b").Installation.Version == "1.0.0" &&
                        Snapshot(runtime, "probe.b").Manifest is not null && !Directory.Exists(old),
                        "Update cleanup preserves verified committed version and deletes only retired version");
                    break;
                }
                case "capabilities":
                {
                    var basis = new ModuleManifest { Id = "probe.b", Version = "1.0.0", EntryPoint = "worker.exe", Name = new("name"),
                        Files = [new("worker.exe", 0, new string('0', 64))],
                        Resources = ModuleContract.Languages.ToDictionary(l => l, _ => new Dictionary<string, string> { ["name"] = "Fixture" }) };
                    foreach (var version in new[] { 0, -1 })
                    {
                        var invalid = basis with { OptionalCapabilities = [new("ui.pages", version, "omit")] };
                        var rejected = false;
                        try { ModuleContract.Validate(invalid, new Dictionary<string, ModuleInstallation>(), int.MaxValue); }
                        catch (InvalidDataException) { rejected = true; }
                        Check(rejected && !ModuleContract.HasCapability(invalid, "ui.pages"), "Nonpositive capability is rejected even with omit: " + version);
                    }
                    var optional = basis with { OptionalCapabilities = [new("future.optional", 99, "omit")] };
                    ModuleContract.Validate(optional, new Dictionary<string, ModuleInstallation>(), int.MaxValue);
                    Check(!ModuleContract.HasCapability(optional, "future.optional"), "Unknown positive optional capability retains omit compatibility");
                    var supported = basis with { OptionalCapabilities = [new("ui.pages", 1, "omit")] };
                    ModuleContract.Validate(supported, new Dictionary<string, ModuleInstallation>(), int.MaxValue);
                    Check(ModuleContract.HasCapability(supported, "ui.pages"), "Supported version one remains available");
                    break;
                }
                case "dependency-order":
                case "dependency-corrupt":
                {
                    await PrepareAsync("probe.b"); await PrepareAsync("probe.a");
                    // A's journal is deliberately written first; loading cannot rely on enumeration order.
                    await store.WriteAsync(new("probe.a", "1.0.0", true, 1));
                    await store.WriteAsync(new("probe.b", "1.0.0", true, 1));
                    if (scenario == "dependency-corrupt")
                        await File.AppendAllTextAsync(Path.Combine(store.GetVersionDirectory("probe.b", "1.0.0"), "FixtureWorker.exe"), "corrupt");
                    await using var runtime = Runtime();
                    await runtime.InitializeAsync();
                    if (scenario == "dependency-corrupt")
                        Check(Snapshot(runtime, "probe.b").RunState == ModuleRunState.Faulted && Snapshot(runtime, "probe.a").RunState == ModuleRunState.Faulted,
                            "Unverified dependency prevents dependent activation");
                    else Check(Snapshot(runtime, "probe.b").RunState == ModuleRunState.Running && Snapshot(runtime, "probe.a").RunState == ModuleRunState.Running,
                        "All journals register first and dependencies activate before dependents");
                    break;
                }
                case "dependency-cycle":
                {
                    await PrepareAsync("probe.c"); await PrepareAsync("probe.d");
                    await store.WriteAsync(new("probe.c", "1.0.0", true, 1));
                    await store.WriteAsync(new("probe.d", "1.0.0", true, 1));
                    await using var runtime = Runtime();
                    await runtime.InitializeAsync();
                    Check(Snapshot(runtime, "probe.c").RunState == ModuleRunState.Faulted && Snapshot(runtime, "probe.d").RunState == ModuleRunState.Faulted,
                        "Cyclic dependencies fail closed without starting either worker");
                    break;
                }
                case "dependency-race":
                {
                    await PrepareAsync("probe.b");
                    await store.WriteAsync(new("probe.b", "1.0.0", false, 1));
                    await SeedAsync(Package("probe.a"));
                    var data = store.GetDataDirectory("probe.a");
                    await File.WriteAllTextAsync(Path.Combine(data, "hold-hello.txt"), "hold");
                    await using var runtime = Runtime();
                    await runtime.InitializeAsync();
                    var installing = runtime.InstallAsync("probe.a");
                    await WaitFileAsync(Path.Combine(data, "hello-entered.txt"));
                    var uninstalling = runtime.UninstallAsync("probe.b");
                    await File.WriteAllTextAsync(Path.Combine(data, "release-hello.txt"), "release");
                    await installing;
                    var rejected = false;
                    try { await uninstalling; } catch (InvalidOperationException) { rejected = true; }
                    Check(rejected && Snapshot(runtime, "probe.a").RunState == ModuleRunState.Running &&
                        Snapshot(runtime, "probe.b").Installation.Version == "1.0.0" &&
                        Directory.Exists(store.GetVersionDirectory("probe.b", "1.0.0")),
                        "Uninstall queued during dependent handshake rechecks graph after install commit");
                    // Also exercise reverse version integrity with an enabled dependent.
                    await SeedAsync(Package("probe.b", "2.0.0"));
                    rejected = false;
                    try { await runtime.InstallAsync("probe.b"); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected && Snapshot(runtime, "probe.b").Installation.Version == "1.0.0" &&
                        Snapshot(runtime, "probe.a").RunState == ModuleRunState.Running,
                        "Incompatible dependency update rolls back without breaking the enabled dependent");
                    break;
                }
                case "uninstall-withdrawal":
                case "uninstall-dependent-rejection":
                {
                    var dependent = scenario == "uninstall-dependent-rejection";
                    await PrepareAsync("probe.e");
                    await store.WriteAsync(new("probe.e", "1.0.0", true, 1));
                    if (!dependent) { await PrepareAsync("probe.b"); await store.WriteAsync(new("probe.b", "1.0.0", false, 1)); }
                    var blocker = dependent ? "probe.f" : "probe.a";
                    await SeedAsync(Package(blocker));
                    var data = store.GetDataDirectory(blocker);
                    var targetData = store.GetDataDirectory("probe.e");
                    await File.WriteAllTextAsync(Path.Combine(targetData, "retained.txt"), "preserve");
                    await File.WriteAllTextAsync(Path.Combine(data, "hold-hello.txt"), "hold");
                    await using var runtime = Runtime();
                    await runtime.InitializeAsync();
                    var action = runtime.InvokeAsync("probe.e", "hold", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
                    await WaitFileAsync(Path.Combine(targetData, "action-entered.txt"));
                    var installing = runtime.InstallAsync(blocker);
                    await WaitFileAsync(Path.Combine(data, "hello-entered.txt"));
                    var uninstalling = runtime.UninstallAsync("probe.e");
                    var duplicate = dependent ? null : runtime.UninstallAsync("probe.e");
                    Check(Snapshot(runtime, "probe.e").RunState == ModuleRunState.Stopping,
                        "Uninstall synchronously withdraws the target before the unrelated transaction completes");
                    var denied = false;
                    try { await runtime.InvokeAsync("probe.e", "ping", JsonSerializer.SerializeToElement(new { }), CancellationToken.None); }
                    catch (IOException) { denied = true; }
                    Check(denied, "New actions are rejected during queued uninstall");
                    denied = false;
                    try { await runtime.WriteSettingAsync("probe.e", "flag", "true", CancellationToken.None); }
                    catch (IOException) { denied = true; }
                    Check(denied && !(await store.ReadSettingsAsync("probe.e")).ContainsKey("flag"),
                        "Queued uninstall rejects settings without changing durable user preferences");
                    var canceled = false;
                    try { await action.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (OperationCanceledException) { canceled = true; }
                    Check(canceled && !installing.IsCompleted && !uninstalling.IsCompleted &&
                        Directory.Exists(store.GetVersionDirectory("probe.e", "1.0.0")),
                        "Already admitted action is canceled while graph transaction and package deletion remain queued");
                    Check((await store.LoadAsync()).Single(s => s.Id == "probe.e") is { Version: "1.0.0", Enabled: true },
                        "Immediate UI/request retirement does not overwrite the durable installation journal");
                    await File.WriteAllTextAsync(Path.Combine(data, "release-hello.txt"), "release");
                    await installing;
                    if (dependent)
                    {
                        var rejected = false;
                        try { await uninstalling; } catch (InvalidOperationException rejection) when (rejection.Message == "ModuleDependencyInUse") { rejected = true; }
                        Check(rejected && Snapshot(runtime, blocker).RunState == ModuleRunState.Running &&
                            Snapshot(runtime, "probe.e") is { RunState: ModuleRunState.Running, Installation.Version: "1.0.0", Installation.Enabled: true } &&
                            Directory.Exists(store.GetVersionDirectory("probe.e", "1.0.0")),
                            "Newly committed dependency rejects removal and restores the retired enabled target");
                        await runtime.InvokeAsync("probe.e", "ping", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
                        Check((await store.LoadAsync()).Single(s => s.Id == "probe.e") is { Version: "1.0.0", Enabled: true, Transaction: ModuleTransactionState.None },
                            "Rejected removal leaves a usable worker and a consistent durable installation");
                    }
                    else
                    {
                        await uninstalling; await duplicate!;
                        Check(Snapshot(runtime, "probe.e") is { RunState: ModuleRunState.Stopped, Installation.Version: null, Installation.Enabled: false,
                            Installation.Transaction: ModuleTransactionState.None } && !Directory.Exists(store.GetVersionDirectory("probe.e", "1.0.0")),
                            "Repeated queued requests finish scoped cleanup after confirmed worker exit without resurrection");
                    }
                    Check(await File.ReadAllTextAsync(Path.Combine(targetData, "retained.txt")) == "preserve", "Both removal outcomes retain user data");
                    break;
                }
                case "job-stop":
                case "job-parent-crash":
                {
                    await PrepareAsync("probe.b");
                    var manifest = await store.ReadManifestAsync("probe.b", "1.0.0");
                    var data = store.GetDataDirectory("probe.b");
                    await File.WriteAllTextAsync(Path.Combine(data, "spawn-child.txt"), "spawn before handshake");
                    await using var worker = await ModuleWorkerClient.StartAsync(
                        Path.Combine(store.GetVersionDirectory("probe.b", "1.0.0"), manifest.EntryPoint), data, manifest, CancellationToken.None);
                    await WaitFileAsync(Path.Combine(data, "child-ready.txt"));
                    var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(data, "child-pid.txt")));
                    using var child = Process.GetProcessById(pid);
                    Check(!child.HasExited, "Fixture starts a real child before its first hello response");
                    if (scenario == "job-parent-crash")
                    {
                        try { await worker.RequestAsync("action", JsonSerializer.SerializeToElement(new { action = "crash" }), CancellationToken.None); }
                        catch (IOException) { }
                    }
                    Check(await worker.StopAsync() && child.HasExited, "Stop confirms root exit and zero live processes in the Job");
                    using var released = new FileStream(Path.Combine(data, "child-lock.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Check(released.Length == 0, "Descendant file lock is released before stop succeeds");
                    break;
                }
                default: throw new ArgumentException("Unknown focused lifecycle scenario");
            }
            passed = true;
        }
        catch (Exception failure) { error = failure.ToString(); Console.Error.WriteLine(error); }
        Directory.CreateDirectory(artifacts);
        var receipt = new { scenario, passed, startedAtUtc = started, finishedAtUtc = DateTimeOffset.UtcNow,
            functionalScenarios = scenario == "dependency-race" ? 2 : scenario == "capabilities" ? 4 : 1,
            evidenceScope = "Windows automated probe of linked production types with test-only hash-pinned packages; no installed App/UI/user machine verification",
            assertions, error };
        await File.WriteAllTextAsync(Path.Combine(artifacts, "lifecycle-" + scenario + ".json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
#endif
}
