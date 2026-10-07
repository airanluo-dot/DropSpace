# Beta 18 round 1 — App OLE and drag services

Review source: immutable `/workspace/scratch/beta18-round1`. The canonical repository agent guide and DropSpace maintainer skill were applied. All assigned files were read in full. The initial review did not edit production; subsequent authorized fixes are recorded below. No tests or builds were run.

## Confirmed findings

### R1-OLE-01 — Cancellation-source disposal can abort observer shutdown before hooks are stopped

**Priority:** P2. **File:** `src/DropSpace.App/Services/DragSessionDetector.cs:425–428`; disposal sites `1169–1177,1209–1217`. The same unsynchronized field cancellation occurs at `977–978,1042–1043,1139`.

The grace and timeout tasks execute independent continuations that clear their owning field and dispose their `CancellationTokenSource`. `StopCoreAsync` calls `_completionGrace?.Cancel()` and `_sessionTimeout?.Cancel()` without catching a disposal race. Reading the nonnull field, then letting the task clear/dispose that source, then calling `Cancel()` throws `ObjectDisposedException`. The surrounding dictionary cancellation loop already handles exactly this condition at lines 413–422, but these direct calls do not.

The exception exits `StopCoreAsync` before `_runCancellation?.Cancel()` and before WM_QUIT/hook-thread retirement. `SetModeAsync` has already published the requested non-Smart mode at line 218, so the observer can continue queuing/processing input while the app believes it disabled. During disposal, the `finally` completes the queues and marks the detector disposed but still leaves live native hook ownership reachable. Other direct cancellation sites convert the race into cancellation of an otherwise valid drag through the generic per-signal error handler.

**Minimal fix:** use one safe cancellation helper that catches `ObjectDisposedException` for these independently retired grace/timeout sources, including all direct call sites. Alternatively, synchronize field exchange, cancellation and disposal under one owner lock. Keep shutdown proceeding through run cancellation and native retirement when a scheduled source has already completed.

### R1-OLE-02 — A second virtual-file drop cancels the first drop after it has been accepted

**Priority:** P2. **File:** `src/DropSpace.App/Services/OleDragDropService.cs:1030–1037`; materialization/rollback evidence `src/DropSpace.App/Services/Ole/VirtualFileMaterializer.cs:81,87–108,112–124,133–170`.

Virtual-file `Drop` returns `DROPEFFECT_COPY` and starts `CompleteVirtualDropAsync`. Materialization deliberately yields for async providers and copies already-owned non-async media on a worker, so the first accepted operation can remain active after the OLE callback returns. A later virtual-file `Drop` unconditionally cancels and replaces `_dropCancellation`, then also reports Copy. The first materializer observes cancellation between chunks/files and rolls back its durable staging lease. Because `CompleteVirtualDropAsync` awaits materialization before invoking the shared owned-path intake callback at lines 1172–1175, none of that first accepted batch reaches Temporary Space.

For example, dropping a large mail attachment and immediately dropping another virtual attachment accepts both gestures but loses the first import. This is separate from the generation guards: supporting reads of `OverlayViewModel` confirmed those guards retire visual completion only and do not skip the actual intake.

**Minimal fix:** retain a separate cancellation/lifetime owner for each accepted virtual drop under a bounded admission policy. If only one owner is supported, return `DROPEFFECT_NONE` for a later virtual drop while the previous completion is active. Cancel all retained owners on explicit service disposal; do not cancel previously accepted intake merely because another drop begins.

## Coverage

All 9 assigned files, totaling 5,374 lines, were physically read from first through last line:

| File under `src/DropSpace.App/Services` | Lines |
| --- | ---: |
| `DragSessionDetector.cs` | 1,933 |
| `OleDragDropService.cs` | 1,353 |
| `Ole/EphemeralOleDragProbe.cs` | 810 |
| `Ole/OleDropTargetNative.cs` | 32 |
| `Ole/OleFileDataClassifier.cs` | 418 |
| `Ole/QueryOnlyDataObject.cs` | 74 |
| `Ole/SmartDragProbeOptions.cs` | 43 |
| `Ole/SmartDragRuntimePolicy.cs` | 41 |
| `Ole/VirtualFileMaterializer.cs` | 670 |
| **Total** | **5,374** |

Supporting call-site reads traced the guarded drop callbacks in `OverlayWindowService` and intake-vs-visual fencing in `OverlayViewModel`. Review covered reliable/lossy signal ordering, pointer release/grace lifetimes, native callback containment, probe owner-thread cleanup/reentrancy, OLE subscription ownership, descriptor/path bounds, per-file/batch copy limits, marshaled stream/medium release, and durable staging rollback. The other assigned files had no additional sufficiently evidenced finding in this pass. Windows native behavior remains outside this no-test review.


## R1-OLE-01 production fix notes

The authorized detector fix routes every grace/timeout field cancellation through `CancelScheduledSource` (`DragSessionDetector.cs:426–427,977–978,1042–1043,1139,1213–1220`). It catches only an already-disposed source so observer shutdown continues to run cancellation and native retirement. Each timer continuation atomically clears only its own owning field before disposing (`1172,1208`); an older completion cannot erase a replacement timer. The existing scheduled-task drain remains in place, and no source is disposed by a canceling caller. Source-only lifetime review and scoped `git diff --check` passed. No tests, builds, remote actions or commits were performed.

## R1-OLE-02 production fix notes

The authorized minimal fix was applied to the canonical production tree, `/workspace/DropSpace`, after the immutable review above. `OleDropTargetRegistration` now admits one accepted virtual import at a time per registered target. While that import is pending, a new gesture receives `DROPEFFECT_NONE` and must be retried after completion; this bounded policy also rejects ordinary file drops at that target during the pending virtual import.

- `OleDragDropService.cs:923–935`: pending-owner admission runs before replacing the current data object, classification, accepted state, generation, or visual callbacks. The rejected-gesture flag preserves rejection even if the original import completes before the later gesture ends.
- `OleDragDropService.cs:986–988,1020–1026,1040–1047`: busy `DragOver`, `DragLeave`, and `Drop` do not invoke `DragLeft` cleanup or cancel the accepted import. Suppressing that cleanup is necessary because `OverlayWindowService.OnVisibleDragLeft` cancels the visual state (`1283–1294`), and hiding the Overlay revokes/disposes its native registration (`OverlayWindow.xaml.cs:1059–1064,1764–1767`).
- `OleDragDropService.cs:1053–1059,1189–1245`: an accepted virtual operation captures its completion guard before acquiring its cancellation owner. Its owner remains present through materialization, shared intake, and staging lease cleanup; the outer completion `finally` atomically clears only that owner and disposes its source even if cleanup fails.
- `OleDragDropService.cs:1176–1178`: explicit registration disposal cancels the active accepted operation but leaves source disposal to its draining completion. An already-completed source cannot interrupt retirement with `ObjectDisposedException`.

Source-only verification traced the async and non-async materializer paths, shared owned-path intake, visual completion guards, registration retirement, rejection before and after owner completion, and disposal ownership. `git diff --check -- src/DropSpace.App/Services/OleDragDropService.cs` passed. No tests or builds were run, and no remote operations or commits were performed. Windows native drag behavior and overlapping provider gestures still require runtime validation; this section records a source-backed fix, not executable acceptance evidence.
