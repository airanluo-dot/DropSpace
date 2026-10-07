using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class CudaLyricsRuntimePackageTests
{
    [TestMethod]
    public async Task ExtractsAndReverifiesEveryCudaDependencyWithIndependentIdentity()
    {
        using var fixture = new Fixture();
        var identity = fixture.Package.GetManifestCacheIdentity();
        var path = await fixture.Package.EnsureWorkerAsync(default);
        StringAssert.Contains(path, CudaLyricsRuntimePackage.RuntimeId);
        StringAssert.Contains(path, identity);
        foreach (var name in fixture.Files.Keys)
        {
            var dependency = Path.Combine(Path.GetDirectoryName(path)!, name);
            CollectionAssert.AreEqual(fixture.Files[name], File.ReadAllBytes(dependency));
            File.WriteAllBytes(dependency, [9, 9, 9]);
            await fixture.Package.EnsureWorkerAsync(default);
            CollectionAssert.AreEqual(fixture.Files[name], File.ReadAllBytes(dependency));
        }
        Assert.HasCount(0, Directory.GetFiles(fixture.Root, "*.partial", SearchOption.AllDirectories));
    }

    [TestMethod]
    [DataRow("backend", "\"vulkan\"")]
    [DataRow("runtimeId", "\"llama-cpp-v0.5.0-cpu-win-x64\"")]
    [DataRow("sourceCommit", "\"changed\"")]
    [DataRow("profile", "\"changed-sampling\"")]
    [DataRow("workerSourceSha256", "\"bad\"")]
    public async Task RejectsWrongVariantBeforeOpeningPayload(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Manifest[key] = JsonNode.Parse(value);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Package.EnsureWorkerAsync(default));
        Assert.IsTrue(fixture.Opened.All(x => x == CudaLyricsRuntimePackage.ManifestResourceName));
    }

    [TestMethod]
    [DataRow("name", "\"../outside.dll\"")]
    [DataRow("name", "\"plain-lyrics-worker-vulkan.exe\"")]
    [DataRow("sha256", "null")]
    [DataRow("bytes", "805306369")]
    public async Task RejectsUntrustedDependencyBeforeOpeningWorker(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Manifest["files"]![2]![key] = JsonNode.Parse(value);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Package.EnsureWorkerAsync(default));
        Assert.HasCount(1, fixture.Opened);
    }

    [TestMethod]
    public async Task CorruptOrMissingDllCannotReturnAnExecutableOrLeavePartials()
    {
        using var fixture = new Fixture();
        fixture.Files["cublasLt64_13.dll"] = [9, 9, 9];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Package.EnsureWorkerAsync(default));
        fixture.Files.Remove("cublasLt64_13.dll");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => fixture.Package.EnsureWorkerAsync(default));
        Assert.HasCount(0, Directory.GetFiles(fixture.Root, "*.partial", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task PrecancelledExtractionDoesNotOpenResources()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Package.EnsureWorkerAsync(cancel.Token));
        Assert.HasCount(0, fixture.Opened);
    }

    [TestMethod]
    public async Task IndependentComponentIsReusedAcrossExactAppBindings()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(123L, fixture.Package.DownloadBytes);
        var installed = await fixture.Package.EnsureWorkerAsync(default);
        var identity = fixture.Package.GetManifestCacheIdentity();
        fixture.Opened.Clear();
        fixture.Descriptor["appRelease"]!["tag"] = "v0.3.1-beta.17";
        fixture.Descriptor["appRelease"]!["sourceCommit"] = new string('b', 40);
        var next = fixture.CreatePackage("v0.3.1-beta.17", new string('b', 40));
        Assert.AreEqual(123L, next.DownloadBytes);
        Assert.AreEqual(identity, next.GetManifestCacheIdentity());
        Assert.AreEqual(installed, await next.EnsureWorkerAsync(default));
        Assert.IsTrue(fixture.Opened.All(name => name == CudaLyricsRuntimePackage.ManifestResourceName ||
            name == CudaLyricsRuntimePackage.DownloadResourceName));
    }

    [TestMethod]
    [DataRow("schemaVersion", "1")]
    [DataRow("componentRelease.tag", "\"cuda-other-v1\"")]
    [DataRow("componentSourceCommit", "\"0000000000000000000000000000000000000000\"")]
    [DataRow("appRelease.tag", "\"v0.3.1-beta.17\"")]
    [DataRow("appRelease.sourceCommit", "\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"")]
    [DataRow("protocol", "2")]
    [DataRow("profile", "\"changed-profile\"")]
    [DataRow("download.name", "\"DropSpace-CUDA-win-x64-v0.3.1-beta.16.zip\"")]
    [DataRow("download.url", "\"https://github.com/other/DropSpace/releases/download/cuda-other/asset.zip\"")]
    [DataRow("download.sha256", "\"bad\"")]
    [DataRow("download.bytes", "0")]
    [DataRow("download.bytes", "1073741825")]
    [DataRow("manifest.sha256", "\"0000000000000000000000000000000000000000000000000000000000000000\"")]
    [DataRow("manifest.bytes", "1")]
    public void RejectsUntrustedDescriptorBeforeAnyDownload(string key, string value)
    {
        using var fixture = new Fixture();
        var keys = key.Split('.');
        var target = keys.Length == 1 ? fixture.Descriptor : fixture.Descriptor[keys[0]]!.AsObject();
        target[keys[^1]] = JsonNode.Parse(value);
        Assert.ThrowsExactly<InvalidDataException>(() => _ = fixture.Package.DownloadBytes);
    }

    [TestMethod]
    [DataRow("?redirect=1")]
    [DataRow("#fragment")]
    public void RejectsModifiedOfficialComponentUrl(string suffix)
    {
        using var fixture = new Fixture();
        fixture.Descriptor["download"]!["url"] = fixture.Descriptor["download"]!["url"]!.GetValue<string>() + suffix;
        Assert.ThrowsExactly<InvalidDataException>(() => _ = fixture.Package.DownloadBytes);
    }

    private sealed class Fixture : IDisposable
    {
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal)
        {
            [CudaLyricsRuntimePackage.ExecutableName] = [1, 2, 3],
            ["cublas64_13.dll"] = [4, 5, 6],
            ["cublasLt64_13.dll"] = [7, 8, 9],
        };
        internal JsonObject Manifest { get; }
        internal JsonObject Descriptor { get; }
        internal List<string> Opened { get; } = [];
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "DropSpace-cuda-" + Guid.NewGuid().ToString("N"));
        internal CudaLyricsRuntimePackage Package { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Manifest = new JsonObject
            {
                ["schemaVersion"] = 1, ["runtimeId"] = CudaLyricsRuntimePackage.RuntimeId,
                ["sourceCommit"] = AiLyricsRuntimePackage.SourceCommit,
                ["sourceRepository"] = "https://github.com/ggml-org/llama.cpp",
                ["backend"] = "cuda", ["protocol"] = 1, ["profile"] = PersistentPlainLyricsRunner.ResidentProfileId,
                ["workerSourceSha256"] = new string('a', 64),
                ["files"] = new JsonArray(Files.Select(x => (JsonNode)new JsonObject
                {
                    ["name"] = x.Key, ["bytes"] = x.Value.Length,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(x.Value)),
                }).ToArray()),
            };
            var raw = System.Text.Encoding.UTF8.GetBytes(Manifest.ToJsonString());
            Descriptor = new JsonObject
            {
                ["schemaVersion"] = 2, ["repository"] = "airanluo-dot/DropSpace",
                ["componentRelease"] = new JsonObject { ["tag"] = CudaLyricsRuntimePackage.ComponentReleaseTag },
                ["componentSourceCommit"] = "806f3e3e40c11a6e7d3d50648a9de8708b16b4ac",
                ["appRelease"] = new JsonObject { ["tag"] = "v0.3.1-beta.16", ["sourceCommit"] = new string('a', 40) },
                ["runtimeId"] = CudaLyricsRuntimePackage.RuntimeId,
                ["backend"] = "cuda", ["platform"] = "win-x64", ["protocol"] = 1,
                ["profile"] = PersistentPlainLyricsRunner.ResidentProfileId,
                ["engineSourceCommit"] = AiLyricsRuntimePackage.SourceCommit,
                ["workerSourceSha256"] = new string('a', 64),
                ["download"] = new JsonObject
                {
                    ["name"] = CudaLyricsRuntimePackage.ArchiveName, ["bytes"] = 123,
                    ["sha256"] = new string('c', 64),
                    ["url"] = "https://github.com/airanluo-dot/DropSpace/releases/download/" +
                        CudaLyricsRuntimePackage.ComponentReleaseTag + "/" + CudaLyricsRuntimePackage.ArchiveName,
                },
                ["manifest"] = new JsonObject
                {
                    ["name"] = "cuda-runtime-manifest.json", ["bytes"] = raw.Length,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(raw)),
                },
                ["files"] = Manifest["files"]!.DeepClone(),
                ["notices"] = new JsonArray(new[] { "LICENSE-llama.cpp", "LICENSE-CUDA.txt" }.Select(name =>
                    (JsonNode)new JsonObject { ["name"] = name, ["bytes"] = 1, ["sha256"] = new string('d', 64) }).ToArray()),
            };
            Package = CreatePackage("v0.3.1-beta.16", new string('a', 40));
        }
        internal CudaLyricsRuntimePackage CreatePackage(string appTag, string appCommit) => new(name =>
        {
            Opened.Add(name);
            if (name == CudaLyricsRuntimePackage.ManifestResourceName)
                return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Manifest.ToJsonString()));
            if (name == CudaLyricsRuntimePackage.DownloadResourceName)
                return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Descriptor.ToJsonString()));
            return Files.TryGetValue(name[CudaLyricsRuntimePackage.ResourcePrefix.Length..], out var bytes)
                ? new MemoryStream(bytes) : null;
        }, Root, appTag: appTag, appCommit: appCommit);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
