# DropSpace v0.3.0-preview.16 — Full Hardening & Lifecycle Convergence Development Plan

> Status: **EXECUTION READY**
>
> Baseline: `main@8f41409e2265af5a0607335779c2fa0dbfcd53ac`
>
> Development branch: `agent/preview16-hardening`
>
> Target release: **DropSpace v0.3.0-preview.16**
>
> This plan is the authoritative implementation contract for Preview.16. Codex/implementers must not silently weaken, defer, reinterpret, or replace the requirements below. If a requirement cannot be satisfied exactly, the implementation must fail closed, preserve current user data, document the blocker, and leave the affected feature disabled rather than ship a partially secure or partially transactional behavior.

---

## 1. Release Objective

Preview.16 is a **full hardening release**, not a feature release.

Its purpose is to close the remaining lifecycle, security, resource-budget, OLE, native-callback, local-network, preview-decoding, and transactional-settings defects that remain after Preview.15.

Preview.16 must preserve all Preview.15 behavior that is already correct:

- local-first Temporary Space;
- Dynamic Island / overlay behavior;
- Windows 10/11 compatibility contract;
- current Preview.15 motion behavior and region ownership;
- current Clipboard transient-write retry;
- current DropLink body binding, replay protection, session admission, chunk mutation gate, finalization single-flight, and session retirement;
- current SQLite schema validation;
- current payload atomic-write behavior;
- current update manifest/hash validation;
- current high-contrast / reduced-motion improvements already merged;
- current hardcoding governance and design-token direction.

No new user-facing feature is required unless it is necessary to make a current feature correct and safe.

---

# 2. Release Blocking Rules

Preview.16 MUST NOT be considered complete while any of the following remains true:

1. Encrypted Internet Share master-key material can remain in a persistent or ordinary OS temp file.
2. Smart Drag can leave a session/candidate active because suppression swallowed a terminal signal.
3. A non-async OLE virtual-file source can be accessed after its valid IDataObject lifetime without first acquiring DropSpace-owned data.
4. Device Handoff / Cross-device Clipboard / Internet Share can report enabled or initialized when their owned resources are not actually active.
5. Settings rollback can stop after the first rollback error or replace the original error.
6. A managed exception can escape a Win32 subclass callback.
7. Remote image ingress can bypass the same decoded-memory/pixel budget enforced for local Clipboard images.
8. Preview image decoding can bypass pixel/decoded-memory limits because a file header parser did not identify dimensions.
9. Nearby receiver admission can exceed the configured maximum because of a race.
10. DropLink host Start/Stop can run concurrently without a process-owned lifecycle gate.
11. Local-network advertisement/bind address selection can disagree between DropLink, DNS-SD, and Nearby Share on a multi-adapter machine.
12. The Windows App test suite does not contain regression coverage for the Preview.16 lifecycle/security fixes that can be tested without a physical two-device setup.
13. CI is red on any supported release lane.

---

# 3. Severity Model

## P0 — Immediate Release Stop
Reserved for guaranteed data loss/corruption, deterministic supported-machine startup crash, plaintext credential/master-key exposure to unrelated processes, remote unauthenticated code execution, or unattended update trust bypass.

No new deterministic P0 was confirmed at the Preview.16 planning baseline.

## P1 — Release Blocker
Must be fixed before Preview.16 may ship.

## P2 — Must Fix Unless Explicitly Proved Non-Reproducible
Engineering defects with meaningful reliability, performance, UX-consistency, or defense-in-depth impact.

## P3 — Structural Follow-up
Refactors that improve maintainability but may ship later only if they are not required to close a P1/P2 item.

---

# 4. Workstream A — Internet Share Secret Material

## A1. Remove encrypted share QR master-key artifacts from `%TEMP%`
**Priority: P1 Security**

### Current defect
`MainPage.ShowShareDescriptorAsync()` constructs a QR code using the complete `descriptor.Url` and writes it to `Path.GetTempPath()/DropSpace-share-<shareId>.png`. The encrypted Internet Share URL contains the decryption material in its fragment. The current implementation writes this QR file even before the user chooses to open the QR view, and does not provide guaranteed deletion.

### Required implementation
Preferred implementation:
- render QR entirely in memory;
- display it inside the ContentDialog using `BitmapImage` / in-memory stream;
- never materialize the encoded QR to disk.

