# DropSpace v0.3.0-preview.15 unified-hardening execution report

## Scope

This report records the Preview.15 implementation slice against the DropSpace_v0.3.0-preview.15_THREE_AXIS_UNIFIED_HARDENING_DEVELOPMENT_PLAN.md. The release keeps the Windows 10 Build 17763 and x64 product boundary and does not turn hosted CI into real-device evidence.

## Implemented release axes

| Axis | Delivered boundary |
|---|---|
| Native/OLE | Physical HRGN identity, fail-closed OLE materialization classification, Smart Drag state/motion deduplication, topology enqueue recovery, exact visible-owner behavior |
| Motion/material/accessibility | Canonical motion ownership/tokens, reduced-motion and wall-clock behavior, UI-thread/native versus compositor visual ownership, capability-gated material, semantic high-contrast resources |
| DropLink/security | Actual-body authentication, pairing admission/rate/replay/session bounds, single-flight completion/finalization, atomic exact chunks, secret/revoke hygiene, Nearby policy/fallback |
| Clipboard/update/storage | Pre-decode image budgets and codec preflight, updater coordination, bounded settings/logging/schema/secret reads, scoped transient cleanup and failure preservation |
| SSOT/release | Central policy/route/artifact/motion owners, website kind contract, installer timing owner, SDK/analyzer lock, hardcoding governance |
| Tests/CI | Core/infrastructure/App policy and boundary coverage, Windows CI/release workflow gates, localized/packaging/website contract checks |

## Verification ledger

- Hosted Windows CI: run on the release candidate and must pass all current x64/en-US and x64/zh-CN build, test, packaging, lifecycle, smoke, governance, and release-contract jobs before publication.
- Official website deployment: release-driven sync consumes the published Release API and validates artifact kinds and official download identity.
- Real Windows OS/DPI/multi-monitor/OLE/accessibility/performance matrix: PENDING REAL-WINDOWS EVIDENCE.
- Real two-device DropLink/Nearby and operator-deployed Worker/browser matrix: PENDING REAL-WINDOWS EVIDENCE.
- ARM64 matching product evidence: not claimed; the public product statement remains x64-validated only.

## Reproduction

Use the commands and target rows in docs/test-plan/v0.3.0-preview.15.md. Keep logs redacted: no full paths, clipboard contents, plaintext keys, pairing secrets, or request bodies.

## Release status

Preview.15 is **CONDITIONAL** until the hosted release checks pass and the listed real-target evidence is attached. The report must be updated only with reproducible commit/run evidence; absent real-target evidence remains explicitly pending.
