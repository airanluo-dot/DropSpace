# Beta 18 round 1 — complete Core source review

Review source: immutable `/workspace/scratch/beta18-round1`. The canonical `/workspace/DropSpace/AGENTS.md`, `.agents/skills/dropspace-maintainer/SKILL.md`, its App/UI reference, and relevant architecture guidance were read. No production source was edited. No tests, builds, diagnostics, or native execution were run.

## Confirmed finding

### R1-CORE-01 — Bounded search indexing normalizes the discarded tail of every large text item

**Priority:** P3 (allocation and repeated work). **Files:** `src/DropSpace.Core/Policies/ContentClassifier.cs:87–91`; `src/DropSpace.Core/Policies/SearchNormalizer.cs:15,24,27–50`. Shipping caller: `src/DropSpace.Infrastructure/Data/SqliteItemRepository.cs:198–225`.

`BuildSearchText` concatenates the entire title/body and passes it through complete NFKD normalization before retaining only the first 65,536 output characters. `SearchNormalizer` additionally creates a builder sized to the complete decomposition, creates temporary strings for each retained rune, then materializes the complete normalized result. For ordinary ASCII text, everything after character 65,536 is necessarily discarded, yet a default-limit 2 MiB clipboard item still performs this work for its entire body; settings admit up to 16 MiB. The actual text insertion invokes this synchronous work after acquiring the shared repository write gate and opening a transaction. This creates avoidable allocation churn and extends the time unrelated repository mutations must wait. Text acquisition, hashing, and durable text storage remain necessary independently of this indexing waste.

**Minimal fix:** give search normalization an output budget and stop processing the input after the stored normalized prefix is complete. Preserve Unicode/combining-sequence and whitespace semantics while processing bounded chunks, avoid assembling a full title/body copy, and append lowercase runes without a temporary string per ASCII character. Computing the bounded search projection before acquiring the repository write gate also reduces write-gate occupancy. Preserve the existing 65,536-character search contract and full original payload.

This is a static finding from the input/output bounds and shipping call path. No latency, memory peak, or OOM measurement is claimed.

## Integrated finding handed to the App reviewer

The Core pass independently identified that the App supplies a partial media identity to `IslandExperienceCoordinator.UpdateMedia`: session/source/title rather than `MediaSessionSnapshot.TrackIdentity`. A continuously playing same-title replacement recording therefore fails to reopen an island dismissed on the prior recording. The complete App media review confirmed and owns this issue as **R1-MEDIA-02** in `round1-app-services-media.md`; do not count it twice. Core's dismissal policy correctly relies on the identity it receives.

## Coverage

Every file in `/workspace/scratch/beta18-round1/scope/core-scope.txt` was physically read from first through last line: **116 files, 14,746 lines**. The full scope list SHA-256 is `3d3c187cd29203a373f5b9ae15fb82e85ff22f8b25780161d13c4e918d2d859c`. This was a full-source pass, not a diff or search-only pass. Any output-truncated portions were reread separately.

| Directory under `src/DropSpace.Core` | Files | Lines |
| --- | ---: | ---: |
| Abstractions | 11 | 431 |
| Actions | 4 | 353 |
| Collections | 2 | 300 |
| Compatibility | 1 | 95 |
| Content | 2 | 126 |
| Diagnostics | 1 | 6 |
| Displays | 1 | 38 |
| Downloads | 1 | 60 |
| DragDrop | 4 | 481 |
| Island | 5 | 269 |
| Lyrics source | 23 | 2,401 |
| Lyrics/Data | 3 | 5,129 |
| Media | 7 | 351 |
| Models | 10 | 1,081 |
| Overlay | 10 | 1,326 |
| Policies | 8 | 447 |
| Preview | 1 | 165 |
| Shell | 1 | 139 |
| SystemActivities | 1 | 11 |
| Transfer | 7 | 966 |
| Undo | 1 | 16 |
| Updates | 6 | 291 |
| Widgets | 4 | 248 |
| Project/lock metadata | 2 | 16 |
| **Total** | **116** | **14,746** |

The 5,062-line OpenCC dictionary was read completely, including its comments, rare supplementary-rune entries, and ambiguous mappings. A read-only `sha256sum` inspection returned `9ff46a7d30e5765375eb13d33f2b03a34d298913caf2b120380679f33ae1642d`, matching the adjacent provenance README. The project embeds both the table and Apache license; the lazy loader accepts only single-rune/single-result pairs and skips ambiguous entries. No arbitrary artist-name mappings were introduced by that loader. Project target and empty Core package lock are consistent (`net10.0`).

Focused policy review covered independent island/main-window theme defaults, normalization and per-field appearance merge; content-priority normalization/merge; explicit page choices across passive refreshes and content loss; single/multiple-file variants; paused content retention versus playing visibility; stopped-media caller admission; drag precedence and temporary page projection; hide-delay quantization and retained hide start; dismissal/deadline generations; fullscreen/manual-open behavior; and coordinator/state-machine callers. No additional sufficiently evidenced defect was confirmed in those Core policies.

The remaining full-source review covered interface ownership boundaries, projection serialization/disposal, drag evidence and late probes, lyrics identity/admission/output/caches, parsing/timing/glow, monotonic media interpolation, overlay geometry/motion/placement/region bounds, clipboard deduplication/propagation budgets, transfer/handoff validation, update ordering, and widget layout/countdown state. Supporting caller reads used the immutable snapshot's `OverlayWindowService`, `OverlayWindow`, `MediaExperienceService`, `SettingsApplicationCoordinator`, `ClipboardCaptureService`, and `SqliteItemRepository`.

## Limits

This pass supplies source evidence only. Windows dispatcher timing, COM/OLE/WinRT behavior, real media/audio/model behavior, and measured memory/latency are not validated by this no-test review. Other App/Infrastructure partitions own their complete-source review; supporting caller reads here are not an assertion that those partitions were fully covered by this reviewer. Subsequent rounds must rescan their fresh integrated snapshot after fixes.