If an external OS viewer remains necessary:
- generate the file only after explicit user action;
- use a DropSpace-owned private staging directory, not generic `%TEMP%`;
- randomize filename;
- create with restrictive/default user-only ACL inherited from DropSpace data location;
- delete in `finally`;
- add startup cleanup for abandoned QR staging files;
- never log full URL or QR payload.

### Required tests
- secure descriptor does not create a temp file during dialog construction;
- closing dialog without opening QR leaves no file;
- opening/closing QR leaves no file after cleanup path;
- Nearby Share and Internet Share keep correct user-facing messaging;
- no test logs contain the full secure URL.

### Files
- `src/DropSpace.App/Views/MainPage.xaml.cs`
- optionally new `src/DropSpace.App/Services/ShareQrPreviewService.cs`
- `tests/DropSpace.App.Tests/*`

---

# 5. Workstream B — Smart Drag & OLE Lifetime

## B1. Suppression may block creation, never terminal convergence
**Priority: P1**

Placement-edit / candidate suppression may prevent a new drag candidate, approach transition, or Smart Drag promotion. It MUST NOT discard button release, drag-end, cancellation, timeout, owner/process disappearance, monitor disappearance, mode switch, explicit cleanup, or shutdown.

Refactor signal processing so suppression is evaluated only for candidate creation/promotion paths. Terminal signals must always pass to active-session convergence logic.

Required regression tests:
- session starts, suppression turns on, release arrives -> session returns idle;
- session starts, suppression turns on, timeout arrives -> session returns idle;
- session starts, placement edit begins -> no stale `DragApproaching`/`DragReady`;
- suppression present before any session -> no candidate is created.

Files:
- `src/DropSpace.App/Services/DragSessionDetector.cs`
- associated Core/App tests.

## B2. Non-async-capable virtual file provider lifetime
**Priority: P1 OLE Compatibility**

For a provider that successfully enters OLE async operation, asynchronous materialization may continue and `EndOperation()` must be guaranteed in `finally`.

For a provider that does not support or cannot start OLE async operation, DropSpace must synchronously acquire/copy the required `STGMEDIUM` data into DropSpace-owned memory/staging before returning from `Drop()`. No later code may call `GetData` against the source IDataObject after `Drop()` returns.

Do not re-enable unsupported `TYMED_ISTORAGE`. Supported virtual-file materialization remains bounded to validated `ISTREAM/HGLOBAL` paths.

Required tests:
- async-capable provider lifecycle;
- non-async provider ownership transfer before Drop returns;
- delayed-render stream;
- source throws after Drop return -> DropSpace no longer depends on it;
- cancellation cleans all staging files.

Files:
- `src/DropSpace.App/Services/OleDragDropService.cs`
- `src/DropSpace.App/Services/VirtualFileMaterializer.cs`
- `tests/DropSpace.App.Tests/*`

---

# 6. Workstream C — Transactional Service Initialization

Engineering rule: initialization flags are committed only after all required owned resources are established. Disable/Dispose always inspect real owned resource state and must clean partial initialization.

## C1. CrossDeviceClipboardService
**Priority: P1**

- do not set `_initialized` / `IsEnabled` before identity acquisition and subscriptions succeed;
- if initialization fails/cancels, remove any subscriptions/resources created during the attempt and return to a retryable state;
- a later identical Enabled setting must retry initialization;
- disable is idempotent.

Tests: failure after first acquisition, cancellation mid-init, retry after failure, disable after partial init, repeated enable/disable.

## C2. DeviceHandoffService
**Priority: P1**

Track actual resource ownership, not one boolean: desired enabled, DropLink host running, DNS-SD registration active, discovery usable, event subscriptions active, fully initialized.

- any failed enable attempt unwinds every successfully acquired resource;
- disable always stops host/registration if either exists even when `_initialized == false`;
- no listening HTTPS host remains after the setting is disabled;
- repeated calls are idempotent.

Fault-inject after identity creation, host start, DNS registration, subscription, and final commit. Each must end with zero leaked resources.

## C3. SecureInternetShareService revoke recovery
**Priority: P1**

- use an initialization gate/single-flight;
- only commit initialized state after `LoadAllAsync` succeeds;
- failed/cancelled recovery remains retryable;
- do not lose the ability to revoke already-created shares because of one transient startup failure.

Tests: first recovery throws then succeeds; cancellation remains retryable; restored handles become visible; logs contain no credentials.

---

# 7. Workstream D — Settings Transaction Correctness

## D1. Best-effort rollback of every committed subsystem
**Priority: P1**

Current rollback steps are sequential and not individually isolated. A rollback exception can stop later rollback operations and overwrite the original forward-path exception.

