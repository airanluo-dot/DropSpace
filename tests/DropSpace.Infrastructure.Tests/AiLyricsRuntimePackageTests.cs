using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AiLyricsRuntimePackageTests
{
    [TestMethod]
    public async Task ExtractsOnlyEmbeddedBytesAndRepairsTamperedCache()
    {
        var directory = NewDirectory();
        try
        {
            byte[] expected = [1, 2, 3, 4];
            var package = Package(directory, expected);
            var path = await package.EnsureExecutableAsync(CancellationToken.None);
            CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(path));
            await File.WriteAllBytesAsync(path, [9, 8, 7, 6]);
            Assert.AreEqual(path, await package.EnsureExecutableAsync(CancellationToken.None));
            CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(1, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RejectsHashMismatchWithoutLeavingExecutableOrPartial()
    {
        var directory = NewDirectory();
        try
        {
            var package = Package(directory, [1, 2, 3], actual: [3, 2, 1]);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => package.EnsureExecutableAsync(CancellationToken.None));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RejectsWrongSourceAndMissingEmbeddedRuntime()
    {
        var directory = NewDirectory();
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                Package(directory, [1], source: new string('0', 40)).EnsureExecutableAsync(CancellationToken.None));
            var missing = new AiLyricsRuntimePackage(_ => null, directory);
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => missing.EnsureExecutableAsync(CancellationToken.None));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CancellationDoesNotPublishRuntime()
    {
        var directory = NewDirectory();
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => Package(directory, [1]).EnsureExecutableAsync(cancelled.Token));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CpuCapabilitySelectsOnlyItsHashVerifiedVariant(bool optimized)
    {
        var directory = NewDirectory();
        try
        {
            byte[] baseline = [1, 2];
            byte[] avx2 = [3, 4, 5];
            var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, runtimeId = AiLyricsRuntimePackage.RuntimeId,
                sourceCommit = AiLyricsRuntimePackage.SourceCommit, executable = "llama-completion.exe",
                sha256 = Convert.ToHexString(SHA256.HashData(baseline)), bytes = baseline.Length,
                avx2 = new { executable = "llama-completion-avx2.exe", sha256 = Convert.ToHexString(SHA256.HashData(avx2)), bytes = avx2.Length },
            }));
            var package = new AiLyricsRuntimePackage(name => new MemoryStream(name switch
            {
                AiLyricsRuntimePackage.ManifestResourceName => manifest,
                AiLyricsRuntimePackage.ExecutableResourceName => baseline,
                AiLyricsRuntimePackage.Avx2ExecutableResourceName => avx2,
                _ => throw new InvalidOperationException(),
            }, false), directory, optimized);
            var path = await package.EnsureExecutableAsync(CancellationToken.None);
            CollectionAssert.AreEqual(optimized ? avx2 : baseline, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DropSpace-runtime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static AiLyricsRuntimePackage Package(string directory, byte[] expected, byte[]? actual = null, string? source = null)
    {
        var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, runtimeId = AiLyricsRuntimePackage.RuntimeId,
            sourceCommit = source ?? AiLyricsRuntimePackage.SourceCommit, executable = "llama-completion.exe",
            sha256 = Convert.ToHexString(SHA256.HashData(expected)), bytes = expected.Length,
        }));
        return new AiLyricsRuntimePackage(name => name switch
        {
            AiLyricsRuntimePackage.ManifestResourceName => new MemoryStream(manifest, false),
            AiLyricsRuntimePackage.ExecutableResourceName => new MemoryStream(actual ?? expected, false),
            _ => null,
        }, directory);
    }
}
