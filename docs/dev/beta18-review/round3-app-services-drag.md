# Beta 18 Round 3 — drag observer and ephemeral OLE probe

## Scope and fresh-read evidence

Reviewed frozen source reference `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9` from `/workspace/scratch/beta18-round3`. The frozen tree has its source files directly under that directory's `src/` subdirectory. The repository `AGENTS.md` and canonical `.agents/skills/dropspace-maintainer/SKILL.md` were read before reviewing source.

This subtask physically read exactly **2 source files / 2,743 lines**, from first line through final line, including unchanged code, diagnostics, comments, native declarations, and COM interface declarations:

| File | Entire range physically read | Lines | Read chunks |
| --- | --- | ---: | --- |
| `src/DropSpace.App/Services/DragSessionDetector.cs` | 1–1933 | 1,933 | 1–325; 326–650; 651–975; 976–1300; 1301–1625; 1626–1933 |
| `src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs` | 1–810 | 810 | 1–270; 271–540; 541–810 |

The combined output containing detector lines 1301–1625 was truncated; that **entire chunk was reread separately with complete output**. There are no unread gaps. Targeted rereads after the complete pass do not add distinct coverage. Two initial reads used an incorrect duplicated `src/` prefix and returned no source; those failed reads carry no credit.

`wc -l` confirmed the counts above. Read-only Git object checks, using the repository's path filters for the frozen CRLF working-tree files, matched the frozen reference:

| File | Canonical Git blob |
| --- | --- |
| `DragSessionDetector.cs` | `a2e6c4009d722588d94c2d3cff9641c35c03e026` |
| `EphemeralOleDragProbe.cs` | `1ef0affeda54bd5ea0846c100ccdb6cebb0f0cc7` |

These two files belong to the combined App Services partition's **120-file / 27,316-line** credited coverage: the App Services parent physically read **85 files / 18,100 lines**, the Core media owner physically read its distinct **33 files / 6,473 lines**, and this subtask physically read **2 files / 2,743 lines**. This report does not claim the other owners' files as its own reads. No prior review or prior finding was reused. The counts and blob references above describe the frozen review baseline, before the authorized live-source correction below.

## Finding

### R3-SVC-01 (supporting alias R3-DRAG-01) — P2: monitor-edge clamping moves the probe hole away from the cursor

This is the same single finding as the parent App Services report's R3-SVC-01, not an additional finding. Its live source correction is recorded below; the affected references and description in this finding identify the frozen review baseline.

**Affected source:** `src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs:98–109`, `:311–319`, `:361–380`. The native contract check at `:216–221` does not catch the mismatch.

The stated probe contract is that the cursor starts inside the real region hole and naturally crosses the surrounding ring (`:24–28`). `CalculateMonitorAwareCenter` instead moves the center inward to keep the whole outer square inside the selected monitor. `ApplyHollowRegion` always cuts the hole around this shifted center, rather than around the original cursor. Near any monitor boundary, the current cursor therefore lies inside the ring when the probe is created.

The geometry is established directly from the source. With the default outer size of **144 pixels** and hole size of **12 pixels**, on a monitor large enough to contain the probe, an origin at `monitor.Left + 1` is clamped to `monitor.Left + 72`. The window then starts at `monitor.Left`. The original cursor's local X coordinate is **1**, while the hole covers local X coordinates **66 through 77**. Its current point belongs to the outer region and is outside the hole. The same calculation applies at the top boundary and, with corresponding coordinates, the right and bottom boundaries.

The parent App Services reviewer supplied freshly read caller context: `SmartDragRuntimePolicy.cs:5–7` defines defaults 144/12/60 ms, `:11–14` defines outer/hole bounds 120–160/8–16, and `SmartDragProbeOptions.cs:15–25` validates those sizes and that the hole is smaller than the outer square. `OleDragDropService.cs:108–120` samples the live cursor, `:128` selects its monitor, and `:134–139` passes that unchanged point and monitor to the constructor. The caller does not move the candidate away from the boundary before creating the probe. Those files were physically read by the parent and are **excluded from this subtask's 2-file coverage**.

This breaks the hole-at-current-cursor invariant precisely where monitor boundaries and the top-edge wake approach make edge positions relevant. The target at the cursor is no longer excluded by the region, so the source code cannot guarantee the intended initial unobstructed point. The native contract check only verifies the shifted center is outside the region and a sample ring point is inside it; it never verifies `_origin` is inside the actual hole.

**Correction required by the finding:** preserve the original cursor inside the real region hole while constraining the ring's intersection with the selected monitor, and make the native-contract geometry check validate the original cursor point. The initial source-only review made no correction; the separately authorized narrow live-source correction is recorded below.

**Confidence and limit:** high confidence in the geometry defect. Windows OLE hit-test timing, DragEnter delivery, a canceled drop, and user-visible impact were not executed or reproduced; this finding does not claim a measured OLE protocol outcome.

## Other reviewed behavior

