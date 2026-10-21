# Mac v0.1.0 Beta 1 release gate

The owner's 2026-10-21 scope change supersedes the earlier full-parity release gates. This release targets a **basic daily-usable native macOS public beta**, with Windows v0.3.2 Beta 7 as a functional reference. Historical PARITY, CORE-CONTRACTS and SETTINGS entries remain evidence and backlog, not a requirement to reproduce every Windows edge case before Beta 1.

## Essential acceptance

- [x] Final candidate launches; file/folder intake, open, copy, reveal, pin, remove and undo preserve source bytes. Final candidate file/folder, pin/remove/undo and source-byte checks passed; earlier open/copy/reveal evidence reused.
- [x] Common system clipboard text, URL and image capture, pause/resume, copy, search and cleanup. Release candidate general-pasteboard capture/pause/search/clear passed; original clipboard restored. Copy/resume evidence reused from earlier native tests.
- [x] Main window, menu-bar recovery, Island expansion/pages and saved settings. Final candidate window recovery, Island expansion/pages and persisted settings passed; earlier menu-bar evidence reused.
- [x] Apple Music metadata/control, lyrics and 1.8B translation. Reuse actual application CPU/Metal evidence; disclose authorization and model requirements.
- [x] Existing downloads/components/nearby sharing retain their recorded working paths. DropLink is experimental; internet sharing requires a configured service.
- [x] Release DMG/ZIP, ad-hoc integrity, public download bytes and copied-app launch. No Developer ID or notarization claim.
- [x] Website Mac/Windows separation, installation, known issues, release notes and a manual download/install update path. Do not claim validated automatic updates.

## Deferred without blocking this beta

QQ Music login; complete other-player controls; physical Windows interoperability; 7B inference on this 16 GiB host; exhaustive ten-language review; every display/system combination; rare edge cases that do not threaten data. Existing implementations are retained. macOS 26 is the deployment minimum; actual evidence currently comes from macOS 27.0.1.

## Current release state

Published 2026-10-21 as an independent prerelease, not GitHub Latest:

- [Official Mac page](https://airanluo-dot.github.io/DropSpace/macos/)
- [GitHub Release](https://github.com/airanluo-dot/DropSpace/releases/tag/macos-v0.1.0-beta.1)
- [DMG](https://github.com/airanluo-dot/DropSpace/releases/download/macos-v0.1.0-beta.1/DropSpace-macOS-arm64.dmg)

PR #133 merged after website, secret scan and Windows compilation checks passed. Tag points to `f08a3f76f94b215bd570ead31af23eae90427439`; signed product bytes match the tested build from `651b3a6e4798371d3823f53509e2bc54388a7b32`. Later changes were website seed, records and exact scanner false-positive fingerprints, not native product code.

Public HTTP downloads of both DMG and ZIP matched manifest sizes/SHA-256. Windows feed was unchanged excluding generatedAt. Chrome downloaded the DMG with quarantine; Finder copied it to an isolated Applications folder, the DMG was ejected, and Gatekeeper's Open Anyway flow succeeded after the owner entered the local administrator password. Welcome UI appeared. An isolated-profile relaunch restored two references and the pinned file. A normal quit, public-bundle replacement of the existing test application, relaunch and normal quit retained settings and source hashes. This is same-version replacement, not future-version migration or automatic-update validation.

Final local packages: `macos/dist/beta1-release-ready`. Public verification evidence: `macos/build/public-macos-beta1-verification.json` and `macos/build/Beta1PublicInstall/receipt.json`; durable summary is in EXECUTION.md. Actual OS: macOS 27.0.1 arm64. No Developer ID signature or Apple notarization.

Pages only permits the main branch. The tag-triggered deployment was rejected by that rule; the existing main-branch manual deployment succeeded (run 39535913942). For subsequent releases, publish assets then run deploy-website.yml on main with full_tests=false, and execute the Mac public verifier. Keep environment protection intact.
