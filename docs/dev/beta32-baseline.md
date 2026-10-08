# 0.3.2 Beta1 baseline and release gates

Intake on 2026-10-08: official `main` is `58e774df9d9a72cac18855bd26f2330d91b983ea`.
Latest public App is `v0.3.1-beta.19`; no `v0.3.2-*` tag existed at intake. The existing clean
`codex/v0.3.2-beta.1-dlc` branch is reused. Untracked `.codex/` is retained. Existing draft
PRs #118, #119, #120 are reviewed and reused as sources rather than independently rebuilding
their fixes. Beta19's 20/20 execution record remains sealed.

## Actual feature and persistence baseline

| Contract | Existing implementation and invariant |
|---|---|
| Temporary Space, clipboard, pinned/search, file operations | MainViewModel, WorkspaceMutationUseCase, ItemProjectionService, SQLite item repository; original file references are never treated as owned payloads |
| Drag/drop, Smart/Classic/Disabled wake, hotkey | OverlayWindowService, DragSessionDetector, OleFileDataClassifier, VirtualFileMaterializer; existing OLE ownership and file admission remain authoritative |
| Main window, settings, tray | MainWindow/MainPage, NativeSettingsEditor, SettingsApplyCoordinator; HideToTray default and language page retirement remain |
| Island state and priorities | IslandExperienceCoordinator, OverlayStateMachine, IslandContentSelectionPolicy; Music default, manual selection, files during drag, notifications/volume and four built-in pages remain |
| Media, lyrics, AI, glow/animation | WindowsMediaSessionService, MediaExperienceService, AiLyricsService and existing worker; no provider, matcher, translation target, prompt, model, cache, acceleration or motion policy migration |
| Monitor/DPI | MonitorLayoutService, DisplayIdentityService, OverlayWindowInterop and placements; no geometry or display persistence change |
| Download/update and DLC assets | shared HttpRangeDownloader owns connection/bandwidth/transfer budgets; existing model/CUDA/NetEase providers and package receipts remain |
| Data | AppStoragePaths: data/settings.json, data/dropspace.db, payloads, caches, Updates, staging; new feature modules use only a separate Modules subtree |
| Defaults | settings schema 15; System language/theme/motion, English independent lyric target, Space launch, Win+Shift+Space, SmartExperimental wake, 30 days/1000 items, capture images/files/folders on, start with Windows on, Stable auto-check/download on and auto-install off, media/lyrics/widgets on, AI translation off, GPU Automatic/on, download connections 64/concurrency 2 |

The implementation source, rather than historical architecture prose, is the authority for
the exact values. New module preferences live outside AppSettings and do not change its schema.

## Startup, task ownership and shutdown

App applies the saved language before constructing windows/services; the composition root
retains the existing settings/database/item and media service graph. Shared downloads and
existing DLC pending-download recovery run in their existing place. New feature runtime
recovery is isolated and awaited without launching a worker when its state directory is absent.
Only enabled installed feature modules launch a worker; each process/session owns a cancellation
source and request generation. A module failure is contained and never restarts in a loop.

App.ShutdownCoreAsync keeps its established order: retire UI/activity; cancel background
owners; drain DLC/file downloads and network; dispose clipboard, media/native resources,
windows and DI. The new module runtime is drained before the shared download engine and windows.
Page/Island contributions are withdrawn before worker shutdown. Every UI subscription is detached
on presentation retirement. No DLC owns a Window, XAML loader, global service container or item DB.

## Distribution and upgrade

Keep Build-NextBetaCandidate.ps1's exact merged-main producer, pinned .NET/Windows SDK/Inno,
original self-contained portable payload, installer/identity/MSIX and updater channels; preserve
existing signing policy, component trust metadata and previously reviewed CPU/language bytes.
Unchanged AI model/CUDA archives are reused, never rebuilt. Beta19 assets/tags are immutable.
New tags/assets require a second live collision check before publication. Website source remains
ten-language/unified URL; English-only new release summaries/highlights. Publication must reuse
the validated final-tree package set and verify actual public assets/API/Pages readback.

## Review and functional evidence boundary

Before publication compare each row above against the final diff. Compilation and structural
checks do not establish runtime behavior. Concentrate the single 20-case task ledger on process
lifecycle/recovery, trusted package admission, UI retirement, no-module startup, long-lived
message language identity, website recovery and installed payload preservation. A known unresolved
regression or incompatible integrated source blocks publication. Record coverage limitations without
requiring a full regression matrix or claiming zero bugs.
