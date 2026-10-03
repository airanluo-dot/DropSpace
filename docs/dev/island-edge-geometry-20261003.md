# Island native clipping and motion geometry 鈥?2026-10-03

Historical geometry-only investigation. Its statements about unchanged material and
remaining implementation refer to that isolated probe, not the final combined Beta.2.
The candidate additionally changes transparent-host handling, Acrylic coverage,
Rendering scheduling and blur. Extra final dynamic/input/GPU acceptance was not
completed; the owner explicitly authorized skipping it. See the Beta.2 release notes.

Baseline: `0c876dbf4ee2761f54df9a135ba98bce1cf3aee2`.
Independent branch: `agent/edge-geometry-20261003`.
Independent worktree: `E:/Dev/DropSpace/artifacts/test/edge-geometry-20261003`.

This investigation covers physical geometry and GDI regions only. It does not change
the accepted light rendering, the window backdrop/material implementation, OLE routing,
or the host safety checks. No application window was launched and no screen test was run.

## Confirmed differences, separated by phase

The baseline `ApplyMotionFrame` gives XAML the fractional spring width, height, radii
and offset, followed by a centered `DropTargetScale` transform. It separately rounds
native dimensions/offsets, truncates native horizontal centering, and passes unscaled
radii to `OverlayNativeRegionController`.

| Case | Confirmed result |
| --- | --- |
| Compact steady, 100%: 340脳64 DIP, radius 32, top 18 | Nominal XAML/native bounds and radius agree. Pulse errors do not explain a persistent line in this pose. |
| Compact steady, 125% | XAML left/top are 162.5/22.5 physical pixels; native left/top are 162/22. Width 425 and radius 40 agree. |
| Expanded steady, 125% | Nominal width/height/radius are 700/425/35 pixels. The top still differs by 0.5 pixel. |
| Expanded accepted-drop pulse, 200%, scale .97 | Actual transformed XAML radius is 54.32 pixels; baseline native radius is 56. This transient difference disappears at scale 1. |
| Fractional transition radius 26 DIP, 125%, scale 1 | Native signature rounds 32.5 to 33 (AwayFromZero); the existing `ToPixels` rounds to 32 (ToEven). |
| Capsule with rounded height 81 pixels | GDI clamps radius to integer `height / 2 = 40`; an unclamped identity of 41 does not describe the builder's actual radius. |

The 48-case CSV in the evidence bundle records compact, expanded and a fractional
transition pose at 100/125/150/200%, each with scale 1/.97/.92/.75. Its baseline native
radius column includes the actual GDI integer clamp; the glow column uses baseline
`ToPixels`. These differences are geometric evidence, not a measured black-edge cause.

## Actual GDI region evidence at steady scale 1

The probe invokes the production `CreateAsymmetricRoundRectRegion` method through
reflection. That method's body is unchanged by this patch. It allocates HRGNs only;
there is no HWND, `SetWindowRgn`, App launch or material activation in the probe.

| Surface/DPI | Nominal size/radius in pixels | Actual `GetRgnBox` | Last-row center in region | Top-left pixels with sampled coverage excluded |
| --- | --- | --- | --- | --- |
| Compact / 100% | 340脳64 / 32 | [0,0,340,63) | false | 43 |
| Compact / 125% | 425脳80 / 40 | [0,0,425,79) | false | 55 |
| Compact / 150% | 510脳96 / 48 | [0,0,510,95) | false | 66 |
| Compact / 200% | 680脳128 / 64 | [0,0,680,127) | false | 99 |
| Expanded / 100% | 560脳340 / 28 | [0,0,560,339) | false | 44 |
| Expanded / 125% | 700脳425 / 35 | [0,0,700,424) | false | 49 |
| Expanded / 150% | 840脳510 / 42 | [0,0,840,509) | false | 60 |
| Expanded / 200% | 1120脳680 / 56 | [0,0,1120,679) | false | 86 |

Coverage here is a continuous circle reference sampled at 32脳32 points per pixel,
not captured WinUI alpha. For compact 100%, pixel (24,0) has sampled coverage 0.107422
but `PtInRegion` is false. At the flat bottom center, the entire nominal last row is
excluded. Thus the production binary mask can discard possible material AA coverage
even when the motion scale is 1 and nominal radii are integers. The eventual screen
appearance and its contribution to black edges remain unverified.

Microsoft documents that [SetWindowRgn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowrgn)
prevents display outside the region and uses window-relative coordinates. This patch
retains the existing client-to-window inset mapping and ownership/failure handling.
The right/bottom arguments to [CreateRoundRectRgn](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-createroundrectrgn)
are device-unit coordinates; the missing-row result above is measured locally rather
than inferred from undocumented rasterization details.

