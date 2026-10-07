# Round 3 — historical glow and motion source comparison

The user reports that both glow modes and all island animations worked in Beta 16 and fail in Beta 17. This is a supplemental static historical trace after the full Core117 and delegated Services33 physical reads; it does not substitute for complete Round 3 coverage.

Compared read-only repository tags:

- `v0.3.1-beta.16`: `44f89877ae2a01b10a76b740c4cf3d3caf692376`
- `v0.3.1-beta.17`: `00d07b3773dc614587b75fbcf1affd45a136535b`
- Current immutable Round 3 source: `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9`

## Established source evidence

The entire `src` name diff between the historical tags contains twelve files: eight App files (overlay window, visual-preference service, item projection, MediaViewModel, compact and expanded music views, their render queue, and visual diagnostic) and four Infrastructure files (SQLite database/repository, download manager, CUDA runtime package). No Core source or native glow renderer, motion controller/orchestrator/compositor, or Acrylic/material implementation changed between these two tags. Identity rows below establish unchanged bytes; the current Core physical read and Services/UI owners' native/full reads establish their source-review context.

The only shared preference implementation change is `SystemVisualPreferenceService.Resolve`: Beta 16 calls `ReadPreferences(preference)` on each resolution; Beta 17 projects an explicit motion override over cached `Current`. Constructor initialization, preference events and the repeating 250 ms dispatcher poll are unchanged. The shipping App service registration provides the UI `DispatcherQueue`. This can change motion freshness if those refresh owners cannot run, but no source-backed trigger proving that condition was established.

Crucially, `OverlayWindow.UpdateGlowTarget` already used `Current` in Beta 16. The Resolve caching change therefore did not newly gate glow through the cache. Both historical tags retain the Core eligible brightness baseline `0.08`, including reduced motion and unavailable/silent audio. A cached reduced-motion state alone cannot explain a completely absent eligible halo.

The current `OverlayMotionOrchestrator` still binds its semantic controller to compositor channels. Overlay frame scheduling runs through a 16 ms dispatcher timer or a display-cadence rendering subscription, steps motion and page transitions, and applies safe native geometry before publishing a shutter frame. The historical scheduler code is unchanged. The Beta 17 media `SetActive`/`RefreshForPresentation` callbacks own only their media view's queued rendering delegate; the UI owner independently corroborated that those callbacks do not stop the motion driver, set reduced motion, or poison native glow availability. Synchronous compact preparation suppresses recursive geometry invalidation while reading the first presentation dimensions.

**No confirmed shipping regression cause is established by this comparison.** These facts narrow the source investigation and prevent an unsupported rewrite or a claim that a hypothetical native failure is the observed cause. The shipping package/runtime contents were not inspected or executed here; packaging/workflow routing changed and remains a separate provenance question. Current post-Beta17 feature/fix source differs from the two historical tags and is covered separately by the full Round 3 owners.

## Evidence limits

Read-only `git show`/`git diff`/`git rev-parse`, current supplemental caller reads, and file identity metadata. **Executed cases: 0.** No build, app/native execution, tests, fixtures, probes, package mutation, production edit, remote mutation or commit. Unchanged Git blob identities prove byte equality, not live Windows behavior or successful presentation. No prior review was counted as a new finding.

## Historical unchanged-byte identities

| File | Git blob identity in both tags | Comparison |
|---|---|---|
| `src/DropSpace.App/Services/IslandGlowController.cs` | `13c31e1df30b51e4ee84e0d05075ee8c0830cb0b` | Identical |
| `src/DropSpace.App/Services/IslandGlowRasterizer.cs` | `70ef4d89f42c41f6e35357786977f8fd21a691da` | Identical |
| `src/DropSpace.App/Services/IslandGlowWindow.cs` | `0a8890cbf937da9e8e99fb9503d90fec983cd07a` | Identical |
| `src/DropSpace.App/Services/OverlayMotionOrchestrator.cs` | `cde826fca92301012d940c2547681e46cf7e331c` | Identical |
| `src/DropSpace.App/Services/OverlayCompositionAnimator.cs` | `205967d74b7c6e0a062ac814b3922f3f094c80de` | Identical |
| `src/DropSpace.App/Services/IslandAcrylicBackdrop.cs` | `c0bb40bead9e744d431eb049cec20a5c6588ce95` | Identical |
| `src/DropSpace.App/Services/OverlayMaterialController.cs` | `d1bd4c7c90b3b682e43deaf06f707994b58a67da` | Identical |
| `src/DropSpace.Core/Overlay/OverlayMotionController.cs` | `731e23dee19d34d82d3d0690aa07654809c9385a` | Identical |
| `src/DropSpace.Core/Lyrics/LyricsGlowEnvelope.cs` | `4ca5a104d34b0bbe1d7abcd8c2bdafcd94339c74` | Identical |
| `src/DropSpace.Core/Lyrics/LyricsGlowPolicy.cs` | `925f52e48f6d90ac2887819ce635a98617a0f1da` | Identical |
| `src/DropSpace.Core/Lyrics/LyricsGlowAudioResponse.cs` | `d12ea665782cf491e524e8266fedf0a6c6a5b79d` | Identical |

## User-resolved closure

The user subsequently confirmed that reduced visual effects explained the animation/glow behavior and explicitly canceled this focused investigation. The historical source comparison above is retained as evidence of what was established statically; it is no longer an open regression investigation. No animation or glow production changes, new implementation, additional tests, or runtime probes are requested or made for this report. Actual executed cases remain **0**.
