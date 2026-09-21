# NetEase enhancement implementation and acceptance

Date: 2026-09-21. Target: current Beta26 working branch. Publication remains paused.
The existing audit checkpoint is commit `84430f4`; its locally tested executable is
preserved at `artifacts/beta26/DropSpace-audit-checkpoint.exe` (SHA-256
`1b8c7cea72fb4689c1fcbf2e79dc9483a01d9d74ee42e1effd70f0d623569487`).

## Contract

Music has one application confirmation, then a background operation for detection,
official downloads, prerequisite installation, atomic deployment, restart and Windows
media verification. Navigation does not cancel an installation; app shutdown drains
it and recovery. There is no percentage invented from elapsed time.

Runtime communication is exclusively Windows SMTC/GSMTC. No plugin API calls, source
copy, modified InfLink build or DropSpace fork. Standard nullable shuffle/repeat state
and capability-gated commands are part of `IMediaSessionService`, usable by any player.

Installation is not acceptance. Metadata, artwork, playback state, actual nonzero valid
timeline and observed progress plus control behavior must pass. A complete native player
skips deployment. A failed upgrade restores the prior managed installation; first-install
failure removes this transaction's files. Existing unrelated plugin files/settings and
unrecognized loaders are preserved. Update/removal only act on verified managed bytes.

## Official upstream evidence