## Minimal production correction

- `OverlayFrameGeometry` makes one physical signature from the projected spring frame.
  Width, height, top and centering retain the existing host's ToEven/integer strategy.
  Radii include pulse, use that same rounding, and include GDI's integer radius clamp.
- XAML layout reverses the pulse/DPI transform from the signature. A horizontal
  translation compensates for differing host/body pixel parity and the measured root
  width. Native clipping consumes that exact signature without another DIP conversion.
- Existing glow geometry receives the same signature. No light rasterizer, material,
  artwork colors, brush or animation profile changes are made.
- The native application no longer forces a zero top radius to one pixel. The builder
  already has the exact rectangular branch for zero radii.

This removes projection disagreements. It does not make HRGNs antialiased and it does
not repair the measured rounded-region last-row truncation. The bounding rectangle is
identical to the baseline for the tested states/pulses, without added padding. Radius
membership changes during pulses are the intended contour correction, not AA padding.

## Preserving input/OLE while allowing complete material AA

Keep the current strict native body region, native failure hiding, visibility gates
and sole OLE target as the input authority. Do not add one-pixel HRGN padding or remove
the region as a visual workaround. Even a `+1` GDI endpoint correction restricted to
the same outer rectangle adds region members, so it requires explicit input analysis.

A renderer-compatible, visual-only alpha surface could draw the clipped AA fringe
while the current HWND retains its exact input/OLE mask. It must never register an OLE
target, activate, or become discoverable as an input owner. For a genuinely layered
window, Microsoft documents [WS_EX_TRANSPARENT mouse pass-through](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
That guarantee must not be assumed for an arbitrary WinUI/DComp host by merely setting
a style bit. The material renderer and actual cross-process pointer/OLE behavior need
owner-led validation before this architecture can be shipped.

An alternative retaining the same input region is an inward material alpha ramp whose
coverage lies wholly inside the existing binary mask; this slightly changes the visible
contour and likewise needs visual evaluation. Neither approach is implemented here.
Simply widening the visual HWND and returning `HTTRANSPARENT` is insufficient:
[WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest)
documents forwarding only to underlying windows in the same thread. OLE callbacks also
need the strict point gate if a future design separates visual and input geometry.

## Checks actually run

The isolated `GeometryChecks.csproj` links the production Core project, the native
interop/controller source, and only the selected geometry/motion test files. It does
not reference or build the WinUI App project.

- 44 passed, 0 failed, 0 skipped; MSTest execution 94 ms. TRX is included in the bundle.
- New geometry checks: transformed edges/radii at four DPIs, preservation of the existing
  input bounding rectangle, odd centering, half ties, zero radii, capsule clamp and inward
  pulse limits. Spring reversal/pulse coverage includes 240 frames per DPI (960 frames).
- Native checks: compact/expanded 脳 four pulse scales 脳 four DPIs stay inside exclusive
  input bounds, square zero-radius bounds, and partial geometric coverage rejection.
- Existing region-signature, motion-controller, motion catch-up and profile tests are
  included in those 44 tests.
- Native-only probe: eight steady GDI cases above. CSV: 48 mathematical cases.
- `git diff --check` passed.

The first test invocation failed before compiling because `--configfile` is a restore
option, not a `dotnet test` option. A second harness attempt used a global custom `obj`
path and failed on duplicate generated assembly attributes. The final isolated harness
uses its own subdirectory and the normal Core intermediate path. These were harness
setup failures; no test result is attributed to those attempts.

## Integration overlap and remaining owner work

The owner worktree was read only. Its current dirty patch already adjusts XAML pixel
placement inside `ApplyMotionFrame` and adds `SystemBackdrop = new IslandTransparentBackdrop()`.
Retain that backdrop line and all owner material/light changes. Integrate this method's
geometry helper in place of the owner's duplicate inline projection, plus the controller
constructor and `Apply(signature, out failure)` call. The owner inline signature still
uses unscaled radii and did not project the GDI clamp when last inspected.

Expected overlap: `OverlayWindow.xaml.cs` constructor near baseline line 218 and
`ApplyMotionFrame` near baseline line 1196. Companion files are the region controller,
region signature, helper, zero-radius native call and the two new test classes.

The isolated compilation checks Core and native adapter code. It does not compile XAML
or the complete `OverlayWindow` partial class. The owner must perform the combined App
build and steady/pulse screen QA. No install, system DPI/registry change, release,
push, PR or merge was performed. Final beta.2 remains publish-only under the user's
instruction; this branch does not install it.