Required algorithm:
1. capture `originalException`;
2. track which forward steps actually committed;
3. rollback committed steps in reverse order;
4. each rollback runs inside its own try/catch;
5. collect rollback failures for diagnostics;
6. always attempt all remaining rollback steps;
7. restore UI preflight state;
8. rethrow the original exception with original stack;
9. log rollback failures separately without secrets/paths.

Suggested helper: `SettingsTransactionRollbackCoordinator`.

Tests must inject a unique exception into every forward step and rollback step. Assert original error survives, all eligible rollbacks are attempted, candidate settings are not left active where rollback succeeds, and status never claims settings saved.

Files:
- `src/DropSpace.App/ViewModels/MainViewModel.cs`
- test fakes.

---

# 8. Workstream E — Native Callback Safety

## E1. Clipboard subclass callback must be never-throw
**Priority: P1 Native**

`WindowSubclassProc` must not expose subscriber exceptions to Win32.

Preferred design:
- native callback samples sequence/time/counters;
- enqueue a managed notification;
- return from callback safely;
- subscriber notification executes outside native callback.

Minimum acceptable design:
- every event invocation individually wrapped;
- exception logged in bounded/redacted form;
- callback always returns safely.

Do not introduce blocking waits inside the subclass callback.

Tests: throwing `ClipboardChanged` and `StatusChanged` subscribers do not stop later notifications and listener remains registered.

## E2. Global hotkey subscriber isolation
**Priority: P2**

A thrown `Invoked` handler must not terminate the hotkey message thread. Isolate invocation, keep the loop alive, and record a bounded diagnostic.

---

# 9. Workstream F — Unified Image Ingress Budget

Create/reuse one shared rule for local Windows Clipboard, Cross-device Clipboard, Share Target bitmap, stored image preview, thumbnail generation, and image transforms where applicable.

## F1. Remote Clipboard pixel/decoded-memory bypass
**Priority: P1/P2**

Before persisting a remote image:
- verify encoded byte limit;
- decode metadata only;
- validate width/height;
- validate `width * height`;
- validate estimated decoded allocation;
- reject before full bitmap materialization if over budget.

Use the same policy object as local Clipboard where possible.

Tests: compressed oversized dimensions rejected; normal PNG/JPEG accepted; malformed metadata rejected; configured `MaxImagePixels` honored.

## F2. Preview decode safety
**Priority: P1/P2**

Do not treat custom header parsing as the final authority.

Required:
- use Windows decoder metadata preflight for all platform-decodable image formats;
- apply `MaxImagePixels` and decoded-memory budget;
- request bounded decode size (`DecodePixelWidth` or equivalent) where supported;
- malformed/unsupported image returns fallback, not process-level failure.

Explicitly test TIFF, WebP VP8, WebP VP8L, WebP VP8X, PNG, JPEG, GIF, BMP.

---

# 10. Workstream G — Preview & Quick Action Capability Contracts

## G1. PDF preview memory
**Priority: P2**

- remove full-file ASCII conversion/page-count regex as an authority;
- use `PdfDocument.PageCount`;
- avoid duplicate full-file in-memory copies where possible;
- preserve existing raster pixel bound;
- retain cancellation and disposal.

## G2. QR capability preflight
**Priority: P2**

`Evaluate()` must not advertise QR as available when payload exceeds encoder capacity. Add bounded UTF-8/QR capacity preflight, return unavailable deterministically, and repeat validation during execution.

## G3. Media capability semantics
**Priority: P2**

Do not claim codec-backed preview support based solely on extension. Where practical use Windows media capability/source preflight; otherwise mark the provider as candidate and gracefully downgrade on decoder failure.

---

# 11. Workstream H — Local Network & Sharing Concurrency

## H1. Nearby receiver admission
**Priority: P1/P2**

Replace the `ContainsKey` -> `Count` -> `TryAdd` race with atomic admission. Never exceed `MaxNearbyReceivers`; repeated requests from an existing IP use no extra slot; cancellation/disposal stays safe.

## H2. One network-interface authority
**Priority: P1/P2**

Introduce `LocalNetworkInterfaceResolver` or equivalent. It must supply one coherent adapter/address decision to DropLink bind address, DropLink advertised endpoint, DNS-SD A record, and Nearby Share bind/URL.

Prefer operational reachable physical interfaces, route-consistent private IPv4, and avoid virtual/tunnel-only adapters when a better physical route exists.

