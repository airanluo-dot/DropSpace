# DropSpace v0.3.0-preview.14 motion-system execution report

## Scope

Implemented the Preview.14 motion/material slice from the attached optimization plan on top of `main@763a2761` (Preview.13): channel-specific motion profiles, reduced-motion bounds, transition descriptors, compositor-owned visual-only channels, bounded Desktop Acrylic selection, DPI-aware native-region deduplication, and release/test documentation.

The change intentionally does not alter the Smart Drag classifier, OLE payload authorization, data model, network protocols, source-file behavior, or the Dynamic-Island-only product boundary.

## Implementation

- Core: `OverlayMotionProfileSet`, bounded drop-confirmation scale, `OverlayTransitionDescriptor`, `OverlayRegionSignature`, and `OverlayRegionUpdatePolicy`. Large elapsed intervals are subdivided into stable integration steps so 60/120/144 Hz cadence and dropped-frame gaps remain bounded.
- App: `SystemVisualPreferenceService`, `OverlayMaterialController`, `OverlayCompositionAnimator`, `OverlayNativeRegionController`, and explicit DI/lifecycle wiring.
- XAML: bounded `SystemBackdropElement`, solid fallback, high-contrast-aware stroke/background resources, non-replacing hover tint, and content-only press feedback.
- Native/lifecycle: repeated `SetWindowRgn` and empty-region calls are skipped by physical signature; animation smoke waits on the actual settled signal rather than fixed animation sleeps.

## Checks run

- Core test project: 147 tests passed locally on .NET 10.0.100/Linux. This verifies pure Core contracts only.
- `git diff --check`: passed.
- Windows App build, packaged/portable smoke, real Windows OS/DPI/OLE/accessibility interaction, GDI/USER measurements, GitHub Release, and website/API verification: pending the Windows CI/release workflow and real-device evidence.

## Evidence status

Preview.14 is **CONDITIONAL**. No Linux result is represented as proof of Windows rendering, Desktop Acrylic availability, native OLE behavior, DPI placement, accessibility, packaging, or release publication. The required release and manual evidence is defined in [docs/test-plan/v0.3.0-preview.14.md](docs/test-plan/v0.3.0-preview.14.md).
