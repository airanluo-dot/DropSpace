using DropSpace.Core.Policies;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class Beta5TextTitleTests
{
    [TestMethod]
    public void TruncatedTitleDoesNotSplitSupplementaryCharacter()
    {
        var title = ContentClassifier.CreateTitle(new string('a', 158) + "😀long suffix");
        Assert.AreEqual(new string('a', 158) + "…", title);
    }

    [TestMethod]
    public void LargeBodyDoesNotChangeFirstLineOrWhitespaceBehavior()
    {
        Assert.AreEqual("hello", ContentClassifier.CreateTitle("  hello  \n" + new string('x', 2_000_000)));
        Assert.AreEqual("Text", ContentClassifier.CreateTitle("  \nsecond line"));
    }
}
