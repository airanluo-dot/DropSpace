using DropSpace.Core.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class WindowsCompatibilityPolicyTests
{
    [TestMethod]
    [DataRow(17_762, false)]
    [DataRow(17_763, false)]
    [DataRow(19_045, false)]
    [DataRow(20_347, false)]
    [DataRow(20_348, true)]
    [DataRow(26_100, true)]
    public void SupportedBuild_IsInclusiveAtBuild20348(int build, bool expected)
    {
        Assert.AreEqual(expected, WindowsCompatibilityPolicy.IsSupportedBuild(build));
    }

    [TestMethod]
    [DataRow(17_763, false)]
    [DataRow(21_999, false)]
    [DataRow(22_000, true)]
    [DataRow(26_100, true)]
    public void Windows11Visuals_StartAtBuild22000(int build, bool expected)
    {
        Assert.AreEqual(expected, WindowsCompatibilityPolicy.IsWindows11OrLater(build));
    }
}
