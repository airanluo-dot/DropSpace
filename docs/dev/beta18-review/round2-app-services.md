# Beta 18 round 2 — App services full-source review

## Immutable scope and actual coverage

Reviewed commit `729d5b179f34ab2f852e7975f129431f77e8dc7a`, using the frozen source under `/workspace/scratch/beta18-round2`. The exact ordered scope is [round2-app-services-scope.txt](round2-app-services-scope.txt): **120 unique files / 27,123 lines**. Every scoped file was physically read from its first through its last line, including diagnostic/smoke source; this was not a diff-only or search-only review. Initially truncated tool output was replaced by complete smaller reads before counting coverage.

The scope file SHA-256 is `8eab7128cd696807cc3f0039b0fb4957490b8f7ded408e0f5ffc127a06c0c1e1`. All 120 frozen file hashes matched [round2-source-manifest.json](round2-source-manifest.json). The aggregate SHA-256 of the ordered UTF-8 records `path + NUL + file-sha256 + LF` is `b2d5a6fe6691762a8500677a7a90a6ab3130fe2e8447c9e28cddea47b7831533`.

| Physical full-read partition | Files | Lines | Covered surfaces |
|---|---:|---:|---|
| Services owner | 84 | 16,026 | Clipboard, handoff/sharing, settings, Undo/exit, image actions, displays/hotkeys/tray/updates/DLC, Acrylic/motion/halo/overlay adapters, remaining OLE helpers |
| Fresh media reviewer | 32 | 6,300 | All Media, Audio, Volume, Notifications, NetEase enhancement and media diagnostics; system activity orchestration |
| Fresh drag/OLE reviewer | 4 | 4,797 | Entire DragSessionDetector, EphemeralOleDragProbe, VirtualFileMaterializer and OleDragDropService |
| **Total** | **120** | **27,123** | **The entire declared App services scope** |

Canonical `AGENTS.md` and `.agents/skills/dropspace-maintainer/SKILL.md` were read. Findings below were derived from this frozen source. Round 1 reports were not used as new findings. Additional caller/contract reads included MainViewModel, OverlayViewModel, OverlayWindow, settings publication, ItemActionRegistry/ItemContentResolver, DeviceSecretStore, production DI, system activity presentation and relevant media ownership helpers; they do not inflate the scoped totals.

## Confirmed new findings

### S2-1 · P2 — Temporary secret-read failures can destroy a valid device pairing

`Services/DeviceHandoffService.cs:203–214,254–268`; `Infrastructure/Network/DeviceSecretStore.cs:46–58`.

Both reconciliation paths treat ordinary `IOException` and `UnauthorizedAccessException` from reading protected material as proof of corruption. They then delete the protected secret; pending-pairing reconciliation also deletes the peer row. `GetAsync` performs `FileInfo.Length` and `File.ReadAllBytesAsync`, so a temporary read lock/access failure can enter that destructive path even when the DPAPI file is valid and subsequently deletable. Ordinary peer discovery invokes reconciliation (`DeviceHandoffService:155–187`), so opening/discovering devices or automatic clipboard peer refresh can unexpectedly remove an established pairing. `File.Exists` also collapses unreadable and missing into the same null result.

Minimal fix: distinguish confirmed missing files and invalid/cryptographically unusable material from retryable I/O/access failures. Preserve metadata and protected bytes on retryable failures. Keep invalid-material cleanup and non-authorizing pending state; do not let an unreadable read authorize a request.

### S2-2 · P2 — Image exports discard EXIF orientation before removing metadata

`Services/WindowsImageTransformService.cs:136–138,161–169`; `Services/QuickActionDialogService.cs:338–346`. The clipboard image normalization path uses the same raw-bitmap re-encoding pattern (`Services/ClipboardCaptureService.cs:1498–1509,1531–1532`).

Resize, conversion and metadata stripping decode a raw `SoftwareBitmap` through the overload without an EXIF-orientation mode and then write a new image without source metadata. A portrait camera JPEG whose display orientation is carried by EXIF therefore exports its unrotated pixel matrix after losing the orientation tag. Target aspect-ratio calculations and the original/percentage size presets also use raw `PixelWidth/PixelHeight`, compounding the incorrect output geometry.

