using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ResidentRuntimePackageTests
{
    [TestMethod]
    [DataRow(false, false, "plain-lyrics-worker.exe")]
    [DataRow(true, false, "plain-lyrics-worker-avx2.exe")]
    [DataRow(false, true, "plain-lyrics-worker-vulkan.exe")]
    [DataRow(true, true, "plain-lyrics-worker-vulkan.exe")]
    public async Task SelectsOnlyManifestBoundRequestedResidentVariant(bool avx2, bool gpu, string expectedName)
    {
        using var fixture = new Fixture();
        var package = fixture.Package(avx2);
        var path = await package.EnsureResidentWorkerAsync(gpu, CancellationToken.None);
        Assert.AreEqual(expectedName, Path.GetFileName(path));
        CollectionAssert.AreEqual(fixture.Resources[expectedName], await File.ReadAllBytesAsync(path));
        CollectionAssert.AreEqual(new[] { AiLyricsRuntimePackage.ManifestResourceName, "DropSpace.AiLyricsRuntime." + expectedName },
            fixture.OpenedResources);
        Assert.AreEqual(Path.Combine(fixture.Root, AiLyricsRuntimePackage.RuntimeId,
            Convert.ToHexStringLower(SHA256.HashData(fixture.Resources[expectedName])), expectedName), path);
        fixture.OpenedResources.Clear();
        Assert.AreEqual(path, await package.EnsureResidentWorkerAsync(gpu, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { AiLyricsRuntimePackage.ManifestResourceName }, fixture.OpenedResources,
            "A verified cache should reuse its exact bytes without opening another embedded executable.");
    }

    [TestMethod]
    public async Task RepairsSameLengthTamperingAndTruncationFromEmbeddedBytes()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        var path = await package.EnsureResidentWorkerAsync(false, CancellationToken.None);
        foreach (var corruption in new byte[][] { [9, 9, 9], [1] })
        {
            await File.WriteAllBytesAsync(path, corruption);
            Assert.AreEqual(path, await package.EnsureResidentWorkerAsync(false, CancellationToken.None));
            CollectionAssert.AreEqual(fixture.Resources["plain-lyrics-worker.exe"], await File.ReadAllBytesAsync(path));
            Assert.HasCount(0, Directory.GetFiles(fixture.Root, "*.partial", SearchOption.AllDirectories));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CorruptEmbeddedWorkerFailsClosedWithoutLeavingPartialFiles(bool wrongLength)
    {
        using var fixture = new Fixture();
        fixture.Resources["plain-lyrics-worker.exe"] = wrongLength ? [7] : [9, 8, 7];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Package().EnsureResidentWorkerAsync(false, CancellationToken.None));
        Assert.HasCount(0, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task MissingGpuResourceNeverSubstitutesCpuPackage()
    {
        using var fixture = new Fixture();
        fixture.Resources.Remove("plain-lyrics-worker-vulkan.exe");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => fixture.Package().EnsureResidentWorkerAsync(true, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { AiLyricsRuntimePackage.ManifestResourceName, "DropSpace.AiLyricsRuntime.plain-lyrics-worker-vulkan.exe" },
            fixture.OpenedResources);
        Assert.HasCount(0, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    [DataRow("schemaVersion", "2")]
    [DataRow("runtimeId", "\"untrusted-runtime\"")]
    [DataRow("sourceCommit", "\"untrusted-source\"")]
    [DataRow("resident.protocol", "2")]
    [DataRow("resident.profile", "\"other-profile\"")]
    [DataRow("resident.cpu.executable", "\"../outside.exe\"")]
    [DataRow("resident.cpu.executable", "\"plain-lyrics-worker-vulkan.exe\"")]
    [DataRow("resident.cpu.sha256", "null")]
    [DataRow("resident.cpu.sha256", "\"bad-hash\"")]
    [DataRow("resident.cpu.sha256", "\"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\"")]
    [DataRow("resident.cpu.bytes", "0")]
    [DataRow("resident.cpu.bytes", "-1")]
    [DataRow("resident.cpu.bytes", "536870913")]
    public async Task RejectsUntrustedMetadataBeforeOpeningExecutable(string property, string json)
    {
        using var fixture = new Fixture();
        var segments = property.Split('.');
        JsonNode parent = fixture.Manifest;
        foreach (var segment in segments[..^1]) parent = parent[segment]!;
        parent[segments[^1]] = JsonNode.Parse(json);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Package().EnsureResidentWorkerAsync(false, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { AiLyricsRuntimePackage.ManifestResourceName }, fixture.OpenedResources);
        Assert.HasCount(0, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task PrecancelledExtractionDoesNotReadResourcesOrCreateFiles()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Package().EnsureResidentWorkerAsync(true, cancel.Token));
        Assert.HasCount(0, fixture.OpenedResources);
        Assert.HasCount(0, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "DropSpace-resident-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Manifest = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["runtimeId"] = AiLyricsRuntimePackage.RuntimeId,
                ["sourceCommit"] = AiLyricsRuntimePackage.SourceCommit,
                ["resident"] = new JsonObject
                {
                    ["protocol"] = 1,
                    ["profile"] = "hy-q8-plain-resident-v1",
                    ["cpu"] = Metadata("plain-lyrics-worker.exe"),
                    ["avx2"] = Metadata("plain-lyrics-worker-avx2.exe"),
                    ["vulkan"] = Metadata("plain-lyrics-worker-vulkan.exe"),
                },
            };
        }

        internal string Root { get; }
        internal JsonObject Manifest { get; }
        internal Dictionary<string, byte[]> Resources { get; } = new(StringComparer.Ordinal)
        {
            ["plain-lyrics-worker.exe"] = [1, 2, 3],
            ["plain-lyrics-worker-avx2.exe"] = [4, 5, 6],
            ["plain-lyrics-worker-vulkan.exe"] = [7, 8, 9],
        };
        internal List<string> OpenedResources { get; } = [];

        private JsonObject Metadata(string name) => new()
        {
            ["executable"] = name,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(Resources[name])),
            ["bytes"] = Resources[name].Length,
        };

        internal AiLyricsRuntimePackage Package(bool avx2 = false) => new(name =>
        {
            OpenedResources.Add(name);
            if (name == AiLyricsRuntimePackage.ManifestResourceName)
                return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(Manifest), writable: false);
            const string prefix = "DropSpace.AiLyricsRuntime.";
            return name.StartsWith(prefix, StringComparison.Ordinal) && Resources.TryGetValue(name[prefix.Length..], out var bytes)
                ? new MemoryStream(bytes, writable: false) : null;
        }, Root, avx2);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
