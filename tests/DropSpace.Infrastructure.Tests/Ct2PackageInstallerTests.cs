using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Ct2PackageInstallerTests
{
    private const long FourGiB = 4L * 1024 * 1024 * 1024;
    private const string WrongHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public async Task ValidManifestAndNonSeekableArchiveInstallAtomicallyWithoutTemporaryFiles()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        var observedPrivateArchive = false;
        using var source = new ObservedStream(bytes, () =>
        {
            observedPrivateArchive = Directory.EnumerateFiles(fixture.Root, "*.archive.tmp").Any();
            Assert.IsFalse(Directory.Exists(fixture.PackageRoot), "The final package cannot appear during download.");
        }, chunkSize: 7);
        var reference = await fixture.Installer.InstallAsync(fixture.Catalog(bytes), source, CancellationToken.None);
        Assert.IsTrue(observedPrivateArchive, "Download must spool to a sibling file rather than buffer the whole ZIP.");
        Assert.IsTrue(source.CanRead, "The caller owns the input stream.");
        Assert.AreEqual(fixture.PackageRoot, reference.PrivateRoot);
        Assert.HasCount(1, Directory.GetFileSystemEntries(fixture.Root));
        foreach (var file in fixture.Files)
            CollectionAssert.AreEqual(file.Value, await File.ReadAllBytesAsync(Path.Combine(reference.PrivateRoot, file.Key)));
        using var verified = await Ct2PrivatePackage.OpenAsync(reference, "ja", "en", CancellationToken.None);
        Assert.AreEqual(Path.Combine(reference.PrivateRoot, "engine", "helper.exe"), verified.Executable);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    [DataRow(0)]
    public async Task ArchiveSizeAndDigestMustMatchReviewedCatalog(int sizeAdjustment)
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        var catalog = fixture.Catalog(bytes);
        catalog = sizeAdjustment == 0 ? catalog with { ArchiveSha256 = WrongHash } :
            catalog with { ArchiveBytes = bytes.Length + sizeAdjustment };
        await RejectAsync(fixture, catalog, bytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ComponentSizeAndDigestMustMatchReviewedCatalog(bool wrongSize)
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        var catalog = fixture.Catalog(bytes);
        catalog = catalog with { Files = catalog.Files.Select(f => f.Path == "model/model.bin" ?
            wrongSize ? f with { Bytes = f.Bytes + 1 } : f with { Sha256 = WrongHash } : f).ToArray() };
        await RejectAsync(fixture, catalog, bytes);
    }

    [TestMethod]
    public async Task EntryCannotExpandPastItsDeclaredAndReviewedSize()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        // The ZIP directory lies about the stored member's uncompressed length. Its four
        // actual bytes must still be bounded while reading, not trusted from Entry.Length.
        for (var offset = 0; offset <= bytes.Length - 46; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) != 0x02014b50) continue;
            var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28, 2));
            if (Encoding.UTF8.GetString(bytes, offset + 46, nameBytes) != "model/model.bin") continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24, 4), 3);
            break;
        }
        var catalog = fixture.Catalog(bytes);
        catalog = catalog with { Files = catalog.Files.Select(f => f.Path == "model/model.bin" ?
            f with { Bytes = 3, Sha256 = Hash([1, 2, 3]) } : f).ToArray() };
        await RejectAsync(fixture, catalog, bytes);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("extra")]
    [DataRow("duplicate")]
    [DataRow("case-collision")]
    public async Task ArchiveInventoryRejectsMissingExtraAndDuplicateFiles(string kind)
    {
        using var fixture = new Fixture();
        var entries = fixture.Entries();
        switch (kind)
        {
            case "missing": entries.RemoveAt(1); break;
            case "extra": entries.Add(new("engine/unexpected.dll", [1, 2, 3])); break;
            case "duplicate": entries[1] = entries[0]; break;
            case "case-collision": entries[1] = entries[0] with { Path = entries[0].Path.ToUpperInvariant() }; break;
        }
        var bytes = Fixture.Archive(entries);
        await RejectAsync(fixture, fixture.Catalog(bytes), bytes);
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("/absolute")]
    [DataRow("C:/escape")]
    [DataRow("model/../../escape")]
    [DataRow("model\\escape")]
    [DataRow("model//escape")]
    [DataRow("model/./escape")]
    [DataRow("model/model.bin:stream")]
    [DataRow("model/CON.txt")]
    [DataRow("model/LPT1")]
    [DataRow("model/model.bin.")]
    [DataRow("model/model.bin ")]
    [DataRow("model/")]
    [DataRow("model/é")]
    [DataRow("model/\0escape")]
    [DataRow("MODEL/model.bin")]
    [DataRow("model/MODEL.BIN")]
    public async Task RawZipNamesAreValidatedWithoutCanonicalizing(string path)
    {
        using var fixture = new Fixture();
        var entries = fixture.Entries();
        entries[1] = entries[1] with { Path = path };
        var bytes = Fixture.Archive(entries);
        await RejectAsync(fixture, fixture.Catalog(bytes), bytes);
    }

    [TestMethod]
    [DataRow(0xA000, 0)] // Symbolic link.
    [DataRow(0x6000, 0)] // Block device.
    [DataRow(0x2000, 0)] // Character device.
    [DataRow(0x1000, 0)] // FIFO.
    [DataRow(0xC000, 0)] // Socket.
    [DataRow(0x4000, 0)] // Directory, including one disguised as a file.
    [DataRow(0, 0x400)] // Windows reparse point.
    [DataRow(0, 0x10)] // Windows directory attribute.
    public async Task ArchiveRejectsAllNonRegularEntryTypes(int unixType, int windowsAttributes)
    {
        using var fixture = new Fixture();
        var entries = fixture.Entries();
        entries[1] = entries[1] with { Attributes = (unixType << 16) | windowsAttributes };
        var bytes = Fixture.Archive(entries);
        await RejectAsync(fixture, fixture.Catalog(bytes), bytes);
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("case")]
    [DataRow("file-parent")]
    [DataRow("parent-file")]
    [DataRow("directory-case")]
    [DataRow("outside-layout")]
    [DataRow("traversal")]
    [DataRow("too-deep")]
    [DataRow("too-long")]
    public async Task CatalogRejectsPathAndDirectoryCollisionsBeforeReading(string kind)
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        var catalog = fixture.Catalog(bytes);
        var files = catalog.Files.ToList();
        var payload = files.First(f => f.Path == "engine/helper.exe");
        switch (kind)
        {
            case "duplicate": files.Add(payload); break;
            case "case": files.Add(payload with { Path = "engine/HELPER.EXE" }); break;
            case "file-parent": files.Add(payload with { Path = "engine/helper.exe/nested.dll" }); break;
            case "parent-file": files.Insert(0, payload with { Path = "engine/helper.exe/nested.dll" }); break;
            case "directory-case":
                files.Add(payload with { Path = "engine/A/first.dll" });
                files.Add(payload with { Path = "engine/a/second.dll" });
                break;
            case "outside-layout": files.Add(payload with { Path = "unreviewed.dll" }); break;
            case "traversal": files.Add(payload with { Path = "model/../escape" }); break;
            case "too-deep": files.Add(payload with { Path = "model/a/b/c/d/e/f/g/h/file.bin" }); break;
            case "too-long": files.Add(payload with { Path = "model/" + string.Join('/', Enumerable.Repeat(new string('a', 80), 3)) }); break;
        }
        using var source = new ObservedStream(bytes, () => Assert.Fail("Invalid catalogs must fail before reading."));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(catalog with { Files = files }, source, CancellationToken.None));
        fixture.AssertNoOutput();
    }

    [TestMethod]
    public async Task CatalogRejectsInvalidSizesHashesInventoryAndMetadataBeforeReading()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        var valid = fixture.Catalog(bytes);
        var manifest = valid.Files.Single(f => f.Path == "manifest.json");
        var payload = valid.Files.First(f => f.Path != "manifest.json");
        Ct2PackageCatalogEntry[] invalid =
        [
            valid with { PackageId = null! }, valid with { PackageId = "" }, valid with { PackageId = "../escape" },
            valid with { PackageId = "CON" }, valid with { SourceLanguage = null! },
            valid with { SourceLanguage = "ja-JP" }, valid with { TargetLanguage = "ja" },
            valid with { ArchiveBytes = 0 }, valid with { ArchiveBytes = FourGiB + 1 },
            valid with { ArchiveSha256 = "0" }, valid with { ArchiveSha256 = new string('g', 64) },
            valid with { ManifestSha256 = null! }, valid with { ManifestSha256 = WrongHash },
            valid with { Files = null! }, valid with { Files = [] }, valid with { Files = [null!] },
            valid with { Files = Enumerable.Repeat(payload, 4097).ToArray() },
            valid with { Files = [payload] }, valid with { Files = [manifest with { Bytes = 1024 * 1024 + 1 }] },
            valid with { Files = [manifest, payload with { Bytes = 0 }] },
            valid with { Files = [manifest, payload with { Bytes = -1 }] },
            valid with { Files = [manifest, payload with { Bytes = long.MaxValue }] },
            valid with { Files = [manifest, payload with { Bytes = FourGiB }] },
            valid with { Files = [manifest, payload with { Sha256 = null! }] },
            valid with { Files = [manifest, payload with { Path = null! }] },
        ];
        foreach (var catalog in invalid)
        {
            using var source = new ObservedStream(bytes, () => Assert.Fail("Invalid catalogs must fail before reading."));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(catalog, source, CancellationToken.None));
            fixture.AssertNoOutput();
        }
    }

    [TestMethod]
    public async Task ArchiveBudgetAllowsFourGiBWithoutAllocatingItInMemory()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        using var source = new ObservedStream(bytes);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(
            fixture.Catalog(bytes) with { ArchiveBytes = FourGiB }, source, CancellationToken.None));
        Assert.IsTrue(source.ReadCalls > 0, "The 4 GiB catalog limit must not retain the old int.MaxValue memory restriction.");
        fixture.AssertNoOutput();
    }

    [TestMethod]
    public async Task ValidZipWithInvalidPinnedManifestCannotPublish()
    {
        using var fixture = new Fixture();
        fixture.Files["manifest.json"] = Encoding.UTF8.GetBytes("{\"schemaVersion\":99}");
        var bytes = fixture.Archive();
        await RejectAsync(fixture, fixture.Catalog(bytes), bytes);
    }

    [TestMethod]
    public async Task CatalogInventoryIsSnapshottedBeforeAwaitingUntrustedStream()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        var files = fixture.Catalog(bytes).Files.ToList();
        using var source = new ObservedStream(bytes, () => files.Clear());
        await fixture.Installer.InstallAsync(fixture.Catalog(bytes) with { Files = files }, source, CancellationToken.None);
        Assert.IsTrue(Directory.Exists(fixture.PackageRoot));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExistingPackageOrFileIsPreservedWithoutReadingArchive(bool existingFile)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        var marker = fixture.PackageRoot;
        if (!existingFile)
        {
            Directory.CreateDirectory(fixture.PackageRoot);
            marker = Path.Combine(fixture.PackageRoot, "keep.txt");
        }
        await File.WriteAllTextAsync(marker, "existing package must survive");
        var bytes = fixture.Archive();
        using var source = new ObservedStream(bytes, () => Assert.Fail("An existing installation must be left untouched."));
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Installer.InstallAsync(fixture.Catalog(bytes), source, CancellationToken.None));
        Assert.AreEqual("existing package must survive", await File.ReadAllTextAsync(marker));
        Assert.HasCount(1, Directory.GetFileSystemEntries(fixture.Root));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationRollsBackAndReleasesGate(bool beforeRead)
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        using var cancel = new CancellationTokenSource();
        if (beforeRead) cancel.Cancel();
        using var source = new ObservedStream(bytes, () => cancel.Cancel(), chunkSize: 7);
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Installer.InstallAsync(fixture.Catalog(bytes), source, cancel.Token));
        fixture.AssertNoOutput();
        using var retry = new MemoryStream(bytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.Installer.InstallAsync(fixture.Catalog(bytes), retry, timeout.Token);
        Assert.IsTrue(Directory.Exists(fixture.PackageRoot));
    }

    [TestMethod]
    public async Task DifferentReviewedIdentityInstallsBesideExistingPackage()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        var catalog = fixture.Catalog(bytes);
        using var first = new MemoryStream(bytes);
        var original = await fixture.Installer.InstallAsync(catalog, first, CancellationToken.None);
        var manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.Files["manifest.json"]) + "\n");
        fixture.Files["manifest.json"] = manifest;
        var updated = fixture.Archive();
        using var second = new MemoryStream(updated);
        var replacement = await fixture.Installer.InstallAsync(fixture.Catalog(updated), second, CancellationToken.None);
        Assert.AreNotEqual(original.PrivateRoot, replacement.PrivateRoot);
        Assert.AreEqual(catalog.ManifestSha256, Hash(await File.ReadAllBytesAsync(Path.Combine(original.PrivateRoot, "manifest.json"))));
        Assert.AreEqual(Hash(manifest), Hash(await File.ReadAllBytesAsync(Path.Combine(replacement.PrivateRoot, "manifest.json"))));
        Assert.HasCount(2, Directory.GetFileSystemEntries(fixture.Root));
    }

    [TestMethod]
    public async Task RootGateSerializesDifferentInstallerInstancesAndCanceledWaiters()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        using var source = new BlockingStream(bytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = fixture.Installer.InstallAsync(fixture.Catalog(bytes), source, timeout.Token);
        await source.Entered.Task.WaitAsync(timeout.Token);
        using var secondSource = new ObservedStream(bytes);
        using var cancel = new CancellationTokenSource();
        var otherInstaller = new Ct2PackageInstaller(fixture.Root + Path.DirectorySeparatorChar);
        var second = otherInstaller.InstallAsync(fixture.Catalog(bytes), secondSource, cancel.Token);
        try
        {
            Assert.AreEqual(0, secondSource.ReadCalls, "Installer instances sharing a root must share one gate.");
            Assert.HasCount(1, Directory.GetDirectories(fixture.Root, "*.staging"));
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        }
        finally { source.Release.TrySetResult(); }
        await first;
        Assert.IsTrue(Directory.Exists(fixture.PackageRoot));
        Assert.HasCount(1, Directory.GetFileSystemEntries(fixture.Root));
    }

    [TestMethod]
    public async Task SourceFailureRollsBackAndReleasesGate()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Archive();
        using var source = new ObservedStream(bytes, () => throw new IOException("Interrupted download."));
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Installer.InstallAsync(fixture.Catalog(bytes), source, CancellationToken.None));
        fixture.AssertNoOutput();
        using var retry = new MemoryStream(bytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.Installer.InstallAsync(fixture.Catalog(bytes), retry, timeout.Token);
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("ancestor")]
    [DataRow("package")]
    [DataRow("dangling-package")]
    public async Task ReparsePathsAreRejectedBeforeWritingAndOutsideFilesSurvive(string kind)
    {
        RequireSymbolicLinks();
        using var fixture = new Fixture();
        var external = Path.Combine(fixture.Base, "outside");
        Directory.CreateDirectory(external);
        var marker = Path.Combine(external, "keep.txt");
        await File.WriteAllTextAsync(marker, "untouched");
        string link;
        string root = fixture.Root;
        if (kind is "root" or "ancestor")
        {
            link = fixture.Root;
            Directory.CreateSymbolicLink(link, external);
            if (kind == "ancestor") root = Path.Combine(link, "not-created");
        }
        else
        {
            Directory.CreateDirectory(root);
            link = fixture.PackageRoot;
            Directory.CreateSymbolicLink(link, kind == "package" ? external : Path.Combine(external, "missing"));
        }
        try
        {
            var bytes = fixture.Archive();
            using var source = new ObservedStream(bytes, () => Assert.Fail("A reparse path must fail before download."));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new Ct2PackageInstaller(root).InstallAsync(fixture.Catalog(bytes), source, CancellationToken.None));
            Assert.HasCount(1, Directory.GetFileSystemEntries(external));
            Assert.AreEqual("untouched", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            if (kind == "dangling-package") File.Delete(link);
            else Directory.Delete(link);
        }
    }

    [TestMethod]
    public async Task CleanupRefusesReparseTreeButStillDeletesArchiveAndReleasesGate()
    {
        RequireSymbolicLinks();
        using var fixture = new Fixture();
        var external = Path.Combine(fixture.Base, "outside");
        Directory.CreateDirectory(external);
        var marker = Path.Combine(external, "keep.txt");
        await File.WriteAllTextAsync(marker, "untouched");
        string? link = null;
        var bytes = fixture.Archive();
        using var source = new ObservedStream(bytes, () =>
        {
            var staging = Directory.EnumerateDirectories(fixture.Root, "*.staging").Single();
            link = Path.Combine(staging, "injected-link");
            Directory.CreateSymbolicLink(link, external);
            throw new IOException("Simulated interrupted download after staging tampering.");
        });
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Catalog(bytes), source, CancellationToken.None));
            Assert.HasCount(0, Directory.GetFiles(fixture.Root, "*.archive.tmp"));
            Assert.IsFalse(Directory.Exists(fixture.PackageRoot));
            Assert.AreEqual("untouched", await File.ReadAllTextAsync(marker));
            using var retry = new MemoryStream(bytes);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await fixture.Installer.InstallAsync(fixture.Catalog(bytes), retry, timeout.Token);
            Assert.IsTrue(Directory.Exists(fixture.PackageRoot));
        }
        finally { if (link is not null) Directory.Delete(link); }
    }

    private static async Task RejectAsync(Fixture fixture, Ct2PackageCatalogEntry catalog, byte[] bytes)
    {
        using var archive = new MemoryStream(bytes);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(catalog, archive, CancellationToken.None));
        fixture.AssertNoOutput();
    }

    private static void RequireSymbolicLinks()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Symbolic-link fixtures require developer mode or link privileges on Windows.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Entry(string Path, byte[] Bytes, int Attributes = 0);

    private sealed class Fixture : IDisposable
    {
        internal string Base { get; } = Path.Combine(Path.GetTempPath(), "DropSpace-ct2-installer-" + Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(Base, "packages");
        internal string PackageRoot => Path.Combine(Root, "reviewed-ja-en-" + Hash(Files["manifest.json"]));
        internal Ct2PackageInstaller Installer { get; }
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal)
        {
            ["engine/helper.exe"] = Encoding.UTF8.GetBytes("fixture bytes; never executed"),
            ["model/model.bin"] = [1, 2, 3, 4],
            ["model/config.json"] = Encoding.UTF8.GetBytes("{}"),
            ["model/source.spm"] = [5, 6],
            ["model/target.spm"] = [7, 8],
        };

        internal Fixture()
        {
            Directory.CreateDirectory(Base);
            Installer = new(Root);
            Files["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, sourceLanguage = "ja", targetLanguage = "en",
                tokenizerProtocol = "ArgosSentencePiece", decoderProtocol = "Argos",
                engineExecutable = "helper.exe", sourceTokenizer = "source.spm", targetTokenizer = "target.spm",
                targetPrefix = (string?)null,
                engineFiles = Inventory("engine/"), modelFiles = Inventory("model/"),
            });
        }

        private object[] Inventory(string prefix) => Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => (object)new { path = f.Key[prefix.Length..], bytes = f.Value.LongLength, sha256 = Hash(f.Value) }).ToArray();

        internal List<Entry> Entries() => Files.Select(f => new Entry(f.Key, f.Value)).ToList();
        internal byte[] Archive() => Archive(Entries());
        internal static byte[] Archive(IEnumerable<Entry> files)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var file in files)
                {
                    var entry = zip.CreateEntry(file.Path, CompressionLevel.NoCompression);
                    entry.ExternalAttributes = file.Attributes;
                    using var output = entry.Open();
                    output.Write(file.Bytes);
                }
            return stream.ToArray();
        }
        internal Ct2PackageCatalogEntry Catalog(byte[] archive) => new("reviewed-ja-en", "ja", "en", archive.LongLength,
            Hash(archive), Hash(Files["manifest.json"]), Files.Select(f => new Ct2PackageFile(f.Key, f.Value.LongLength, Hash(f.Value))).ToArray());
        internal void AssertNoOutput() => Assert.HasCount(0, Directory.Exists(Root) ? Directory.GetFileSystemEntries(Root) : []);
        public void Dispose() { if (Directory.Exists(Base)) Directory.Delete(Base, true); }
    }

    private sealed class BlockingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ObservedStream(byte[] bytes, Action? beforeRead = null, int chunkSize = 65536) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        internal int ReadCalls { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            beforeRead?.Invoke();
            return _inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