Minimal fix: explicitly decode with `RespectExifOrientation`, calculate output and preset dimensions from `OrientedPixelWidth/OrientedPixelHeight`, and continue omitting source metadata after baking its orientation into the pixels. This concerns native API semantics, not a measured image-quality result.

### S2-3 · P2 — Image action availability repeatedly reads files on the UI thread

`Services/WindowsImageCodecPreflight.cs:12–29,49–51`; `Services/WindowsImageTransformService.cs:288–301,334–337,371–374`; `ViewModels/MainViewModel.cs:885–893,1725,1793–1801`.

Each of the three image action evaluators calls the same synchronous codec preflight. It checks the file, opens it and reads its header before returning. Main projection construction evaluates every card synchronously on the dispatcher, and menu evaluation repeats the work. An available image on an unresponsive/network path can therefore block navigation or projection publication; reading only 16 bytes does not bound the wait for the filesystem. The repeated file reads are source-confirmed; elapsed blocking/CPU cost was not measured.

Minimal fix: use snapshot metadata for image-action presentation, without resolver/header filesystem reads. Retain exact header/codec validation at explicit execution, on a bounded background owner. This avoids a new capability-cache subsystem and keeps execution fail-closed.

### S2-4 · P2 — Settings can report a new hotkey while native registration retains the old one

`Services/SettingsApplicationCoordinator.cs:107–121,163–166`; `Services/OverlayWindowService.cs:711–714,864–875`; `Services/GlobalQuickPanelHotkeyService.cs:74–102`.

The settings transaction probes `CanRegister`, applies the UI preflight, then persists. The preflight raises OverlayViewModel's hotkey property notification, whose service subscriber starts an unawaited restart. Actual registration failure is only logged; the native hotkey service can retain the old gesture. A registration conflict or thread startup failure after the initial probe thus leaves persisted/presented settings at the requested gesture while the old key remains active or no key is registered. The transaction never observes the actual registration result.

Minimal fix: await actual hotkey replacement as a compensated settings transaction step and fail the transaction on unsuccessful registration. Restore the prior registration through compensation. Remove the independent fire-and-forget settings restart so it cannot race the transaction owner.

### S2-5 · P2 — Retired OLE callbacks can republish ownership after foreign classification

`Services/OleDragDropService.cs:925,940–941,953–969,1106–1121,1161–1164`; `Services/OverlayWindowService.cs:1006–1010,1255–1270,1398–1415`.

DragEnter checks disposal before calling the supplying COM data object. Foreign classification/path resolution can pump the STA while topology handling closes the old overlay or settings retires a Classic host. Dispose clears acceptance, but the suspended DragEnter subsequently restores acceptance/Copy effect and publishes callbacks without checking retirement. A retired visual registration can start visible drag ownership against newly rebuilt surfaces. A retired Classic host can instead throw when trying to expand its destroyed HWND and issue stale DragLeft cleanup that clears current ownership.

Minimal fix: capture registration generation before foreign work; recheck disposal/generation before publishing acceptance, effect or callbacks and after reentrant callback boundaries. Return `DROPEFFECT_NONE` for retired work without issuing cleanup against the current visual owner. Apply the same fence around Drop path resolution. Ordinary delayed drop completion guards and the current single accepted virtual-import admission were independently read and are not re-reported.

### S2-6 · P2 — A full volume queue discards the user's final volume/mute value

`Services/Volume/WindowsVolumeActivityService.cs:42,55–59,80–82,96–105,138–144`; `Services/SystemActivityExperienceService.cs:33,65,75–89`.

The bounded 32-event collection uses `TryAdd` without handling a full queue. It drops the newest events, including the final percent/mute value in a burst, then publishes the retained older values. There is no endpoint resampling after draining; sampling occurs only when attaching an endpoint. The system activity owner projects that retained snapshot into the island, which can display an incorrect final value until another change arrives.

Minimal fix: retain the newest bounded volume snapshot, using coalescing or DropOldest, while preserving device-change signalling and endpoint-generation rejection. Hardware callback rates and visible timing were not executed.

