using DropSpace.App.Services.NeteaseEnhancement;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NeteaseDeploymentTests
{
    [TestMethod]
    public async Task InstallCommitRestartReceiptRemoveOnlyOwnedFiles()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare("first");
        string sentinel = Path.Combine(prepared.ProfilePath, "user-settings.json");
        Directory.CreateDirectory(prepared.ProfilePath);
        await File.WriteAllTextAsync(sentinel, "keep");
        using (var service = fixture.Service())
        {
            var receipt = await service.InstallAsync(prepared);
            Assert.IsFalse(receipt.Committed);
            await service.CommitAsync(receipt);
        }
        using (var service = fixture.Service())
        {
            Assert.IsTrue((await service.GetManagedReceiptAsync(prepared.Installation))!.Committed);
            await service.RemoveAsync(prepared.Installation);
            Assert.IsNull(await service.GetManagedReceiptAsync(prepared.Installation));
        }
        Assert.AreEqual("keep", await File.ReadAllTextAsync(sentinel));
        Assert.IsFalse(File.Exists(fixture.Loader));
    }

    [TestMethod]
    public async Task UnknownLoaderNeverOverwritten()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare("first");
        await File.WriteAllTextAsync(fixture.Loader, "user loader");
        using var service = fixture.Service();
        var error = await Assert.ThrowsExactlyAsync<EnhancementDeploymentException>(() => service.InstallAsync(prepared));
        Assert.AreEqual("ExistingUnmanagedFile", error.Code);
        Assert.AreEqual("user loader", await File.ReadAllTextAsync(fixture.Loader));
        Assert.IsNull(await service.GetManagedReceiptAsync(prepared.Installation));
    }

    [TestMethod]
    public async Task UpdateRollbackRestoresPreviousFilesAndOwnership()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var first = fixture.Prepare("first");
        await service.CommitAsync(await service.InstallAsync(first));
        var second = fixture.Prepare("second");
        var update = await service.InstallAsync(second);
        await service.RollbackAsync(update);
        Assert.AreEqual("first-loader", await File.ReadAllTextAsync(fixture.Loader));
        Assert.AreEqual("first", (await service.GetManagedReceiptAsync(first.Installation))!.PluginVersion);
        await service.RemoveAsync(first.Installation);
        Assert.IsFalse(File.Exists(fixture.Loader));
    }

    [TestMethod]
    public async Task TamperedOwnedFilePreventsAnyRemoval()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = fixture.Prepare("first");
        await service.CommitAsync(await service.InstallAsync(prepared));
        string plugin = Path.Combine(prepared.ProfilePath, "plugins", "InfLink-rs.plugin");
        await File.WriteAllTextAsync(plugin, "changed by user");
        var error = await Assert.ThrowsExactlyAsync<EnhancementDeploymentException>(() => service.RemoveAsync(prepared.Installation));
        Assert.AreEqual("ManagedFileChanged", error.Code);
        Assert.IsTrue(File.Exists(fixture.Loader));
        Assert.AreEqual("changed by user", await File.ReadAllTextAsync(plugin));
    }

    [TestMethod]
    public async Task ExistingIdenticalLoaderIsBorrowedAndPreserved()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = fixture.Prepare("first");
        File.Copy(prepared.LoaderPath, fixture.Loader);
        var receipt = await service.InstallAsync(prepared);
        Assert.IsFalse(receipt.Files[0].Owned);
        await service.CommitAsync(receipt);
        await service.RemoveAsync(prepared.Installation);
        Assert.IsTrue(File.Exists(fixture.Loader));
    }

    [TestMethod]
    public async Task ChangedPreparedBytesAreRejectedBeforeMutation()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = fixture.Prepare("first");
        await File.WriteAllTextAsync(prepared.PluginPath, "tampered");
        var error = await Assert.ThrowsExactlyAsync<EnhancementDeploymentException>(() => service.InstallAsync(prepared));
        Assert.AreEqual("HashMismatch", error.Code);
        Assert.IsFalse(File.Exists(fixture.Loader));
    }

    [TestMethod]
    public async Task PlayerVersionUpgradeDoesNotLoseManagedReceipt()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = fixture.Prepare("first");
        await service.CommitAsync(await service.InstallAsync(prepared));
        var upgraded = prepared.Installation with { Version = new Version(3, 2, 0) };
        Assert.IsNotNull(await service.GetManagedReceiptAsync(upgraded));
        await service.RemoveAsync(upgraded);
        Assert.IsFalse(File.Exists(fixture.Loader));
    }

    [TestMethod]
    public async Task UncommittedInstallCanBeRecoveredAfterServiceRestart()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare("first");
        using (var service = fixture.Service()) await service.InstallAsync(prepared);
        using (var service = fixture.Service())
        {
            var receipt = await service.GetManagedReceiptAsync(prepared.Installation);
            Assert.IsNotNull(receipt);
            Assert.IsFalse(receipt.Committed);
            await service.RollbackAsync(receipt);
            Assert.IsNull(await service.GetManagedReceiptAsync(prepared.Installation));
        }
        Assert.IsFalse(File.Exists(fixture.Loader));
    }

    [TestMethod]
    public async Task ReparseDestinationRejectedWithoutTouchingOutside()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var prepared = fixture.Prepare("first");
        Directory.CreateDirectory(prepared.ProfilePath);
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(prepared.ProfilePath, "plugins"), outside);
        var error = await Assert.ThrowsExactlyAsync<EnhancementDeploymentException>(() => service.InstallAsync(prepared));
        Assert.AreEqual("UnsafePath", error.Code);
        Assert.AreEqual(0, Directory.GetFiles(outside).Length);
        Directory.Delete(Path.Combine(prepared.ProfilePath, "plugins"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DropSpace-NeteaseTests-" + Guid.NewGuid().ToString("N"));
        public string Loader => Path.Combine(Root, "player", "msimg32.dll");
        public Fixture() { Directory.CreateDirectory(Path.Combine(Root, "player")); }
        public InfLinkDeploymentService Service() => new(Path.Combine(Root, "state"));
        public PreparedInfLinkDeployment Prepare(string version)
        {
            string staging = Path.Combine(Root, "staging", version);
            Directory.CreateDirectory(staging);
            string loader = Path.Combine(staging, "loader"), plugin = Path.Combine(staging, "plugin");
            File.WriteAllText(loader, version + "-loader");
            File.WriteAllText(plugin, version + "-plugin");
            return new(new NeteaseInstallation(Path.Combine(Root, "player", "cloudmusic.exe"), new Version(3, 1, 40), Architecture.X64), Path.Combine(Root, "profile"), loader, plugin, Hash(loader), Hash(plugin), version);
        }
        private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        public void Dispose() { Directory.Delete(Root, true); }
    }
}
