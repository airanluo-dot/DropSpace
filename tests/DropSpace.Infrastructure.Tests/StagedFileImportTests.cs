using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class StagedFileImportTests
{
    private readonly AppStoragePaths _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-import-tests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [TestMethod]
    public async Task ImportCommitsOwnedPayloadRejectsOversizedAndNeverDeletesExternalSource()
    {
        _paths.EnsureCreated();
        var source = Path.Combine(_paths.Root, "external.txt");
        var batch = Path.Combine(_paths.Staging, "batch");
        Directory.CreateDirectory(batch);
        var good = Path.Combine(batch, "good.txt");
        var big = Path.Combine(batch, "big.txt");
        await File.WriteAllTextAsync(source, "original");
        await File.WriteAllTextAsync(good, "new");
        await File.WriteAllTextAsync(big, "oversized");
        var repository = Repository();
        var service = Service(repository, new LocalFileReferenceService());
        var result = await service.ImportBatchAsync([source, good, big], 42, "virtual-file", 3);
        Assert.AreEqual(1, result.Accepted);
        Assert.AreEqual(2, result.Rejected);
        Assert.AreEqual("original", await File.ReadAllTextAsync(source));
        Assert.IsFalse(Directory.Exists(batch));
        var item = (await repository.QueryAsync(new ItemQuery())).Single();
        Assert.IsNotNull(item.Payload);
        var owned = new FilePayloadStore(_paths).ResolvePath(item.Payload.RelativePath);
        Assert.AreEqual("new", await File.ReadAllTextAsync(owned));
        Assert.AreEqual(1, Directory.GetFiles(_paths.Payloads, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task CancellationAfterAdmissionCleansUnvisitedStagingFiles()
    {
        _paths.EnsureCreated();
        var batch = Path.Combine(_paths.Staging, "batch");
        Directory.CreateDirectory(batch);
        var first = Path.Combine(batch, "first.txt");
        var unvisited = Path.Combine(batch, "second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(unvisited, "second");
        using var cancellation = new CancellationTokenSource();
        var repository = Repository();
        var service = Service(repository, new CancellingReferences(cancellation));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ImportBatchAsync([first, unvisited], null, "virtual-file", 1024, cancellation.Token));
        Assert.IsFalse(Directory.Exists(batch));
        Assert.AreEqual(0, (await repository.QueryAsync(new ItemQuery())).Count);
        Assert.AreEqual(0, Directory.GetFiles(_paths.Payloads, "*", SearchOption.AllDirectories).Length);
    }

    private SqliteItemRepository Repository() => new(new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);
    private StagedFileImportService Service(IItemRepository repository, IFileReferenceService references) =>
        new(_paths, references, new FilePayloadStore(_paths), repository, NullLogger<StagedFileImportService>.Instance);

    private sealed class CancellingReferences(CancellationTokenSource cancellation) : IFileReferenceService
    {
        private readonly LocalFileReferenceService _inner = new();
        public async Task<FileCandidate> InspectAsync(string path, CancellationToken cancellationToken = default)
        {
            var result = await _inner.InspectAsync(path, cancellationToken);
            cancellation.Cancel();
            return result;
        }
        public Task<FileAvailabilityCheck> CheckAvailabilityAsync(FileReference reference, CancellationToken cancellationToken = default) => _inner.CheckAvailabilityAsync(reference, cancellationToken);
    }
}
