# Beta14 implementation and verification record

Scope: the ten-item October 6 owner-supplied Beta14 plan, markdown/DOCX parameter
table and all fourteen screenshots. Baseline App source f62f8fd (Beta13); checkout
5731929 additionally contained CI/website changes. Existing worktrees, downloader,
models, components, library and normal caches are preserved.

| Item | Implementation |
| --- | --- |
| 1 | Coordinator Open synchronously retires hide generation; ViewModel opens before refresh; zero count preserves expanded state; stale Dismissed/settled callbacks cannot clear manual state. |
| 2 | Fixed logo/name/chevron slots, E70D, ellipsis; same official transparent PNG embedded and decoded to both island images without URI resolution. No proved historical PRI/decode root cause. |
| 3 | ShowLogoWhenIdle default false, field-level merge and native localized toggle. |
| 4 | Visible/safe/active windows reassert topmost on reuse and normal foreground/timer events; existing no-activate/no-owner-order flags and suppression retained. Initial external demotion trigger remains unknown. |
| 5 | Eight native sliders; separate unlimited-width bool; natural text width and monitor clamp; 0–10 decimal GB disk cache; legacy values preserved; MiB import limits and logarithmic large ranges. |
| 6 | Playing alone supplies automatic media presence; paused metadata is retained. Existing one-deadline coordinator handles repeated pause, resume and other reasons. |
| 7 | Page KeyboardAcceleratorPlacementMode Hidden; shortcut handlers retained. |
| 8 | Settings-only blank/Enter focus behavior with composition tracking; placement commits Text before applying once and Escape restores. |
| 9 | Exact canonical/qualified bilingual titles, complete artist compatibility and version/album/time safeguards; NetEase revision3 retains canonical title/aliases; versioned source cache and bound-result revalidation. |
| 10 | Contextual admission v10; explicit foreign evidence wins, mixed-script units retain row IDs/times; native coverage skips per row; current completed local rows reused; statuses/reasons, partial failure retention and new cache identity. Fixed model template unchanged. |

## Checks actually run

A single focused seven-case initial MSTest run built Core/Infrastructure/test assembly.
Six passed initially; the cache sample had an invalid AI key and unbound source
identity. Only that case was rerun after correcting the fixture and passed.
One further focused boundary case passed: disabling disk persistence during
inference preserves the live translation and leaves no cache file.
The production cache safety checks were not relaxed. No full suite ran.

The new 48-line host admission snapshot records real current policy output and
retains independent source-language annotations. It explicitly reports no model
inference and no semantic approval. Controlled infer delegates verify host
admission, repeated output reuse, coverage, caching and failure states only.

An isolated Release app was opened with a separate test-data root. The idle logo
toggle displayed the embedded official logo; clicking the empty capsule opened
the Files panel and it remained open beyond the old two-second hide deadline.
It stayed above the ordinary main window with fullscreen forcing off. The five
import sliders exposed a clipped readout due to the old 180-DIP column; their
columns are now 260 DIP. This observation is not a hardware/fullscreen matrix.

Release build and package identities are recorded under runtime-publication.json
and SHA256SUMS.txt in the Release. CPU/Vulkan/CUDA native bytes are reused;
metadata is rebound to the actual Beta14 build. Installer payload execution,
large-file/network/hardware matrices and full-song semantic review are not run.

## Uncertainty

Historical screenshot provider and actual recording/termination cannot be inferred
from the UI alone. No blanket attribution to NetEase or model prompts is made.
Topmost guarantees cover normal Windows desktop windows, not protected desktops
or exclusive full-screen. Real playback/platform availability and model-quality
coverage require observation beyond the controlled host cases.

## Final local build

App build source: `bf4b97a893476b27d9007aa7ea0a511ae8ac90fe`.
Portable Release publish and MSIX build passed; Inno Setup 7.0.2 produced the
installer. The final portable was opened once to inspect the corrected slider
readouts at the ordinary window size. Its extracted managed assembly and the
final MSIX embedded runtime inventory match the recorded native bytes. The
installer evidence binds the compiler input only; it was not installed.

Local evidence: `.codex/beta14/evidence/empty-island-open.png`,
`.codex/beta14/evidence/slider-readouts-final.png` and build/check logs in the
same Beta14 directory. This isolated UI fixture changed no real user settings.
The subsequent CI route only accepts the pinned nine candidate asset hashes and
App commit; the permitted post-build diff is CI and this verification record.
It repeats no functional checks and does not turn skipped tests into passes.
