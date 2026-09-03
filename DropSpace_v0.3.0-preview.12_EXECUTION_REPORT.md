# DropSpace v0.3.0-preview.12 — Execution Report

Execution date: 2026-09-03 UTC. This report records the Preview.12 receiver-memory hardening and native Smart Drag smoke extension against the full-audit plan DropSpace_v0.3.0-preview.11_FULL_AUDIT_HARDENING_PLAN.md.

## Decision

Preview.12 is a CONDITIONAL PASS for shipping as a public prerelease. The code, automated regression gates, Windows CI, release bundle, GitHub Release, website deployment, live release API, latest-change API, and public download links are verified. An unconditional full-audit pass remains blocked by the explicitly unrun real Windows OS/DPI/multi-monitor/native/manual acceptance matrix and by operator-deployed Worker/browser acceptance. No source or CI result is being used as a substitute for those cells.

## Git and pull request

- Base main commit:  ecd25413219562ddbc9bd57f4d908356eff0719f
- Implementation branch head: 6cc35f6a3eca8f49aea5d51fd3e4a7b281b32f7b
- Pull request: https://github.com/airanluo-dot/DropSpace/pull/41
- Merge method: squash
- Merged main commit: 353ae69b2a3e87823efea810fa9bf707e2fd0818
- The implementation PR changed ten files; no historical release or asset was overwritten.

## Changes delivered

- share-worker/src/index.js now prefers showSaveFilePicker and decrypts, hashes, length-checks, and writes one plaintext chunk at a time to a selected file. A failed length or SHA-256 check aborts the destination.
- The browser fallback is explicitly capped at 256 MiB. Fallback buttons are disabled while active, fallback downloads are serialized page-wide, and the shared lock remains held through temporary Blob URL cleanup.
- The receiver SHA-256 implementation reuses one Uint32Array(64) schedule per hasher and keeps rotateRight outside the per-block process loop, removing the large-file allocation and closure churn identified by review.
- Receiver output keeps nonce-based CSP, contains no unsafe-inline, and treats displayName as text/download metadata rather than HTML.
- The native OLE verification smoke now moves the real cursor to a live monitor target, passes a stale candidate point, asserts that the probe centers on the live cursor, then restores the cursor and verifies bounded cleanup.
- share-worker/test/worker.test.mjs covers the nonce CSP, streaming path, bounded fallback, schedule reuse, Blob cleanup, and button-lock contract.
- RELEASE_VERSION, release notes, README, ROADMAP, share-worker README, and the repository maintenance Skill were synchronized to Preview.12.

## Validation

- PR Windows CI run 33800665201: success. The en-US and zh-CN jobs passed the Windows compatibility checks, 136 Core tests, 72 Infrastructure tests, application build, installer lifecycle, localized portable smoke, MSIX/identity packaging, and the native smoke path.
- PR Release bundle run 33800665310: success. It passed both localized portable smoke runs, installer /UPDATE, restart, upgrade, uninstall, byte freeze, metadata, checksum, and manifest generation.
- Main Windows CI run 33802078794 attempt 2: success. Its en-US job 100808234666 passed the same full lifecycle after a retry. Attempt 1 reported only a post-update graceful maintenance shutdown exit-3 timeout in en-US; the failed job was rerun without source changes and did not reproduce.
- Main Release run 33802078940: success. Build job 100803739113 and publish/verify job 100807541148 both completed successfully.
- Worker regression in the PR and main workflows: 4/4 tests passed, including receiver page generation.

## Release evidence

- Tag: v0.3.0-preview.12
- GitHub Release: https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.0-preview.12
- Published: 2026-09-03T20:36:53Z
- Status: public, non-draft, prerelease; no signing claim is made because the optional signing credentials were not used.
- Exact public assets: DropSpace-x64.msix, DropSpace.exe, DropSpaceSetup.exe, SHA256SUMS.txt, update-manifest.json.
- Published portable SHA-256: a07013b5511ae6e39d689de1b21c6aae3d9a1f51d2907ac1ee41f5fa28fed241
- Published installer SHA-256: 65094160f3606e1840003a3ff2dbd6cafff2e52bd56851b59078dd9b553f09c0
- Published MSIX SHA-256: 20e16a3dc9d0f2bcdd0b6bf2c66d861433d956c129ac2e7060ad5666c2034994
- update-manifest.json reports version 0.3.0-preview.12, channel preview, minimum Windows build 17763, and the same installer/portable sizes and hashes.

## Website and API evidence

- Pages deployment run 33803310292: success. Verification job 100807986123: success.
- Live releases API: https://airanluo-dot.github.io/DropSpace/api/v1/releases.json reports source github-releases and v0.3.0-preview.12 as a prerelease with all five canonical GitHub download URLs.
- Live latest-change API: https://airanluo-dot.github.io/DropSpace/api/v1/latest-change.json reports v0.3.0-preview.12, channel preview, title Receiver memory hardening, and the GitHub Release URL.
- Live English page: https://airanluo-dot.github.io/DropSpace/en/ contains the v0.3.0-preview.12 latest-change marker.
- The Release publish verification logged: GitHub Release, five public assets, manifest, checksums, release API, latest-change API, and live website verified.

## Residual gates

- Real Windows 10 1809/22H2 and Windows 11 version/build matrix evidence is still not available. Hosted Windows CI records build 26100 and validates compatibility policy, but does not prove every required OS row.
- Mixed-DPI one-to-three-monitor geometry, cursor feedback, border-leak, fullscreen, Explorer/provider drag behavior, accessibility, and complete manual Quick Action acceptance remain physical/manual gates.
- Two-device/network handoff and operator-deployed Worker/browser acceptance remain unverified. share-worker is reference deployment code and still requires the SHARE_COORDINATOR Durable Object binding plus deployment-level abuse/rate-limit controls.
- These residual items affect audit evidence completeness, not the integrity of the published Preview asset set. Preview.12 remains explicitly conditional until they are recorded.

## Skill synchronization

- The repository maintenance Skill was updated in the implementation PR and is present on main.
- The installed personal DropSpace maintenance Skill was validated and pushed at commit 3a60634df2696ef4fe3fade1bc7185d4ade74000.
- Both Skills carry the Preview.12 streaming, bounded fallback, serialized Blob-lifetime, CSP, native-smoke, and conditional-evidence facts.