- [InfLink-rs 3.2.11](https://github.com/apoint123/inflink-rs/releases/tag/v3.2.11):
  release asset `InfLink-rs.plugin`; GitHub-provided SHA-256
  `f437b6ecda3915c147e5e195f4f5dae4c4a7534a8f8b46eae8636c401bf956f3`.
- [BetterNCM 1.3.4](https://github.com/std-microblock/chromatic/releases/tag/1.3.4):
  reviewed official DLL bytes pinned by architecture; no upstream digest exists for
  these historical assets. The application does not confuse chromatic 2.0 with BetterNCM.
- [InfLink configuration](https://github.com/apoint123/inflink-rs/blob/v3.2.11/packages/frontend/src/store.ts):
  SMTC defaults enabled, Discord RPC disabled. Existing preferences are not rewritten.
- [Microsoft prerequisite deployment](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170):
  architecture-specific VC runtime, official HTTPS source, valid Authenticode and Microsoft
  publisher, silent install without forced reboot. Windows elevation cannot be bypassed.

No Node/Bun/Rust installation is required for released plugin binaries. BetterNCM profile
resolution uses `BETTERNCM_PROFILE` (inherited process, user, machine) or `C:\betterncm`;
plugins go in `plugins`, not generated `plugins_runtime`. Restarted processes inherit the
resolved profile explicitly. Per-target PE architecture selects the loader.

Official documented player test ranges do not include this host's NetEase 3.1.40.205461.
Any compatibility claim requires actual acceptance; a successful download cannot establish it.
Read-only source/PE evidence is in `artifacts/netease-enhancement/research/findings.md`.

## Blocking upstream contract

Complete enhancement acceptance is **not met**. This is not a released success claim.
Official InfLink-rs 3.2.11 ignores the requested absolute Shuffle/Repeat values and dispatches
toggle commands instead ([handlers](https://github.com/apoint123/inflink-rs/blob/v3.2.11/packages/backend/src/smtc_core.rs#L260-L278)).
Its connection initialization does not publish the current mode; publication depends on a
later mode-change event ([frontend](https://github.com/apoint123/inflink-rs/blob/v3.2.11/packages/frontend/src/hooks.ts#L243-L269)).
Real samples remain null after starting playback and changing tracks. The control enabled
flags are true, which is not sufficient evidence of working standard setters.

The mode state machine loses information when leaving shuffle or AI modes. Inferring an
unknown original mode by changing it cannot guarantee restoration. No fork, copied code,
private JavaScript API, guessed state, or relaxed success gate is used to conceal this.
An upstream protocol fix is needed to satisfy the original complete-capability requirement.

## Validation record

- Core: 239/239; Infrastructure: 180/180; no skips. New complete-capability policy and
  generic mode contracts are included. Logs/TRX are under `artifacts/netease-enhancement`.
- Application: 112/112 after the persistent-manager connection amendment, including
  verifier 15/15. Core + Infrastructure + App: 531 passed, zero skips.
- Locked restore passed. The Release solution build passed; its only two warnings,
  PRI257/PRI263, concern MSTest's zh-Hans adapter satellite resource lacking a neutral test
  PRI entry. They do not affect the application resource pack; both application locales
  passed the localization gate (613 keys). The packaged test host is not the shipping app.
- Hardcoding governance, Windows 20348 baseline, release version and consistency gates passed.
- App tests require `-p:Platform=x64 -p:WindowsAppSdkDeploymentManagerInitialize=false`.
  The solution uses `Release|Any CPU` and maps its WinUI project to x64; a solution-level
  `Release|x64` invocation is invalid. Use the documented configuration, not a source workaround.
- The final documented WinUI application build passed with zero warnings/errors. The local
  candidate executable is `artifacts/release/DropSpace.exe`, SHA-256
  `3686dbb5e301e3d525bbd90aa090cf068d58ed1761415177666cc20ccc66ccc4`, product
  version `0.3.0-beta.26`. It is an unpublished test candidate, not a release artifact claim.
- Original user changes and `artifacts/beta26/DropSpace-audit-checkpoint.exe` are preserved.

### Target-machine findings

NetEase product version 3.1.40.205461 (file version 3.1.40.8853), x64; existing Microsoft
VC 14.51 runtime. The host initially had neither BetterNCM nor InfLink installed.

1. The native Music card and its confirmation were exercised. Cancel performed no component
   installation. Confirm downloaded official artifacts and deployed the verified bytes.
2. First iterations found process shutdown races, stale COM session failures, and temporary
   DLL deletion denial after exit. Fixes preserve per-process path checks, re-enumerate newly
   spawned children, isolate stale sessions, and retry only failed owned-file mutations.
   A pending receipt survived restart and was recovered. An already-recovered removal no
   longer reports NotManaged. Persistent recovery failure still preserves its receipt.
3. Official BetterNCM 1.3.4 + InfLink 3.2.11 did load on this host. Independent read-only
   SMTC samples observed title, artist, album, artwork, valid duration, advancing position,
   and enabled seek/shuffle/repeat controls. Weak and rich sessions coexist with the same
   source. Samples and events are retained in `live-smtc.jsonl`, `live-smtc-2.jsonl`, etc.
   The probe's `identity` field is a hashed track metadata key, not a COM session identifier.
4. A diagnostic manual launch/play was used to separate component compatibility from the
   installer bugs; it is explicitly **not** counted as one-click workflow success.
5. At 06:45 and 06:52 UTC, failed verification automatically removed both owned components
   and the pending receipt, then restarted NetEase. No manual cleanup was needed for these
   iterations. Native complete control acceptance still failed; no Enhanced status was shown.
6. Real Apple Music was played in its native app and paused through DropSpace at 06:54 UTC.
   DropSpace switched to its session, displayed the advancing native timeline, and kept
   unsupported seek/shuffle/repeat disabled. Apple Music remained paused afterward; its UI
   seek returned it to approximately the original 14-second position. This does not claim
   Apple Music exposes capabilities that its SMTC flags do not provide.
7. Final candidate, 06:58:13–07:00:25 UTC: one confirmation, automatic native check,
   official preparation/deployment, restart, then the complete 60-second verification
   window. Reusing the manager connection removed the immediate post-restart exit.
   The rich session appeared with album/artwork, valid 226.403-second timeline, seek and
   mode enabled flags. Its state remained Opened and modes null. Three bounded automatic
   playback attempts reported session-unavailable exceptions; final state restoration did
   not validate. The exact failing operation in that automatic command path is unresolved
   and is **not** attributed to the mode-source defect without evidence. Strict acceptance
   failed; automatic rollback removed both components and the receipt, and NetEase was
   restarted. No user action was required after the confirmation in this run.

The final machine remains on its original NetEase installation without the two enhancement
components. Generated BetterNCM profile/runtime files are preserved rather than recursively
deleting third-party data. Both players are paused. Repository and installed maintainer
skills were synchronized and both passed `quick_validate.py`.

### Remaining limits

- Full success/update/reinstall of a **verified complete** InfLink installation cannot be
  claimed while the official component's mode contract is incomplete. Fixture tests cover
  ownership, tampering, rollback, and persistence; they are not substitutes for that gate.
- A protected installation directory currently reports an access failure; automatic elevated
  file deployment is not implemented. The VC prerequisite path can request Windows elevation,
  which must remain a user-owned OS prompt. No missing-runtime/UAC install occurred on this host.
- Existing InfLink-disabled preferences are preserved rather than rewritten through a private
  browser storage API. Such configurations cannot be guaranteed to become enabled automatically.
- A missing/empty playback queue, missing metadata, or one-track queue may prevent full control
  verification. No fabricated metadata or track changes are used to force a pass.
- Physical DPI/accessibility/monitor matrices and successful full lifecycle across other player
  versions are not established by this test. Beta26 merge and publication remain paused.
