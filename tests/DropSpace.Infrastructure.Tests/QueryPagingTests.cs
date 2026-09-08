using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class QueryPagingTests
{
    private string _root = null!;
    private AppStoragePaths _paths = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-query-paging-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppStoragePaths(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task KeysetPaging_ReturnsEveryItemExactlyOnce_AndLegacyOffsetUsesSameOrder()
    {
        var repository = CreateRepository();
        for (var index = 0; index < 23; index++)
        {
            await repository.AddTextAsync(ContentClassifier.CreateTextCandidate($"paging item {index:D2}"));
        }

        var all = new List<DropItem>();
        ItemQueryCursor? cursor = null;
        bool hasMore;
        do
        {
            var page = await repository.QueryPageAsync(
                new ItemQuery(Source: ItemSource.Clipboard, Limit: 7),
                cursor);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
            hasMore = page.HasMore;
        } while (hasMore);

        Assert.AreEqual(23, all.Count);
        Assert.AreEqual(23, all.Select(item => item.Id).Distinct().Count());

        var legacyOffset = await repository.QueryAsync(
            new ItemQuery(Source: ItemSource.Clipboard, Limit: 5, Offset: 7));
        CollectionAssert.AreEqual(
            all.Skip(7).Take(5).Select(item => item.Id).ToArray(),
            legacyOffset.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task TrigramSearch_FindsLongMiddleSubstring()
    {
        var repository = CreateRepository();
        var expected = await repository.AddTextAsync(
            ContentClassifier.CreateTextCandidate("prefix architecturehardening suffix"));
        await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("unrelated clipboard value"));

        var results = await repository.QueryAsync(new ItemQuery(Search: "tecturehard", Limit: 20));

        Assert.IsTrue(results.Any(item => item.Id == expected.Id));
    }

    private SqliteItemRepository CreateRepository()
    {
        var database = new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance);
        return new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
    }
}