### S2-7 · P2 — NetEase verification performs synchronous publisher COM work on the UI dispatcher

`Services/NeteaseEnhancement/NeteaseSmtcVerifier.cs:37–39,59–73,109–147,491,503–523,532–544`; `Services/NeteaseEnhancement/NeteaseEnhancementService.cs:74,125,144`.

Production DI uses the dispatcher constructor. Verification and restart invalidation dispatch to the UI owner, then synchronously discover sessions/read Source, register/remove subscriptions and call `GetPlaybackInfo`/`GetTimelineProperties`. The 4/60-second managed deadlines cannot interrupt a stalled synchronous call on that thread, nor can deadline continuations execute while it is blocked. Ordinary inspection/enhancement reaches this path. General WindowsMediaSessionService already moves the same agile manager/session APIs into bounded retained native owners.

Minimal fix: use retained bounded background admission for verifier native discovery, reads, controls and subscription ownership; leave UI work to result projection. Timeout/cancellation may release the waiter but must retain the actual native owner and its admission slot until completion, rather than spawning unlimited stalled queries.

## Limits and disposition

This round ran **zero tests, builds, native probes, fixtures or remote operations**. Review used source reads, caller traces, line/count/hash inventory checks and collaboration; runtime Windows/WinUI/OLE reentrancy, audio hardware, GPU behavior, DPI/monitor transitions and semantic lyric quality were not executed. No performance measurements or native success claims are made.

No additional AI source/admission/publication, NetEase retained-backup, Undo expired-CTS, drag CTS, or accepted virtual-import finding was confirmed. Native callback lifetime concerns without an established failure path, controls restoring a different song after failed Next/Previous verification, and measured halo/polling cost were excluded. Scope includes all helper/diagnostic sources even where their execution is opt-in.

The seven findings were reported before production fixes. Root authorized their minimal fixes after all four full-source partitions completed. All seven fixes are now implemented in the canonical working tree. The frozen counts, locations and digests above continue identifying what was reviewed.

| Finding | Fix disposition |
|---|---|
| S2-1 | DeviceHandoffService separates invalid material from retryable reads; DeviceSecretStore reports null only for confirmed missing paths. Retryable read/access failures retain pairing metadata and protected bytes. |
| S2-2 | Image transforms and clipboard PNG normalization explicitly respect EXIF orientation before encoding; target geometry, returned clipboard geometry and resize presets use oriented dimensions. |
| S2-3 | All three image action evaluators use snapshot metadata without resolver/header reads. Explicit execution resolves and inspects the current source on four retained background admission slots; canceled waiters leave a still-running inspection owning its slot. No extension-only execution authorization or cache was added. |
| S2-4 | SettingsApplicationCoordinator awaits actual hotkey replacement as a compensation step, including rollback after persistence failure. OverlayWindowService no longer starts an independent settings restart. GlobalQuickPanelHotkeyService tracks the native thread's target before startup, allowing recovery to distinguish a late new registration from the old gesture. |
| S2-5 | OLE classification/path results remain local until callback generation/disposal checks pass. Fences surround reentrant readiness/publication boundaries; retired callbacks return NONE and cannot clear a newer drag in exception/finally cleanup. |
| S2-6 | One bounded wake signal accompanies the retained latest volume snapshot. Separate device-change signalling and endpoint-generation checks remain; the user's final value survives a callback burst. |
| S2-7 | NetEase verification/invalidation run on one retained native worker. Actual WinRT lifetime remains owned across timeout/cancellation; subscriptions retire callbacks before native removal. Disposal closes admission, requests cancellation and cleans up the agile manager after actual worker drain. BoundedMediaOperation exposes that drain task. |

Source-only verification: reviewed each fix diff and the affected ownership/caller paths, ran `git diff --check` across the 12 modified C# files successfully, and confirmed all 12 retain CRLF. Caller/call-site searches confirmed image presentation no longer calls codec preflight and that the independent hotkey restart is removed. No tests or new test cases were added or run. Native Windows image decoding, registration conflicts, publisher COM stalls, OLE pumping and audio callback bursts remain unexecuted; these are source fixes pending the subsequent fresh full-source review and any separately authorized native qualification.
