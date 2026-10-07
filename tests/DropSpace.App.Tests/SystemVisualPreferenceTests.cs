using DropSpace.App.Services;
using DropSpace.Core.Compatibility;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class SystemVisualPreferenceTests
{
    [TestMethod]
    public void RepeatedMotionResolutionDoesNotReprobeWindowsCapabilities()
    {
        var capabilities = new CountingCapabilities();
        using var service = new SystemVisualPreferenceService(capabilities);
        var initialReads = capabilities.Reads;
        var initialSnapshotReads = capabilities.SnapshotReads;
        Assert.AreEqual(2, initialReads, "Construction captures the two material capabilities once.");
        for (var index = 0; index < 1000; index++)
        {
            _ = service.Resolve(OverlayMotionPreference.System);
            _ = service.Resolve(OverlayMotionPreference.Full);
            _ = service.IsReducedMotion(OverlayMotionPreference.Reduced);
        }
        Assert.AreEqual(initialReads, capabilities.Reads);
        Assert.AreEqual(initialSnapshotReads, capabilities.SnapshotReads);
    }

    [TestMethod]
    public void MotionOverridesPreserveEveryCapturedMaterialPreference()
    {
        var capabilities = new CountingCapabilities();
        using var service = new SystemVisualPreferenceService(capabilities);
        var current = service.Current;
        Assert.IsFalse(current.DesktopAcrylicSupported);
        Assert.IsTrue(current.CompositionEffectsSupported);
        Assert.IsFalse(current.CompositionEffectsFast);
        Assert.IsTrue(current.IsRemoteSession);
        Assert.IsFalse(current.IsWindows11OrLater);
        Assert.AreEqual(current, service.Resolve(OverlayMotionPreference.System));
        Assert.AreEqual(current with { Motion = OverlayVisualPreferenceMode.Full },
            service.Resolve(OverlayMotionPreference.Full));
        Assert.AreEqual(current with { Motion = OverlayVisualPreferenceMode.Reduced },
            service.Resolve(OverlayMotionPreference.Reduced));
    }

    [TestMethod]
    public void ReducedMotionUsesTheCapturedSystemPreferenceAndExplicitOverride()
    {
        using var service = new SystemVisualPreferenceService(new CountingCapabilities());
        Assert.AreEqual(service.Current.ReducedMotion, service.IsReducedMotion(OverlayMotionPreference.System));
        Assert.IsFalse(service.IsReducedMotion(OverlayMotionPreference.Full));
        Assert.IsTrue(service.IsReducedMotion(OverlayMotionPreference.Reduced));
    }

    private sealed class CountingCapabilities : IWindowsCapabilityService
    {
        public int Reads { get; private set; }
        public int SnapshotReads { get; private set; }
        public WindowsCompatibilitySnapshot Snapshot
        {
            get
            {
                SnapshotReads++;
                return new(new(10, 0, WindowsCompatibilityPolicy.MinimumSupportedWindowsBuild, 0),
                    new(CompatibilityStatus.Available, "test"));
            }
        }
        public WindowsCapabilityState Get(WindowsCapability capability)
        {
            Reads++;
            return capability switch
            {
                WindowsCapability.DesktopAcrylic => new(capability, CompatibilityStatus.BlockedByPolicy,
                    "test", IsRemoteSession: true),
                WindowsCapability.CompositionEffects => new(capability, CompatibilityStatus.Available,
                    "test", IsFast: false),
                _ => new(capability, CompatibilityStatus.UnsupportedByOs, "test"),
            };
        }
        public bool IsAvailable(WindowsCapability capability) => Get(capability).IsAvailable;
    }
}
