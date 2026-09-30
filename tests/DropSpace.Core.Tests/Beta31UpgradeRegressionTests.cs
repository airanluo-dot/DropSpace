using DropSpace.Core.Updates;
using DropSpace.Core.Widgets;
namespace DropSpace.Core.Tests;
[TestClass]
public sealed class Beta31UpgradeRegressionTests
{
    [TestMethod]
    public void Beta31HasMonotonicInstallerAndPackageVersions()
    {
        var previous = ReleaseVersion.Parse("v0.3.0-beta.30");
        var current = ReleaseVersion.Parse("v0.3.0-beta.31");
        var next = ReleaseVersion.Parse("v0.3.0-beta.32");
        var stable = ReleaseVersion.Parse("v0.3.0");
        Assert.IsTrue(previous < current && current < next && next < stable);
        Assert.IsTrue(previous.ToVersionCode() < current.ToVersionCode());
        Assert.IsTrue(current.ToVersionCode() < next.ToVersionCode());
        Assert.IsTrue(previous.ToPackageVersion() < current.ToPackageVersion());
        Assert.IsTrue(current.ToPackageVersion() < next.ToPackageVersion());
    }
    [TestMethod]
    public void InvalidWidgetLayoutsNormalizeToBoundedNonoverlappingCells()
    {
        var random = new Random(31);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var items = Enumerable.Range(0, 24).Select(_ => new WidgetPlacement(
                (NativeWidgetId)random.Next(-3, 20), random.Next(-20, 20), random.Next(-20, 20), random.Next(-4, 20), random.Next(-4, 20))).ToArray();
            var normalized = WidgetLayoutPolicy.Normalize(new(items, new(null, null, null)));
            var occupied = new HashSet<(int, int)>();
            foreach (var item in normalized.Expanded)
            {
                Assert.IsTrue(item.Column >= 0 && item.Row >= 0);
                Assert.IsTrue(item.Column + item.ColumnSpan <= WidgetLayoutPolicy.Columns);
                Assert.IsTrue(item.Row + item.RowSpan <= WidgetLayoutPolicy.Rows);
                for (var x = item.Column; x < item.Column + item.ColumnSpan; x++)
                    for (var y = item.Row; y < item.Row + item.RowSpan; y++) Assert.IsTrue(occupied.Add((x, y)));
            }
            Assert.AreEqual(normalized, WidgetLayoutPolicy.Normalize(normalized));
        }
    }
}
