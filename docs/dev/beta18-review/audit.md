# DropSpace Beta18 integrated source review

The repository owner authorized PRs #107–109, both island settings, three complete
sequential App source review/fix rounds, severely limited verification and direct
publication of the next Beta. [The decision record](../../../scripts/ai-model-qa/evidence/v0.3.1-beta.18-owner-decision.md)
preserves that authorization without claiming new model-quality approval.

## Integration

Work began on main `00d07b3773dc614587b75fbcf1affd45a136535b`.
The latest PR heads and required checks were inspected before merging:

| PR | Result | Main squash commit |
| --- | --- | --- |
| [#107](https://github.com/airanluo-dot/DropSpace/pull/107) | Merged; bilingual website requirements/FAQ now avoid obsolete Beta.25 wording | `8908a047680ad2e3e66aed2d65bdd6fb5abadb79` |
| [#108](https://github.com/airanluo-dot/DropSpace/pull/108) | Merged; five Linux production jobs use verified Ubuntu 26.04 | `83bd467dfa50288beab7da91d5c765d1af5bb796` |
| [#109](https://github.com/airanluo-dot/DropSpace/pull/109) | Merged; `action-gh-release` pins Node24-compatible v3.0.3 SHA | `85c5716b7c8f1d58a197defd2b2bb38680dc7032` |

PRs #107/#108 needed a real complete Windows App/XAML compile under the required
check name, because their originally skipped/dynamically named jobs left the
required check unavailable. The associated CI naming fix supplied those successful
compiles; repository protection was not changed. No test cases ran in those jobs.

Both island features were integrated in `57474cd5ca02ad64bb2367cfb05bb94b74380b8c`
before any complete review round began. Appearance uses independent Follow/Light/
Dark settings and theme resources; Follow retains the existing Windows **application**
appearance source, including Custom mode. Priority is shared by compact and
expanded states; explicit user pages survive passive refreshes. Both settings
persist immediately. Existing fixed appearance is migrated once from the previous
main-window preference when the new field is absent.

## Sequential complete coverage

Each round froze a different complete source snapshot, read every listed file in
full and then fixed the new confirmed findings. Parallel partitions divide one
round's coverage; they are not different review rounds. Scope includes App, Core,
Infrastructure, XAML, locale resources, project/lock/manifest configuration and
embedded OpenCC text/data. Brand binaries and external vendor engine trees are not
misrepresented as text source reads. Native worker and delivery paths received
additional source inspection.

| Round | Immutable source commit | Complete coverage | Result records |
| --- | --- | --- | --- |
| 1 | `57474cd5ca02ad64bb2367cfb05bb94b74380b8c` | 424 files / 84,564 physical lines | [UI](round1-app-ui.md), [Services](round1-app-services.md), [Core](round1-core.md), [Infrastructure](round1-infrastructure.md), [integration](round1-root.md), [release](round1-release.md), [manifest](round1-source-manifest.json) |
| 2 | `729d5b179f34ab2f852e7975f129431f77e8dc7a` | 425 files / 84,869 physical lines | [UI](round2-app-ui.md), [Services](round2-app-services.md), [Core](round2-core.md), [Infrastructure](round2-infrastructure.md), [integration](round2-root.md), [manifest](round2-source-manifest.json) |
| 3 | `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9` | 425 files / 85,220 physical lines | [UI](round3-app-ui.md), [Services](round3-app-services.md), [Core](round3-core.md), [Infrastructure](round3-infrastructure.md), [root supplement](round3-root.md), [manifest](round3-source-manifest.json) |

Round 1 repaired clipboard reload/capture publication and bounded live projection;
hidden music rendering/seek lifetime; off-thread payload-store initialization;
Undo cancellation ownership; stale lyric callbacks canceling a newer AI request;
same-title recording identity; drag timer retirement and accepted virtual-import
ownership; bounded NetEase backup retention; lyrics-cache publication locking;
output reservation ownership/partial-write failures; recent-query and staging
recovery worker boundaries; and transient lease recovery failures. Integration
follow-up preserved old fixed island appearance, bounded ASCII search-index work
without changing Unicode normalization, and repaired the required CI outcome gate
and the existing website scope helper. Round 1 fixed 18 confirmed App/Core/Infrastructure/integration findings; release-routing fixes are recorded separately. No measured speedup is claimed.

Round 2 reread all corrected and unchanged modules. Its new findings include live
clipboard paging/group/removal publication boundaries, stale navigation and hidden
settings work, failed overlay construction lifetime and tray-independent theme
updates, provider target-language completeness and bounded local pairing cleanup.
Services also fixed retryable device-secret reads, EXIF image geometry, dispatcher-bound image preflight, actual hotkey transaction ownership, retired OLE callbacks, final volume snapshot coalescing and dispatcher-bound NetEase native calls. All 16 new App findings are fixed in source.

Round 3 reread the entire second-round-corrected source. Six confirmed findings in the third-round review/final-delta phase concern hidden widgets doing visual work, programmatic card brushes bypassing the island theme, monitor-edge OLE probe geometry, unbounded DropLink response buffering and path-only rollback deleting user replacements of published outputs, plus the subsequently reported lyrics outcome classification. All six are fixed; their individual reports record the changes and source-only verification. No further confirmed Core issue was found. [Final feature checks](round3-feature-final-static.md) cover the selected cases and state/persistence boundaries. The subsequently authorized [lyrics outcome correction](round3-lyrics-state-classification.md) preserves completed no-match evidence when another source fails or times out, while true all-failure remains retryable; this received focused final-delta review. The [lyrics empty-state wording](round3-lyrics-empty-state.md) uses “无匹配歌词” / “No matching lyrics” only for NotFound, retaining the retry message for genuine Failed; the existing state pipeline was traced and the two resource edits received final delta review. The user identified the reported animation/glow behavior as the reduced-motion setting and canceled those two bug requests; their existing renderers are retained.

## Verification and remaining limits

[The verification ledger](verification-ledger.md) fixes the original suite denominator
at **2,124** cases, expanded before new tests, and the actual-execution ceiling at
**21**. No original test is deleted or counted twice to enlarge that denominator.
Source reads, parsing and compilation are distinct from functional test cases.
The selected producer allows eight real focused Core cases and two Infrastructure lyric-outcome cases, each executed once, plus one isolated installer payload installation/uninstallation, conservatively counted as one more scenario (planned total eleven). Repeats must be added to the ledger; skipped suites are not reported as
passing. Final-main validation executed exactly **11 cases/scenarios** with no repeats (0.518% of the original suite). Both managed selections passed and the isolated installer payload check completed; the final ledger and private Actions receipts preserve their actual scope.

This Linux session cannot observe interactive Windows light/dark transitions,
legibility/animation frames, Shell drag-in/drag-out, real media players, monitor/DPI
movement or GPU fallback. Actual hosted compilation and managed policy execution
will not establish those behaviors. Existing AI model semantic/latency/hardware
qualification remains incomplete; models, native worker bytes and the independent
CUDA archive are unchanged. A second native drop at a target busy importing accepted
virtual files is rejected and can be retried after completion; bounded ownership
prevents the prior accepted import from being canceled.

The [final release-route review](round3-release-final.md) also repaired an automatic CUDA-contract job that would have exceeded the budget; exact Beta18 now checks actual component metadata binding without running the unrelated fixture suites. Original tests remain intact, including the existing receive-finalization case updated to the safe preserved-file contract.

[Beta18 is public](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.1-beta.18), published 2026-10-07T17:53:45Z from exact main `e4d6b1b2ac22dd2a70b0bd08bcabd8d21a6d6175`. The producer, immutable publication and Pages deployment succeeded. All eight public files were actually downloaded and hash-checked; actual PE/MSIX and update versions agree. Stable remains v0.2.1 and the original independent CUDA component is reused. [Public verification evidence](public-verification.json) records downloaded identities, versions and the precise download/static-inspection scope. Final documentation updates retain the published App commit and do not claim a new App build/release.
