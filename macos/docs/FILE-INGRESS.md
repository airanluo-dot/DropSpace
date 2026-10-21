# File ingress and drag correspondence

Fixed source: Windows `v0.3.2-beta.7`, `1cd4620dd58a67b0963b73487743137813b8864a`. Status: implemented components with partial verification; **not accepted native drag/drop parity**.

## Ordinary references and drag out

`FileIntake` and `FileReferenceService` preserve ordinary sources as references. Manual intake is independent of clipboard capture settings and has the frozen2048 distinct-input bound. Missing inputs remain actionable records; Locate preserves identity/source. Availability checks, metadata worker ownership and last-use evidence are in CORE-CONTRACTS DATA-02.

Ordinary reference insertion follows frozen `AddPathsBatchAsync`: each item commits independently, accepted indexes are contiguous and batchCount retains the candidate count. An isolated real picker selected three files with a SQLite trigger rejecting only the second. UI reported2 accepted/1 rejected, SQLite contained first/third with indexes0/1 and count3, and all three source hashes were unchanged. Native Add Text persisted a separate one-item batch with `manual-text-url`; normal Quit exited and removed the warm empty promise lease. Main-window file/text/URL drop markers, Finder Services and application-open markers are now separate; pending startup requests keep their original batch/source. Services/drop routing still needs actual native acceptance.

`MainPage.OnDragItemsStarting` chooses actionable selected cards. One grouped card resolves the complete persisted batch; mixed selections prefer available storage items, one text/URL can be dragged, multiple text-only items cannot. The native `DragSelectionRules` and `WorkspaceDragging` implement that mapping. `NativeDragSource` owns only an admitted gesture; row selection/click/context actions stay in SwiftUI. Files resolve through the metadata scheduler and storage identity checks. Release, Escape, view removal, query changes and quitting retire pending work. NSDraggingSession offers copy only.

The real List bridge initially compared local AppKit points to row rectangles that included the hosting offset. Actual event traces exposed this defect; global row bounds are now translated to the bridge origin, with visible/bounds clipping. The repeat trace admits the correct row and sees leftMouseDragged. CUA supplied that event with `NSEvent.pressedMouseButtons == 0`, then release; no native dragging session was proven. The guard is retained. Three source hashes were unchanged, FinderDrop remained empty, the isolated process exited and the task Finder window was closed. This is a failed/unverified drag attempt, not a successful copy.

## Promised files and owned custody

Frozen owners: `Services/Ole/VirtualFileMaterializer.cs`, `Infrastructure/Storage/StagingLeaseStore.cs`, `OleDragDropService.CompleteVirtualDropAsync`.

| Rule | Native implementation | Evidence / limit |
|---|---|---|
 Materialize only after a drop | NativeDropView calls NativePromisedFiles synchronously in performDragOperation; AppKit receive call happens there | Native advertisement test confirms no files/name list before acceptance. Actual receiver drop remains unverified |
 Durable lease before first byte | One empty destination is prepared asynchronously at app startup; SQLite owned_imports precedes creation; no provider is called during warm-up/hover | Actual app had one lease/empty directory,0 items; normal Quit removed lease/directory and exited |
1000 items;2GiB each;8GiB batch | PromisedFileRules; inspect completed results before adoption; promised directories count their regular-file contents, links are retained without traversal | Sparse boundary tests passed. AppKit controls producer writes, so this is not a streaming limit on producer disk usage |
 Complete batch identity | Preserve titles and one UUID/index/count; adopt within the same payload filesystem; commit each row together with its custody transfer; later failures retain successful rows | Real SQLite/filesystem commit, undo/reopen and a second-row INSERT failure retaining first/third files passed |
 Source/path protection | Direct children of this batch only; descriptor-relative no-follow traversal; root type/identity/size/timestamps rechecked before exclusive rename; referenced sources never enter cleanup | Escaped link/root and replacement refused; external linked target unchanged |
 Cancellation / failed producer | Cancel further adoption immediately, retaining already committed items; retain receivers and callback queue until all advertised completions arrive; app lifetime owns the job, not the view | Importer cancellation before work and after the first committed item passed; actual provider cancellation and window changes during receive remain unverified |
 Cleanup/recovery | Uncommitted reserved paths become cleanup entries on abandonment/restart; only lease paths are eligible; failures remain queued | Interrupted adoption/reopen, permission-denied cleanup/retry and unrelated-file preservation passed |
 Progress/error language | Native status/cancel/retry, stable keys and raw system-error arguments;12 new IDs in10 offline locales | Resource integrity reviewed; no10-language runtime/layout qualification |

The prior code checked `fileNames` before acceptance (that array is empty then), used100 rather than1000, imported each callback separately and put cleanup ownership in a disposable view. These paths have been replaced.

An attempted provider/receiver API test outside a native drop crashed with NSInternalInconsistencyException: receivePromisedFiles may only run during prepareForDragOperation, performDragOperation or concludeDragOperation. This also exposed an incorrect asynchronous receive call in the first new integration. The fix prepares only an empty leased destination beforehand and calls receive synchronously in the actual drop callback. No exception suppression or fake drag context was introduced. The invalid acceptance test was replaced with an explicitly narrower advertisement-only test; the crash log is retained.

## Open native differences and acceptance

- Public NSFilePromiseReceiver has no producer-cancel/byte-stream interception API. A cancelled or over-limit producer can still be writing into the isolated destination; acceptance is withheld and custody is retained. Source crash, indefinite writer, native disk-full, recovery while another process still holds the destination and complete shutdown deadlines need real validation.
- Only one promise batch is admitted while another batch is receiving/retiring; a further drop reports busy. Unknown legacy counts or extra callbacks prevent claiming completion. Later reported complete counts can permit cleanup of a cancelled batch. Compatibility with actual legacy/multi-file providers and duplicate-name negotiation remains unverified.
- Directory promise handling retains the native tree and symbolic links without following link targets; the2GiB per-root aggregate is the native mapping for a promised directory. Windows OLE virtual-file descriptors are not identical to macOS directory promises.
- Physical Finder drag out/in, file-promise sources, cancelling during producer IO, whole-batch filtering/paging, scrolling/multiselection, Island routing, another filesystem/volume and macOS26 remain open. Source code, a package test or startup/quit evidence cannot close them.
- Finder Services exist but their complete acceptance remains open. The Share extension remains unimplemented. No public release or complete1:1 claim follows from this register.

Commands, failed attempts, retries and actual evidence paths are recorded in EXECUTION.md. Validation used task-owned roots and private pasteboards; no general clipboard/security setting/user source was changed.
