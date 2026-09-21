# Beta 26 native and data audit

Scope: actual Beta 25 source at `32f055a`, including its first-parent diff.
This report covers the non-media native/data sub-audit. Final solution, portable,
installer and release verification are recorded by the coordinating audit.

## Confirmed bugs and fixes

| Severity | Trigger and root cause | Fix and evidence |
| --- | --- | --- |
| S1 | A synchronous virtual-file source returns `TYMED_ISTREAM`. The Beta 25 P/Invoke for `CoGetInterfaceAndReleaseStream` declared two arguments and returned a pointer; Windows actually takes a third output-pointer argument and returns HRESULT. Native code could write through an invalid address. | Correct ABI, validate HRESULT, release the returned interface. Actual native `CreateStreamOnHGlobal` streams cross the production marshal/unmarshal boundary in the regression matrix. |
| S1 | An app payload subdirectory is replaced by a directory link. Direct payload-store operations and its legacy deferred-delete journal checked only the textual path prefix, allowing deletion of a file outside the payload root. | All payload-store path resolution uses the existing reparse-safe owned-path policy. A real directory-link test verifies direct delete/read/write rejection, journal restart recovery, and intact external sentinel contents. This is defense against pre-existing links; no claim of atomic protection against an adversarial concurrent junction replacement is made. |
| S2 | A synchronous virtual-file batch offers repeated names. Beta 25 reserves every medium before writing files, but name collision checks considered only existing files. Multiple media obtained the same output path and `CreateNew` failed, rolling back the entire batch. | Reserve names in a case-insensitive batch set, in both async and synchronous branches. Regression uses repeated Chinese names and a pre-suffixed collision, validates each independent payload's bytes across HGLOBAL/IStream and async/synchronous sources. |
| S2 | A later medium fails or cancellation abandons already-marshaled streams. Releasing the marshal packet's IStream alone does not release the interface reference stored in that packet. | Consume the unused packet with `CoGetInterfaceAndReleaseStream`, then release its interface. Native reference-count regression confirms only the fixture reference survives rollback and staging is empty. |
| S2 | 256 old outbox entries cannot be deleted. Every bounded drain previously selected those same oldest-created rows, permanently starving all later cleanup. | Order by oldest/null last-attempt time before creation/id. A database regression inserts 256 malformed obligations followed by a real payload and proves the second bounded drain deletes the later payload while retaining all failures. |
| S2 | Resume settings have committed, then the caller cancels before the duplicate-state reset. The cancellable reset threw, leaving runtime capture paused while disk said resumed. | Complete the in-memory reset without caller cancellation after durable commit. Fault-injection test uses real JSON persistence and cancels exactly after its successful update. |
| S2 | Shutdown occurs while initialization awaits settings. Disposal could destroy the state semaphore/CTS before initialization resumed, subsequently attaching callbacks or starting workers with disposed resources. | Shutdown waits for initialization's state gate; initialization checks the shutdown flag after its await. Deterministic blocked-load regression verifies orderly ownership and repeated disposal. |

## Coverage and related-pattern review

- Read the maintainer skill/native contract and relevant product/UX/architecture,
  roadmap and persistence/lifecycle decisions before changing code.
- Reviewed `ClipboardCaptureService` notification queue, current-sequence checks,
  pause generation/commit gate, repository commit paths, self-write markers,
  retention and disposal; `ClipboardNotificationService` native registration;
  `ConsecutiveClipboardCaptureCoordinator` commit/reset semantics.
- Reviewed `SqliteItemRepository` image/file transaction commit boundaries and
  cleanup outbox; `StagedFileImportService` lease admission, cancellation and
  rollback; `FilePayloadStore` journal/write/read/delete; `OwnedPayloadReconciler`
  quarantine and ownership; reparse-path policy; `JsonSettingsService` serialized
  load/migration/save/update and existing settings concurrency/migration coverage.
- Reviewed `VirtualFileMaterializer` descriptor/medium ownership, source async
  contract, marshaling, output collision handling and cleanup; `OleDragDropService`
  registration and virtual-drop completion; `DragSessionDetector` observer,
  cancellation/drain and timeout ownership.
- Reviewed `OverlayWindowService` creation, wake-mode transitions, topology
  rebuild, dispatcher failure recovery, event removal and shutdown; monitor
  negative-coordinate nearest-monitor calculation, DPI fallback and foreground
  fullscreen classification; display watcher and maintenance shutdown lifecycle.
- Repository-wide search found the incorrect COM API declaration/marshal-packet
  ownership confined to the materializer. Both allocation branches now use the
  same reservation set. Payload resolver correction covers direct store callers
  and journal recovery, not only the already-protected cleanup coordinator.
- These checks do not claim every interleaving or physical display/source-app
  behavior has been demonstrated. No speculative native UI behavior was changed.

## Executed verification

1. `dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --filter FullyQualifiedName~PayloadCleanupOutboxTests --no-restore -v minimal`
   — 4 passed, 0 failed, 0 skipped.
2. `dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~AuditHardeningTests|FullyQualifiedName~StorageAndRepositoryTests|FullyQualifiedName~StagedFileImportTests|FullyQualifiedName~PayloadCleanupOutboxTests|FullyQualifiedName~OwnedPayloadReconcilerTests' --no-restore -v minimal`
   — 41 passed, 0 failed, 0 skipped; includes actual Windows directory links.
3. `dotnet test tests/DropSpace.App.Tests/DropSpace.App.Tests.csproj -p:Platform=x64 --filter 'FullyQualifiedName~ClipboardStateRaceTests|FullyQualifiedName~Preview16OleLifetimeTests' --no-restore -v minimal`
   — 10 passed, 0 failed, 0 skipped; includes native OLE streams/handles and
   two clipboard persistence/lifecycle fault-injection tests.
4. Before the coordinator fixed the test-host Windows App SDK bootstrap, the
   original App tests failed at module initialization (`REGDB_E_CLASSNOTREG`).
   A temporary WinUI-free harness linking the exact production adapter and same
   test source independently passed 8 OLE tests. The corrected real App project
   subsequently passed item 3; the temporary harness is outside the repository.
5. `git diff --check` — passed; Git emitted line-ending normalization notices.

The targeted App build emitted PRI257/PRI263 for the MSTest adapter's localized
satellite resources. These are test-host packaging warnings, not hidden failing
assertions; final build warning disposition belongs to the coordinated gate.

## Evidence boundaries

- Native COM API contract verified against Microsoft documentation:
  [CoGetInterfaceAndReleaseStream](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cogetinterfaceandreleasestream),
  [CoMarshalInterThreadInterfaceInStream](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-comarshalinterthreadinterfaceinstream),
  [CoReleaseMarshalData](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coreleasemarshaldata).
- NOT VERIFIED — physical mixed-DPI display hotplug, negative-coordinate physical
  monitor layout, external Explorer/Outlook/provider drag gestures, long-duration
  resource stability, high contrast and keyboard/focus/Alt+Tab matrix. These need
  the relevant interactive hardware/source applications; native fixture tests
  are not substitutes.
- No schema/dependency/product-scope change. The existing app/data boundaries,
  file-reference ownership and bounded cleanup contract remain in place.
- Skill synchronization is performed by the coordinating agent, including the
  corrected native OLE/payload/pause ownership evidence and current release gate.
