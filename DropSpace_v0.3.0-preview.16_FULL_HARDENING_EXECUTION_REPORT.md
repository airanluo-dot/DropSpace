# DropSpace v0.3.0-preview.16 — Full Hardening Execution Report

## Outcome

Preview.16 implements the uploaded full-hardening plan against the Preview.15 baseline. The source and hosted Windows publication gates are green. The release remains **CONDITIONAL** for evidence that requires matching physical Windows targets or an operator-deployed sharing backend.

## Reproducibility

- Baseline: `main@8f41409e2265af5a0607335779c2fa0dbfcd53ac`
- Branch: `agent/preview16-hardening`
- Target: `v0.3.0-preview.16`
- Final implementation source commit before this report: `a19fc4f9ba775098ea7c0b7119eaf07758628889`
- Version/release metadata commit: `cc539bb2e2dfb9687bf46d9a71b728332bfa1583`
- Authoritative plan: `docs/plans/DropSpace_v0.3.0-preview.16_FULL_HARDENING_PLAN.md`

The implementation was uploaded through GitHub tree/commit APIs from a clean comparison against the baseline. Unrelated line-ending-only working-tree changes were excluded.

## Implemented workstreams

- Memory-only secure-share QR rendering and no generic temporary secret artifact.
- Smart Drag suppression that preserves every terminal signal and converges placement/edit/shutdown sessions.
- Bounded async and non-async virtual-file OLE ownership, with guaranteed async `EndOperation` cleanup.
- Transactional, retryable initialization for Cross-device Clipboard, Device Handoff, and encrypted-share revoke recovery.
- Reverse-order settings rollback that isolates rollback failures and rethrows the original forward exception.
- Per-subscriber Clipboard subclass and hotkey callback isolation, plus strict one-key hotkey grammar.
- Shared Windows decoder metadata and decoded-memory/pixel budget preflight across image ingress, preview, thumbnail, Share Target, and transforms.
- Conservative PDF/media/QR capability behavior and decoder failure fallback.
- Deterministic private IPv4 interface authority shared by DropLink, DNS-SD, and Nearby Share; serialized host/discovery lifecycles and atomic Nearby receiver admission.
- Bounded DropLink finalization, save-side revoke retention, caller-independent update single-flight, and primary-exception-preserving cleanup.
- Focused App and Infrastructure regression tests for lifecycle, native boundaries, image fixtures, OLE lifetime/cancellation, network policy, update cancellation, and revoke retention.

## Files and documentation

The source changes are grouped under `src/DropSpace.App`, `src/DropSpace.Infrastructure`, and the matching App/Infrastructure test projects. New policy/adapters include `ImageDecoderPreflight`, `LocalNetworkInterfaceResolver`, `NativeSubscriberNotification`, and `SettingsTransactionRollbackCoordinator`. The repository maintainer skill is updated at `.agents/skills/dropspace-maintainer/SKILL.md`; its installed `dropspace-codex` copy was synchronized and validated with the skill-creator quick validator.

Release metadata and product documentation were updated in `RELEASE_VERSION`, `.github/release-notes/v0.3.0-preview.16.md`, `README.md`, and `ROADMAP.md`.

## Automated validation

The following hosted runs completed successfully for the final implementation/version source:

| Gate | Run |
| --- | --- |
| Windows CI push (en-US, zh-CN, Worker) | `33949730367` |
| Windows CI pull request (en-US, zh-CN, Worker) | `33949732236` |
| DropSpace Release validation (bundle, lifecycle, both localized smoke paths) | `33949732223` |

These workflows executed the repository's static release/localization/compatibility/hardcoding gates, Core/Infrastructure/App tests, x64 WinUI Release build, self-contained portable publish, pinned Inno installer lifecycle including `/UPDATE` and both uninstall modes, localized smoke, unsigned MSIX and identity package checks, release metadata generation, and Worker syntax/tests. The final en-US installer lifecycle path passed after binding ViewModel-owned startup background work to its lifetime cancellation token; an earlier run had timed out while waiting for that uncancelable storage/thumbnail work.

No local Linux test result is claimed because the Windows App SDK and Windows-specific test surface are publication-gated by hosted Windows CI.

## Physical and external evidence boundary

This execution environment did not provide physical Windows 10/11 machines, mixed-DPI displays, real Explorer or third-party OLE providers, accessibility/high-contrast interaction, two-device DropLink/Nearby LAN peers, or an operator-deployed Cloudflare Worker/R2 backend. Those acceptance rows remain conditional and are not inferred from source inspection or hosted CI. The release notes and roadmap retain that boundary explicitly.

## Publication follow-through

The release workflow is the authority for the final GitHub Release assets, checksums, update manifest, and website/API synchronization after the PR is merged. The tag and live API must be checked against `v0.3.0-preview.16` before calling the overall task complete.
