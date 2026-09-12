# Preview.24 rebuild execution ledger

## Scope and source

User-authorized full plan: `E:/Dev/reference/Preview24-plan/DropSpace_Preview24_WindowsLocal_FullRelease_Codex_Package/DropSpace_v0.3.0-preview.24_WINDOWS_LOCAL_FULL_RELEASE_PLAN_for_Codex.md`.
All 15 target/failure images were opened individually before UI work.
Baseline: `820daa9ed02bd2943100a718b3c16acd1b93cdb8` (Preview.21).
Branch: `agent/v0.3.0-preview.24-rebuild-from-preview21`.
Do not merge Preview.22/23 or transplant their UI/state machine.
Preserve the pre-existing untracked `.codex/environments/` configuration.

## Checkpoints

- `13491f6`: SDK 10.0.401 in global.json, CI and release workflow. Solution restore passed.
- Baseline Release WinUI build passed on this Windows host: 0 warnings, 0 errors.
- `67c917a`: build 20348, settings schema 14 and migration coverage. Release/Debug WinUI builds: zero warnings/errors. Migration/concurrency: 7 passed; Core compatibility/settings: 19 passed. Compatibility and localization gates passed.
- No Preview.24 release has been prepared, pushed or published.

## Build notes

Use the checked-in CI commands. A build with an explicit global `RuntimeIdentifier=win-x64`
can regenerate Core/Infrastructure lock files with RID sections; rerun ordinary solution
restore before locked solution restore. Do not commit that incidental lockfile churn.
Git HTTPS direct transport was reset; per-command use of the existing Windows user proxy
allowed fetch/clone without changing repository or global proxy configuration.

## Required remaining order

1. Finish platform/schema validation and checkpoint.
2. Audit pinned WinIsland and Microsoft/Files/EarTrumpet sources; record parity and license boundaries.
3. Backend checkpoints: SMTC, lyrics providers/matching, process loopback/FFT, system observers, widget data.
4. Bounded material and focused/unfocused dark/light native smoke.
5. Compact media with real lyrics, PCM spectrum and actual XAML measurement; native smoke.
6. Independent Files/Music/Widgets pages preserving Preview.21 Files UI; native smoke.
7. Music page and working widget/settings editor; restart persistence smoke.
8. Transient activities and final product hardening.
9. Full local Windows gate, required GitHub checks, merge, release, five assets/checksums, website/API verification.
10. Synchronize and verify repository and installed `dropspace-codex` maintenance skills.

Do not mark completion or publish with any plan release blocker unresolved.
