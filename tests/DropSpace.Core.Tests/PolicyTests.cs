using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class PolicyTests
{
    [TestMethod]
    public void Classifier_RecognizesHttpUrlAndRemovesFragment()
    {
        var candidate = ContentClassifier.CreateTextCandidate("https://example.com/path?q=1#private");

        Assert.AreEqual(ItemKind.Url, candidate.Kind);
        Assert.AreEqual(DetectedSubtype.Url, candidate.Subtype);
        Assert.AreEqual("example.com", candidate.Title);
        Assert.IsNotNull(candidate.Url);
        Assert.AreEqual("https://example.com/path?q=1", candidate.Url.NormalizedUrl);
    }

    [TestMethod]
    [DataRow("#0af")]
    [DataRow("#00AAFF80")]
    [DataRow("rgb(10, 20, 30)")]
    [DataRow("rgba(255, 0, 127, .5)")]
    public void Classifier_RecognizesStrictColors(string text)
    {
        var result = ContentClassifier.Classify(text);

        Assert.AreEqual(ItemKind.Color, result.Kind);
        Assert.AreEqual(DetectionConfidence.High, result.Confidence);
    }

    [TestMethod]
    public void SearchNormalizer_IsCaseAndDiacriticInsensitive()
    {
        Assert.AreEqual("cafe 已固定", SearchNormalizer.Normalize("  CAFÉ\t已固定  "));
    }

    [TestMethod]
    public void PayloadPathPolicy_RejectsDirectoryTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", "payloads");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            PayloadPathPolicy.ResolveContainedPath(root, Path.Combine("..", "outside.bin")));
    }

    [TestMethod]
    public void RetentionPolicy_PreservesPinnedAndSpaceItems()
    {
        var now = DateTimeOffset.UtcNow;
        var oldestClipboard = CreateItem(ItemSource.Clipboard, now.AddDays(-40));
        var overflowClipboard = CreateItem(ItemSource.Clipboard, now.AddMinutes(-3));
        var newestClipboard = CreateItem(ItemSource.Clipboard, now.AddMinutes(-1));
        var pinnedClipboard = CreateItem(ItemSource.Clipboard, now.AddDays(-50), isPinned: true);
        var spaceItem = CreateItem(ItemSource.Space, now.AddDays(-50));

        var expired = RetentionPolicy.SelectExpired(
            [newestClipboard, overflowClipboard, oldestClipboard, pinnedClipboard, spaceItem],
            now.AddDays(-30),
            countLimit: 1);

        CollectionAssert.AreEquivalent(
            new[] { overflowClipboard.Id, oldestClipboard.Id },
            expired.ToArray());
    }

    [TestMethod]
    public void LogRedactor_RemovesPathsQueriesAndSecrets()
    {
        var redacted = LogRedactor.Redact(
            "open C:\\Users\\Airan\\secret.txt at https://example.com/a?token=abc api_key=hunter2 Bearer abc.def");

        Assert.IsFalse(redacted.Contains("Airan", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("token=abc", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("hunter2", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("abc.def", StringComparison.Ordinal));
    }

    private static DropItem CreateItem(ItemSource source, DateTimeOffset createdAt, bool isPinned = false) => new(
        Guid.NewGuid(),
        source,
        source == ItemSource.Space ? ItemKind.File : ItemKind.Text,
        "item",
        createdAt,
        null,
        isPinned,
        ItemStatus.Available,
        "item",
        1,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}
