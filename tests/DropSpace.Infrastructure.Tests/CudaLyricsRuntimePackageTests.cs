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
    [DataRow("bytes", "536870913")]
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
        fixture.Files["cublasLt64_12.dll"] = [9, 9, 9];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Package.EnsureWorkerAsync(default));
        fixture.Files.Remove("cublasLt64_12.dll");
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

    private sealed class Fixture : IDisposable
    {
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal)
        {
            [CudaLyricsRuntimePackage.ExecutableName] = [1, 2, 3],
            ["cublas64_12.dll"] = [4, 5, 6],
            ["cublasLt64_12.dll"] = [7, 8, 9],
        };
        internal JsonObject Manifest { get; }
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
            Package = new CudaLyricsRuntimePackage(name =>
            {
                Opened.Add(name);
                if (name == CudaLyricsRuntimePackage.ManifestResourceName)
                    return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Manifest.ToJsonString()));
                return Files.TryGetValue(name[CudaLyricsRuntimePackage.ResourcePrefix.Length..], out var bytes)
                    ? new MemoryStream(bytes) : null;
            }, Root);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