Synthetic adapter tests: Wi-Fi only, Ethernet only, Wi-Fi + Hyper-V, Wi-Fi + WSL, Wi-Fi + VPN, Ethernet + Wi-Fi, no private IPv4.

## H3. DNS-SD registration lifecycle
**Priority: P2**

Serialize Register/Unregister, remove check-then-create race, and ensure advertised address equals resolved DropLink endpoint address.

## H4. DropLink Host lifecycle single-flight
**Priority: P1/P2**

Add lifecycle gate and explicit state: Stopped, Starting, Running, Stopping, Disposed.

Guarantees: concurrent start creates one host; stop during start converges deterministically; repeated stop is idempotent; dispose cannot race with start; endpoint exists only for a running host.

## H5. Finalization maximum lifetime
**Priority: P2**

`Verifying` must not be permanently exempt from timeout. Add a bounded finalization lifetime with safe cancellation/failure and later staging cleanup.

---

# 12. Workstream I — Revoke Store Capacity

## I1. Save-side bounded retention
**Priority: P2**

`MaximumPersistedRecords` must be a storage invariant, not just a `LoadAllAsync().Take()` behavior.

- delete expired records;
- remove malformed records when safe;
- sort valid records deterministically;
- preserve newest active handles;
- prune surplus oldest handles after successful save;
- never silently delete the just-created handle.

Tests: >128 valid records, expired+active mix, corrupt mix, deterministic newest-record restoration.

---

# 13. Workstream J — Hotkey Parser & Update Single-flight

## J1. Strict hotkey grammar
**Priority: P2**

Reject duplicate modifiers (`Ctrl+Ctrl+A`), multiple non-modifier keys (`Ctrl+A+B`), missing modifier, missing key, unsupported key, and empty segment patterns. Accept exactly one non-modifier key and one or more unique supported modifiers.

## J2. Update cancellation ownership
**Priority: P2**

Shared update operations must not be owned by the first caller's cancellation token. Prefer an internal operation token/shared task and per-caller `sharedTask.WaitAsync(callerToken)`. A caller cancelling its wait must not cancel work another caller awaits, except explicit app shutdown.

---

# 14. Workstream K — Cleanup Error Preservation

## K1. Share Target staging cleanup
**Priority: P2**

Cleanup failure must never replace the original processing exception. Use a shared best-effort cleanup helper or preserve the original exception, log cleanup failure, then rethrow original. Apply the same rule to similar temporary-artifact cleanup sites.

---

# 15. Testing Program

## 15.1 Required automated test projects

### DropSpace.Core.Tests
Add/extend shared image budget, pure hotkey grammar if moved to Core, adapter selection policy if extracted as pure model, and relevant state-machine tests.

### DropSpace.Infrastructure.Tests
Add/extend InternetShareRevokeStore capacity, Nearby receiver admission, DropLink lifecycle policy where abstractable, preview provider input limits, and secure sharing behavior.

### DropSpace.App.Tests
Preview.16 MUST substantially expand this project with Smart Drag suppression convergence, OLE async/non-async lifetime, Cross-device Clipboard init rollback/retry, Device Handoff partial cleanup, Secure Internet Share retry, Settings rollback isolation, Clipboard callback failure isolation, QR capacity preflight, share QR memory-only behavior where testable, image decoder preflight, hotkey parser and invocation isolation.

## 15.2 Windows CI requirements

Run Core, Infrastructure, App Windows tests, x64 en-US, x64 zh-CN, portable self-contained build, installer build, upgrade/uninstall checks, current smoke suite, and new Preview.16 targeted smoke tests. No test may be skipped solely to get green.

## 15.3 Physical Windows validation matrix

Automated CI does not replace physical validation.

OS: Windows 10 minimum supported baseline; Windows 11 stable; Windows 11 preview if available.

Display: 100%, 125%, 150%, 200%, mixed-DPI dual monitor.

Drag/OLE: Explorer files/folders, virtual async provider, virtual non-async provider, browser drag, non-file drag, cancellation/release during placement edit.

Network: two real LAN devices, Wi-Fi + Hyper-V/WSL, Wi-Fi + VPN, adapter transition while DropLink enabled.

Accessibility: Windows Animation Effects runtime toggle, High Contrast, keyboard-only access.

---

# 16. Implementation Order / Commit Strategy

