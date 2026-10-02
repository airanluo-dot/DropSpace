# Cloud Beta 1 repair candidate — 2026-10-02

This work starts at `a31d0092dc2b3fb5c4c437bb100429d32cbf0bfc` (tree
`1cf13129f84f948cc1653ed67b819ef6fada5e44`) on the separate
`agent/cloud-beta1-fixes-20261002` branch. Target version remains `v0.3.1-beta.1`.
The user's Windows checkout, model directories and GPU were not accessed. No release,
tag, merge or deployment is authorized by this candidate.

## Repairs

- Lyrics requests retain cancellation callbacks, provider slots and timeout ownership until
  actual completion. Cancelled late results cannot populate the cache. Explicit music refresh
  bypasses source cache and remains pending across worker/cache-maintenance contention;
  AI cleanup no longer occupies both source-fetch slots. Translation errors preserve fetched
  source lyrics. The existing soft restart refreshes music, artwork and lyrics.
- Real mocked NetEase/QQ payloads and old source-v2 cache entries receive conservative
  translation-language evidence. A valid source translation matching the target bypasses
  the entire AI path before cache/resolver/inference/progress calls; partial blanks remain.
- Original-language admission excludes credits and confidently same-target lines before
  AI activity. Explicit TTML span languages and bounded lexical/context rules preserve
  foreign verses and unknown text. Original IDs, cache v2 eligibility, progress and final
  results use the same policy; previous AI caches cannot restore excluded translations.
  The fixed model prompt and sampler are unchanged. These rules are conservative heuristics,
  not a general calibrated language detector.
- Glow mode uses a standard untemplated WinUI ComboBox. Refresh has a full system refresh
  glyph and localized text. The default-on Show AI lyrics label setting changes rendering
  only in main, compact and expanded surfaces.
- Compact translations have their own measured viewport and marquee, including fractional
  12–28 DIP original fonts and 0.875 translation scaling. Main/expanded text wraps without
  ellipsis or a two-line cap inside a focusable ScrollViewer. Track/text/font/width/DPI and
  label changes remeasure; scrolling and reduced-motion preferences remain effective.
- The lighter, smaller halo uses the existing six real FFT bands for contour and intensity.
  Its independent click-through HWND sits below the body; finite signed-distance overlap
  extends inward beneath it while deep interior and outside bitmap edges stay transparent.
  Render padding does not expand body hit testing. Actual visible LocalAi text drives AI
  mode across retained gaps, including expanded viewport intersection. Both forms share
  smoothed intensity and frozen contour motion on pause/Off/reduced motion.
- Native cleanup now observes the Windows kernel process signal via a retained duplicated
  process handle. The original cancellation/deletion regression remains, with repeated
  immediate deletion and caller-timeout coverage. This addresses a cleanup race found while
  investigating old CI `37003028144`; Linux cannot establish that Windows failure is closed.
- Optional 7B is documented separately in [its integration record](hy-mt2-7b-optional-profile.md).
  Default 1.8B identity/prompt/sampler are retained. No complete 7B weights were downloaded.

## Actual cloud verification

| Check | Actual result | Limit |
| --- | --- | --- |
| Complete Core suite | 450 passed, 0 failed, 0 skipped | Pure managed policies |
| Complete Infrastructure suite | 580 passed, **8 failed**, 26 skipped | Linux platform/environment failures below |
| Linked actual App services and four regression files | 51 passed, 0 failed, 0 skipped | Temporary net10.0 harness; not the full WinUI App suite |
| Linked actual App glow rasterizer tests | 14 passed, 0 failed, 0 skipped | Native window tests compiled but not run on Linux |
| Node scripts | 346 passed, 0 failed, 0 skipped | Includes strict dual-model identity and source fingerprint checks |
| Localization, release version/consistency, Windows compatibility, hardcoding, secret hygiene | Passed | Static PowerShell policies, not Windows execution |
| Native resource policy | C++17 compile and execution passed | Small policy fixture; no model/runtime loading |
| Actual AI publication approval | Blocked at `pending` as expected | No new quality approval was produced |

The eight Infrastructure failures were preserved and investigated: seven call Windows DPAPI
on Linux (four authentication, one pairing admission, two revoke-store tests). One existing
receive-route test attempts to create `/home/agent/Downloads`; the cloud's read-only home
returns `EROFS`, which the endpoint maps to HTTP 400. That final failure was reproduced from
the already-built test DLL with syscall tracing. The affected production/tests and their
storage/transfer dependencies have no diff from the a31 baseline. These failures are not
reported as passes or removed/skipped to obtain a green result.

Cloud TRX files are under the corresponding test project's ignored `TestResults` directory.
Temporary linked App evidence is in `/workspace/scratch/app-managed-regression` and
`/workspace/scratch/glow-regression-harness`; Node/PowerShell logs and exact executor setup
are in `/workspace/scratch/node-static-triage`. None is a path on the user's Windows computer.
NuGet restore succeeded through the configured network; dependency locks and sources were
not changed. No runtime, model, build cache or large test payload is added to Git.

## Remaining acceptance

The candidate must receive new Windows CI for its exact pushed commit, including both en-US
and zh-CN matrices, native cleanup tests, worker rebuild, WinUI compile and packaging. Old
a31 CI and user screenshots do not validate these repairs. Requested Library screenshots
could not be materialized by the official helper here; no actual screenshot pixels or new
dynamic Windows frames were observed. Native controls, 100/125/150/200% physical DPI, text
scale, keyboard/high-contrast behavior, halo layering at the top edge and actual playback
still need observation on Windows. Added native visual diagnostics cover these boundaries
without downloading models, but their presence is not a recorded pass.

Real 7B quality, loading, cancellation/unload, CPU/GPU memory, speed and 16 GB GPU behavior
remain unmeasured. Existing quality capture assumes every fixture line reaches inference;
its adapter needs separately reviewed eligibility-aware evidence, and 7B needs its own
model-bound captures. Publication stays blocked until those genuine captures and independent
same-commit review exist. Later user-machine acceptance should reuse installed models and
directories, retain local uncommitted changes, and clean only this task's obsolete outputs.