- **Observer callback lifetime and shutdown:** detector `:591–713` roots the hook delegates, creates the message queue before reporting readiness, registers observers before pumping, and unhooks in the hook thread's `finally`. `:195–234` and `:335–361` serialize lifecycle changes; `:411–538` bounds asynchronous waits, retains live thread/processor state, and prevents a replacement observer while the old one remains active. These are source observations, not evidence of successful native unhooking on Windows. Native unhook return values are ignored, and the final disabled log is unconditional even when the earlier bounded-wait path logs a still-live observer.
- **OLE callback reentrancy and native cleanup:** probe `:251–309`, `:477–559`, and `:669–705` defer completion/cleanup while `DragEnter` is inside classifier COM calls, then queue another owner-thread message after the callback unwinds. Timer callbacks request owner-thread work rather than directly revoking or destroying native resources. The shared window procedure is statically rooted (`:54`, `:563–617`). No separate confirmed callback-lifetime defect was established in this pass.
- **Timeout/cancellation convergence:** detector `:873–911` tags delayed completion, verification, rejection, and timeout signals with session IDs. `:1132–1219` retires and disposes scheduled cancellation sources, and tolerates cancellation racing source disposal. Probe `:383–473` uses a locked first-result slot; timeout and classification compete for that slot, and owner cleanup clears it. `:529–559` gives an already deferred cleanup request priority while a nested OLE callback is active. Native cleanup remains dependent on the owner thread actually processing the request; the watchdog also queues owner work rather than establishing an absolute native cleanup deadline.
- **COM use:** detector `:1316–1349` initializes COM on the worker thread that performs source inspection and balances successful initialization with `CoUninitialize`. `:1692–1806` releases acquired UIA/MSAA objects, handles expected COM/conversion failures, and limits the raw-view ancestor count. Probe classification takes place synchronously inside the supplying OLE callback, with rejection on a caught classification failure; this subtask did not physically read the classifier implementation and does not claim classifier coverage.
- **Cross-monitor behavior:** detector resolves the current transition point to a monitor at candidate and verification publication (`:982`, `:1007`). The probe uses physical pixel coordinates and the selected monitor's rectangle. The confirmed edge-hole defect above is the finding from this area; mixed-DPI Windows behavior was not executed.
- **Performance and remaining races:** low-level observer callbacks marshal/queue metadata rather than perform UIA inspection (`:715–799`). Moves use a lossy capacity-one lane (`:56–57`), and the reader merges the lanes by timestamp while canceling and observing both pending read waits (`:1265–1313`). Source inspection still runs synchronously on the single processor (`:846`, `:1316–1349`, `:1692–1784`); a bounded ancestor count is not an explicit wall-clock bound on foreign COM calls. A delayed provider can therefore delay processing and leave the processor alive after the bounded stop wait. Actual latency, provider responsiveness, backlog size, and whether this happens on the supported Windows versions were not measured, so no separate performance or shutdown defect is asserted from an API assumption alone.

## Checks and honest limits

Executed **0 tests**. The task ceiling is **21** tests against the original **2,124-test** baseline; none of that allowance was used. No tests or fixtures were read or changed, and no probes, builds, application launches, native execution, model inference, remote mutations, or commits were performed. The initial review made no production edits and used source reads, line counts, and read-only reference/blob verification. The follow-up authorization permitted the one live source correction and scoped diff check recorded below.

This is a fresh two-file source review. It establishes one P2 geometry defect and records the relevant source protections and limits. It does not certify Windows callback scheduling, COM provider timing, successful native resource teardown, mixed-DPI runtime behavior, or end-to-end drag/drop behavior.

## Authorized narrow live-source correction

After all Round 3 full reads were complete, the parent authorized the minimal R3-SVC-01 geometry correction in live `src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs`. The inward monitor-clamped window, native hollow-region approach, region-handle ownership, OLE callbacks, timing, and policies remain intact.

- **Live lines 317–326:** the existing centered hole offset is translated independently in X and Y by `_origin - _probeCenter`. Subtracting this translated inner region from the same outer square clips the removed hole at edges and corners. With no clamping, both translation offsets are zero, so the prior centered geometry and odd/even size behavior are preserved. For the default left-edge example, the inner X interval moves from `[66, 78)` to `[-5, 7)`, whose intersection with the outer square removes `[0, 7)` and contains local cursor X = 1. At a top-left cursor of `(0, 0)`, the same translation removes the corner intersection `[0, 6) × [0, 6)`.
- **Live lines 216–224:** `VerifyNativeContract` converts the original cursor to actual window-local coordinates and checks that point is outside the native target region. The positive ring sample is selected on the opposite X side of the cursor; a fixed left-side sample could otherwise lie inside a correctly translated hole.

The scoped source diff is **12 added / 5 removed lines**, bringing the live file to **817 lines**; the frozen full-read coverage remains **810 lines** for this file. Existing CRLF source formatting was preserved. The final source diff was physically inspected and `git diff --check -- src/DropSpace.App/Services/Ole/EphemeralOleDragProbe.cs` passed. No tests, probes, builds, or native execution were run for the correction.

Only that source file and this supporting report were changed by this subtask. Motion and glow source were left untouched after the user confirmed reduced motion and canceled the animation/glow investigation. This fix closes the source geometry mismatch; actual Windows region hit-testing and OLE delivery remain unvalidated under the authorized source-only limits.