1. **Preview.16 execution contract** — add this file.
2. **Secure share QR secret containment** — memory-only QR + tests.
3. **Smart Drag suppression convergence** — tests first where practical.
4. **OLE non-async virtual-file ownership** — lifetime tests.
5. **Transactional service initialization** — CrossDeviceClipboard, DeviceHandoff, SecureInternetShare.
6. **Settings rollback coordinator** — reverse-order best-effort rollback and original exception preservation.
7. **Native callback / hotkey isolation** — Clipboard subclass, hotkey callback, strict parser.
8. **Unified image ingress budget** — remote Clipboard, Share Target where necessary, Preview decoder preflight.
9. **Preview capability + PDF memory cleanup** — image, QR action, PDF, media capability semantics.
10. **Network interface authority + concurrency** — resolver, Nearby admission, DNS-SD lifecycle, DropLink lifecycle.
11. **Revoke-store capacity + update cancellation + cleanup preservation**.
12. **Test/CI hardening and final Preview.16 execution report**.

Keep commits reviewable and logically isolated. Do not combine unrelated fixes.

---

# 17. Non-Regression Requirements

Preview.16 must not regress Preview.15 region/native-region correctness, drag-ready same-state dedupe, DropLink authenticated body hash binding, pairing capacity, replay cache, chunk mutation serialization, complete single-flight, session retention cleanup, SQLite schema validation, settings size/read policy, Windows image transform codec preflight, logger retry, Clipboard transient write retry, updater hash verification, unattended publisher trust gate, portable/installer distinction, Win10 fallback, reduced motion, High Contrast fixes, or idle rendering ownership.

---

# 18. Coding Constraints

1. No new unbounded dictionaries, queues, tasks, file reads, or request bodies.
2. No fire-and-forget Task without explicit ownership and exception observation.
3. No `async void` except framework event handlers.
4. No network listener without an explicit lifecycle owner.
5. No temp artifact containing secrets unless unavoidable and guaranteed cleanup exists.
6. No native callback may throw into native code.
7. No mutable service should use a single boolean when multiple independently acquired resources define real state.
8. No UI capability should claim support execution cannot satisfy.
9. No cleanup exception may hide the primary exception.
10. No new hardcoded OS/build/timeout/size magic number when an existing policy/token is the authority.
11. No path or credential-bearing URL in logs.
12. Security-sensitive comparisons use fixed-time comparison where applicable.
13. Cancellation must not create partially committed durable state.

---

# 19. Observability Requirements

Add bounded diagnostics for service initialization attempt/success/failure category, rollback failure category, selected network adapter category, memory-only QR rendering, OLE async capability presence, Smart Drag terminal cleanup under suppression, image ingress rejection category, native callback subscriber failure count, revoke-store prune count, and DropLink lifecycle transitions.

Do not log secure share full URLs/fragments/master keys, bearer authorization, paired-device secrets, raw Clipboard payloads, or unnecessarily sensitive filesystem paths.

---

# 20. Preview.16 Definition of Done

Preview.16 is DONE only when:

- all P1 items are implemented;
- all P2 items are implemented or have concrete reproducible evidence proving the issue does not exist;
- Windows CI is green;
- App.Tests has meaningful new lifecycle coverage;
- no Internet Share master key is written to generic temp storage;
- service initialization is retryable after failure;
- Settings rollback always attempts every committed rollback;
- Clipboard Win32 callback cannot leak exceptions across native boundary;
- local and remote images obey shared decoded-memory/pixel budgets;
- preview image decode obeys platform metadata budget;
- Nearby receiver count is atomic;
- DropLink/DNS-SD/Nearby use one network adapter decision;
- DropLink host lifecycle is serialized;
- documentation does not claim physical-device validation that was not performed;
- a final execution report records baseline SHA, final SHA, files changed, tests added, CI run IDs, physical validation performed, and remaining known limitations.

---

# 21. Final Execution Instruction for Codex

Proceed autonomously from this file.

Do not stop after analysis. Do not merely create issues or TODO comments. Do not mark an item fixed because a report says it is fixed; verify current source. Do not weaken a security/resource limit to make a test pass. Do not skip Windows-specific tests when the change is Windows-specific. Do not modify unrelated product behavior.

For each item:
1. inspect current implementation;
2. reproduce the defect with a focused test where possible;
3. implement the smallest robust correction;
4. run affected tests;
5. run the full relevant suite;
6. commit the isolated fix;
7. continue to the next item.

Continue until the Preview.16 Definition of Done is satisfied or an external, non-code dependency makes an item physically unverifiable. In that case, implement all code/test work possible, document exactly what remains for physical verification, and continue with the rest of the plan.
