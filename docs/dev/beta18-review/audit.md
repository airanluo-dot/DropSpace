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
| 3 | Pending final round-2 fix commit | Pending fresh complete read | Not yet performed |

Round 1 repaired clipboard reload/capture publication and bounded live projection;
hidden music rendering/seek lifetime; off-thread payload-store initialization;
Undo cancellation ownership; stale lyric callbacks canceling a newer AI request;
same-title recording identity; drag timer retirement and accepted virtual-import
ownership; bounded NetEase backup retention; lyrics-cache publication locking;
output reservation ownership/partial-write failures; recent-query and staging
recovery worker boundaries; and transient lease recovery failures. Integration
follow-up preserved old fixed island appearance, bounded ASCII search-index work
without changing Unicode normalization, and repaired the required CI outcome gate
and the existing website scope helper. No measured speedup is claimed.

Round 2 reread all corrected and unchanged modules. Its new findings include live
clipboard paging/group/removal publication boundaries, stale navigation and hidden
settings work, failed overlay construction lifetime and tray-independent theme
updates, provider target-language completeness and bounded local pairing cleanup.
Services also fixed retryable device-secret reads, EXIF image geometry, dispatcher-bound image preflight, actual hotkey transaction ownership, retired OLE callbacks, final volume snapshot coalescing and dispatcher-bound NetEase native calls. All 16 new App findings are fixed in source; round 3 has not yet begun.

## Verification and remaining limits

[The verification ledger](verification-ledger.md) fixes the original suite denominator
at **2,124** cases, expanded before new tests, and the actual-execution ceiling at
**21**. No original test is deleted or counted twice to enlarge that denominator.
Source reads, parsing and compilation are distinct from functional test cases.
The selected producer allows eight real focused Core cases once and one isolated
installer payload installation/uninstallation, conservatively counted as one more
scenario. Repeats must be added to the ledger; skipped suites are not reported as
passing. No case has executed at this audit draft's creation.

This Linux session cannot observe interactive Windows light/dark transitions,
legibility/animation frames, Shell drag-in/drag-out, real media players, monitor/DPI
movement or GPU fallback. Actual hosted compilation and managed policy execution
will not establish those behaviors. Existing AI model semantic/latency/hardware
qualification remains incomplete; models, native worker bytes and the independent
CUDA archive are unchanged. A second native drop at a target busy importing accepted
virtual files is rejected and can be retried after completion; bounded ownership
prevents the prior accepted import from being canceled.

Public release, package and website verification remain pending. The next public
App version will be checked again immediately before preparation and publication.
