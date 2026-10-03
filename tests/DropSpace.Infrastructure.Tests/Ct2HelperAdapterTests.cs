using DropSpace.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Ct2HelperAdapterTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [TestMethod]
    public void RoutingPrefersDirectAndAllowsOnlyDocumentedPivot()
    {
        var direct = Package(HashA);
        var pivotOne = Package(HashB);
        var pivotTwo = Package(HashC);
        Assert.AreEqual(direct, Ct2RoutePlanner.Resolve("ja-JP", "zh-CN", (s, t) => s == "ja" && t == "zh" ? direct : null).Single().Package);
        var pivot = Ct2RoutePlanner.Resolve("ko", "zh", (s, t) => (s, t) switch { ("ko", "en") => pivotOne, ("en", "zh") => pivotTwo, _ => null });
        CollectionAssert.AreEqual(new[] { "ko-en", "en-zh" }, pivot.Select(x => $"{x.Source}-{x.Target}").ToArray());
        Assert.HasCount(0, Ct2RoutePlanner.Resolve("zh", "ja", (_, _) => null));
    }

    [TestMethod]
    public void CacheIdentityIsolatedByEngineModelTokenizerProtocolAndRoute()
    {
        var original = Package(HashA);
        var identities = new HashSet<string>
        {
            original.CacheIdentity("ja", "en"),
            (original with { EngineSha256 = HashB }).CacheIdentity("ja", "en"),
            (original with { ModelSha256 = HashB }).CacheIdentity("ja", "en"),
            (original with { TokenizerSha256 = HashB }).CacheIdentity("ja", "en"),
            (original with { Decoder = Ct2DecoderProtocol.HelsinkiOpus }).CacheIdentity("ja", "en"),
            original.CacheIdentity("ko", "en"),
            (original with { RuntimeVersion = "ct2-runtime-v2" }).CacheIdentity("ja", "en"),
            original.CacheIdentity("ja", "en", "pivot"),
        };
        Assert.HasCount(8, identities);
    }

    [TestMethod]
    public void ApplyingOutputPreservesOriginalsOrderAndTimestamps()
    {
        var source = new LyricsDocument([
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "first", null, []),
            new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "second", null, [])], LyricsProviderKind.LocalLrc);
        var result = Ct2LyricsOutput.Apply(source, [0, 1], [new(0, "一"), new(1, "二")], "zh-CN");
        for (var i = 0; i < 2; i++)
        {
            Assert.AreEqual(source.Lines[i].Text, result.Lines[i].Text);
            Assert.AreEqual(source.Lines[i].Start, result.Lines[i].Start);
            Assert.AreEqual(source.Lines[i].End, result.Lines[i].End);
        }
        Assert.AreEqual("一", result.Lines[0].Secondary);
    }

    [TestMethod]
    [DataRow("{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"ok\"}]}", "missing")]
    [DataRow("{\"version\":1,\"lines\":[{\"id\":1,\"text\":\"x\"},{\"id\":0,\"text\":\"y\"}]}", "reordered")]
    [DataRow("{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"x\"},{\"id\":0,\"text\":\"y\"}]}", "duplicate")]
    [DataRow("{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"x\"},{\"id\":1,\"text\":\"y\"}],\"extra\":true}", "extra")]
    public async Task FakeHelperRejectsProtocolErrors(string response, string _)
    {
        WindowsProcessFixture.RequireAvailable();
        using var fixture = new Fixture($"cat >/dev/null\nprintf '%s' '{response}'\n", new { kind = "ct2", output = response });
        try
        {
            using var adapter = new Ct2HelperAdapter(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<InvalidDataException>(() => adapter.TranslateAsync(fixture.Reference, "ja", "en",
                [new(0, "a"), new(1, "b")], CancellationToken.None));
        }
        finally { }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(12)]
    public async Task CancellationRejectsLateOutputAndCleanupReleasesExecutable(int repetitions)
    {
        WindowsProcessFixture.RequireAvailable();
        for (var iteration = 0; iteration < repetitions; iteration++)
            await CancelAndDeleteImmediatelyAsync();
    }

    private static async Task CancelAndDeleteImmediatelyAsync()
    {
        using var fixture = new Fixture("cat >/dev/null\nprintf ready > ../started\nsleep 2\nprintf '%s' '{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"late\"}]}'\n",
            new { kind = "ct2", ready = true, delayMilliseconds = 2000, lateOutput = "{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"late\"}]}" });
        using var adapter = new Ct2HelperAdapter(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var running = adapter.TranslateAsync(fixture.Reference, "ja", "en", [new(0, "a")], cancel.Token);
        System.Diagnostics.Process? observed = null;
        try
        {
            await WaitForStartedAsync(fixture, running);
            observed = System.Diagnostics.Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(fixture.ProcessIdPath),
                System.Globalization.CultureInfo.InvariantCulture));
            _ = observed.Handle; // Keep the same native process identity across cancellation.
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => running);
            await adapter.DrainCleanupAsync(CancellationToken.None);
            // Synchronous WaitForExit(0) queries the kernel signal, unlike HasExited.
            Assert.IsTrue(observed.WaitForExit(0), "Cleanup returned before the kernel process object was signaled.");
            File.Delete(fixture.Executable); // No sleep, retry or test-only release after the drain.
            Assert.IsFalse(File.Exists(fixture.Executable));
        }
        finally
        {
            cancel.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            finally { observed?.Dispose(); }
        }
    }

    private static Ct2PackageIdentity Package(string model) => new(HashA, model, HashC,
        Ct2TokenizerProtocol.ArgosSentencePiece, Ct2DecoderProtocol.Argos);

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"version\":1,\"lines\":null}")]
    [DataRow("{\"version\":1,\"lines\":[null]}")]
    [DataRow("{\"version\":1,\"lines\":[{\"id\":0}]}")]
    [DataRow("{\"version\":1,\"version\":1,\"lines\":[{\"id\":0,\"text\":\"ok\"}]}")]
    [DataRow("{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"ok\",\"text\":\"other\"}]}")]
    [DataRow("{\"version\":1,\"lines\":[{\"id\":0,\"text\":null}]}")]
    [DataRow("{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"ok\"}]} {}")]
    [DataRow("{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"\\ud800\"}]}")]
    public void StrictResponseSchemaRejectsMalformedValues(string response) =>
        Assert.ThrowsExactly<InvalidDataException>(() => Ct2HelperAdapter.ParseResponse([new(0, "source")], Encoding.UTF8.GetBytes(response)));

    [TestMethod]
    public async Task VerifiedHelperCompletesAndReleasesOwnershipRepeatedly()
    {
        WindowsProcessFixture.RequireAvailable();
        using var fixture = new Fixture("cat >/dev/null\nprintf '%s' '{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"translated\"}]}'\n",
            new { kind = "ct2", output = "{\"version\":1,\"lines\":[{\"id\":0,\"text\":\"translated\"}]}" });
        // A prior invocation's readiness signal must not crash the next helper.
        // Keep it present: deleting it here would hide the fixture publication bug.
        File.WriteAllText(fixture.ProcessIdPath, "stale prior process");
        using var adapter = new Ct2HelperAdapter();
        for (var i = 0; i < 10; i++)
        {
            var result = await adapter.TranslateAsync(fixture.Reference, "ja", "en", [new(0, "source")], CancellationToken.None);
            Assert.AreEqual("translated", result.Single().Text);
            await adapter.DrainCleanupAsync(CancellationToken.None);
            Assert.IsTrue(int.TryParse(await File.ReadAllTextAsync(fixture.ProcessIdPath), out var processId) && processId > 0);
            Assert.IsFalse(File.Exists(fixture.ProcessIdPath + ".pending"));
        }
    }

    [TestMethod]
    public async Task PackagePinsVerifyManifestAndEveryPayloadBeforeLaunch()
    {
        using var fixture = new Fixture("exit 9");
        using var valid = await Ct2PrivatePackage.OpenAsync(fixture.Reference, "ja", "en", CancellationToken.None);
        Assert.AreEqual(fixture.Executable, valid.Executable);
        Assert.AreNotEqual(Package(HashA).CacheIdentity("ja", "en"), valid.Identity.CacheIdentity("ja", "en"));
        valid.Dispose();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Ct2PrivatePackage.OpenAsync(fixture.Reference with { ManifestSha256 = HashA }, "ja", "en", CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "model", "model.bin"), "tampered");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Ct2PrivatePackage.OpenAsync(fixture.Reference, "ja", "en", CancellationToken.None));
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("/absolute")]
    [DataRow("C:/escape")]
    [DataRow("nested/../../escape")]
    [DataRow("model.bin:stream")]
    [DataRow("CON")]
    [DataRow("model.bin.")]
    public async Task UntrustedManifestPathsAreRejected(string path)
    {
        using var fixture = new Fixture("exit 9");
        fixture.RewriteManifest(data => ((Dictionary<string, object?>[])data["modelFiles"]!)[0]["path"] = path);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Ct2PrivatePackage.OpenAsync(fixture.Reference, "ja", "en", CancellationToken.None));
    }

    [TestMethod]
    public async Task ManifestRejectsSizeDuplicateRouteProtocolAndUnlistedFiles()
    {
        foreach (var mutation in new Action<Dictionary<string, object?>>[]
        {
            d => ((Dictionary<string, object?>[])d["modelFiles"]!)[0]["bytes"] = 999,
            d => ((Dictionary<string, object?>[])d["modelFiles"]!)[0]["sha256"] = HashA,
            d => ((Dictionary<string, object?>[])d["modelFiles"]!)[1]["path"] = "model.bin",
            d => d["sourceLanguage"] = "ko",
            d => d["decoderProtocol"] = "HelsinkiOpus",
            d => d["sourceTokenizer"] = "unlisted.spm",
            d => d["targetPrefix"] = ">>zh<<",
        })
        {
            using var fixture = new Fixture("exit 9");
            fixture.RewriteManifest(mutation);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Ct2PrivatePackage.OpenAsync(fixture.Reference, "ja", "en", CancellationToken.None));
        }
        using var extra = new Fixture("exit 9");
        await File.WriteAllTextAsync(Path.Combine(extra.Root, "engine", "unlisted.dll"), "data");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Ct2PrivatePackage.OpenAsync(extra.Reference, "ja", "en", CancellationToken.None));
    }

    [TestMethod]
    public async Task ManifestRejectsSymlinkPayload()
    {
        WindowsReparseFixture.RequireSymbolicLinks(directory: false);
        using var fixture = new Fixture("exit 9");
        var path = Path.Combine(fixture.Root, "model", "model.bin");
        File.Delete(path);
        File.CreateSymbolicLink(path, Path.Combine(fixture.Root, "model", "source.spm"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Ct2PrivatePackage.OpenAsync(fixture.Reference, "ja", "en", CancellationToken.None));
    }

    [TestMethod]
    public async Task InvalidRequestsFailBeforeOpeningAnyExecutable()
    {
        using var adapter = new Ct2HelperAdapter();
        var missing = new Ct2PackageReference("/does-not-exist", HashA);
        foreach (var lines in new IReadOnlyList<Ct2SourceLine>[]
        {
            [], [new(-1, "a")], [new(0, "a"), new(0, "b")], [new(0, "")], [new(0, "a\nb")],
            [new(0, new string('a', 4097))], [new(0, "\ud800")], [null!],
        })
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => adapter.TranslateAsync(missing, "ja", "en", lines, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => adapter.TranslateAsync(missing, "auto", "en", [new(0, "a")], CancellationToken.None));
    }

    [TestMethod]
    public void ApplyingOutputRejectsRepeatedIndices() => Assert.ThrowsExactly<InvalidDataException>(() =>
        Ct2LyricsOutput.Apply(new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "a", null, [])], LyricsProviderKind.LocalLrc),
            [0, 0], [new(0, "one"), new(0, "two")], "en"));

    [TestMethod]
    public async Task ActiveCleanupIsObservedAndSharedGatePreventsOverlap()
    {
        WindowsProcessFixture.RequireAvailable();
        using var fixture = new Fixture("cat >/dev/null\nprintf ready > ../started\nexec sleep 60\n",
            new { kind = "ct2", ready = true, delayMilliseconds = 60_000 });
        using var adapter = new Ct2HelperAdapter();
        using var cancel = new CancellationTokenSource();
        var running = adapter.TranslateAsync(fixture.Reference, "ja", "en", [new(0, "source")], cancel.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitForStartedAsync(fixture, running);
        var drain = adapter.DrainCleanupAsync(CancellationToken.None);
        Assert.IsFalse(drain.IsCompleted, "Maintenance must observe active work, not only returned cleanup tasks.");
        Assert.IsFalse(await LocalInferenceProcess.InferenceGate.WaitAsync(50, timeout.Token), "All inference backends share the native process boundary.");
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        await drain;
        Assert.IsTrue(await LocalInferenceProcess.InferenceGate.WaitAsync(50, timeout.Token));
        LocalInferenceProcess.InferenceGate.Release();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedStreamsAreKilledAndOwnershipIsReleased(bool stderr)
    {
        WindowsProcessFixture.RequireAvailable();
        using var fixture = new Fixture("cat >/dev/null\nhead -c 262145 /dev/zero" + (stderr ? " >&2" : "") + "\n",
            new { kind = "ct2", stderrBytes = stderr ? 262_145 : 0, stdoutBytes = stderr ? 0 : 262_145 });
        using var adapter = new Ct2HelperAdapter();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => adapter.TranslateAsync(fixture.Reference, "ja", "en", [new(0, "source")], CancellationToken.None));
        await adapter.DrainCleanupAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task NativeRetainedStdinClosesAndReachesActualExit()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows process and Job APIs."); return; }
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::Write([Console]::In.ReadToEnd())" }) start.ArgumentList.Add(argument);
        using var child = LocalInferenceProcess.Start(start, retainStandardInput: true);
        using var observed = System.Diagnostics.Process.GetProcessById(child.Process.Id);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        await child.StandardInput!.WriteAsync("private-stdin");
        child.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await child.Process.WaitForExitAsync(timeout.Token);
        Assert.AreEqual("private-stdin", await output);
        await LocalInferenceProcess.WaitForCleanupAsync(child.CompleteAsync(output, errors), observed.Id);
        Assert.IsTrue(observed.HasExited);
    }

    [TestMethod]
    public async Task VerifiedRunnerRejectsChangedIdentityBeforeProcessLaunch()
    {
        using var fixture = new Fixture("touch must-not-run\n", new { kind = "ct2", marker = true });
        using var verified = await Ct2PrivatePackage.OpenAsync(fixture.Reference, "ja", "en", default);
        using var adapter = new Ct2HelperAdapter();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => adapter.TranslateVerifiedAsync(fixture.Reference,
            verified.Identity with { RuntimeVersion = "unexpected-runtime" }, "ja", "en", [new(0, "source")], default));
        await adapter.DrainCleanupAsync(default);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "engine", "must-not-run")));
    }

    [TestMethod]
    public async Task ReviewedPrivateResolverRevalidatesPayloadAndRefusesUnreviewedRoutes()
    {
        using var fixture = new Fixture("cat >/dev/null\n");
        var resolver = new Ct2PrivatePackageResolver([new("ja", "en", fixture.Reference)]);
        Assert.IsNull(await resolver.ResolveAsync("ko", "en", default));
        var resolved = await resolver.ResolveAsync("ja", "en", default);
        Assert.IsNotNull(resolved);
        Assert.AreEqual(fixture.Reference.ManifestSha256, resolved.Value.Identity.ManifestSha256);
        await File.AppendAllTextAsync(fixture.Executable, "changed");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.ResolveAsync("ja", "en", default));
    }

    private static async Task WaitForStartedAsync(Fixture fixture, Task running)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(fixture.Root, "started")))
        {
            if (running.IsCompleted) { await running; Assert.Fail("The helper exited before its active-work signal."); }
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "dropspace-ct2-test-" + Guid.NewGuid().ToString("N"));
        internal string Executable => Path.Combine(Root, "engine", "helper.exe");
        internal string ProcessIdPath => Path.Combine(Root, "process-id");
        internal Ct2PackageReference Reference { get; private set; } = null!;
        private readonly Dictionary<string, object?> _manifest;
        internal Fixture(string body, object? windowsScenario = null)
        {
            Directory.CreateDirectory(Path.Combine(Root, "engine"));
            Directory.CreateDirectory(Path.Combine(Root, "model"));
            if (OperatingSystem.IsWindows())
            {
                var scenario = JsonSerializer.SerializeToElement(windowsScenario ?? new { kind = "ct2", exitCode = 9 });
                var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(scenario.GetRawText())!;
                properties["pidPath"] = JsonSerializer.SerializeToElement(ProcessIdPath);
                if (scenario.TryGetProperty("ready", out var ready) && ready.GetBoolean())
                    properties["readyPath"] = JsonSerializer.SerializeToElement(Path.Combine(Root, "started"));
                if (scenario.TryGetProperty("marker", out var marker) && marker.GetBoolean())
                    properties["markerPath"] = JsonSerializer.SerializeToElement(Path.Combine(Root, "engine", "must-not-run"));
                WindowsProcessFixture.Write(Executable, properties);
            }
            else
            {
                File.WriteAllText(Executable, "#!/bin/sh\nprintf '%s' \"$$\" > ../process-id\n" + body, new UTF8Encoding(false));
                File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            foreach (var name in new[] { "model.bin", "config.json", "source.spm", "target.spm" }) File.WriteAllText(Path.Combine(Root, "model", name), name);
            _manifest = new()
            {
                ["schemaVersion"] = 1, ["sourceLanguage"] = "ja", ["targetLanguage"] = "en",
                ["tokenizerProtocol"] = "ArgosSentencePiece", ["decoderProtocol"] = "Argos",
                ["engineExecutable"] = "helper.exe", ["sourceTokenizer"] = "source.spm", ["targetTokenizer"] = "target.spm", ["targetPrefix"] = null,
                ["engineFiles"] = Inventory("engine"), ["modelFiles"] = Inventory("model"),
            };
            RewriteManifest(_ => { });
        }
        private Dictionary<string, object?>[] Inventory(string directory) => Directory.GetFiles(Path.Combine(Root, directory))
            .OrderBy(p => p.EndsWith("model.bin", StringComparison.Ordinal) ? 0 : 1).Select(path => new Dictionary<string, object?>
            { ["path"] = Path.GetFileName(path), ["bytes"] = new FileInfo(path).Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
        internal void RewriteManifest(Action<Dictionary<string, object?>> edit)
        {
            edit(_manifest);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_manifest);
            File.WriteAllBytes(Path.Combine(Root, "manifest.json"), bytes);
            Reference = new(Root, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
