# Beta 7 focused execution record — 2026-10-20

Scope: consumer DLC cleanup, downloader staging visibility, and a dedicated keyboard shortcuts page. Owner authorized the fixes and direct publication, requested minimal necessary testing, then added the separate shortcuts page with explicit scope labels and no explanatory filler.

## Baseline and changes

Live main was 1657f1730ec28f086028937f10726cde5fee8bfb; latest public App was v0.3.2-beta.6. Existing untracked .codex and scripts/__pycache__ were preserved. No unrelated open PR was merged.

The embedded and public catalogs were already empty. The remaining module refresh button was removed; the existing DLC refresh now refreshes both inventories. Stale sample descriptors are filtered at catalog ingestion and sample cards are excluded. Future real official modules retain their runtime contracts.

The downloader retains its same-volume staging and resume identity. It adds Hidden without overwriting other attributes or touching payload bytes, checks reparse containment, repairs existing default/history folders and treats unsupported visibility updates as nonfatal.

Eight existing main-window accelerators now bind from additive persisted overrides; the old defaults and global gesture remain. Existing global/default collisions do not invalidate old settings. New assignments reject duplicate command gestures. Recording suspends the old native hotkey and local accelerators, commits through the existing settings transaction and restores registration on cancel/unload. The UI lists fixed context keys separately.

## Executed checks

- Native scenario 1: standalone Chinese settings page, global Ctrl+1 duplicate rejected; Ctrl+Alt+K recorded and saved; native registration confirmed; invocation displayed and focused the real Island. Reopening and cancelling restored the persisted Ctrl+Alt+K registration. Existing Win+Shift+Space failed registration on this machine and is now visibly reported.
- Native scenario 2: main-window Ctrl+Shift+S opened Settings; Temporary Space changed to Ctrl+Alt+1, persisted and immediately navigated to Temporary Space. The unrelated Settings binding remained usable.
- Native scenario 3: DLC live accessibility tree contained zero sample cards, one DlcRefresh and no ModuleCatalogRefresh.
- Probe scenario 1: settings round-trip retained global and local overrides, untouched comma/default bindings; duplicate rejected; legacy global/default collision remained loadable.
- Probe scenario 2: existing staging directory gained Hidden while sentinel resume bytes remained identical.
- Probe scenario 3: real sample fixture catalog parsed with zero public sample descriptors.
- App localization integrity passed after explicit review of 24 new IDs across nine translations; the recorder prompt was subsequently shortened and its one changed ID reviewed in each locale.
- Local App/XAML Debug compilation passed with zero warnings/errors after correcting pre-existing CUDA metadata binding. Initial compilation stopped on stale CUDA metadata; no runtime/model bytes were rebuilt.
- A packaged-development EXE launch failed before application initialization (WindowsAppRuntime COM class not registered, 0x80040154), producing a JIT prompt. The prompt was cancelled. Two self-contained build attempts encountered that suspended child lock; the second completed after dismissal, with five copy-retry warnings and zero errors. The self-contained build was used for the native scenarios above.
- Changes after native checks are limited to shortening recorder instructions and adding nonfatal default-folder visibility recovery. Final PR compilation covers the combined source.

Six focused functional scenarios executed locally. No full suite, model inference, language matrix, DPI matrix or installer upgrade test was run. Final CI compilation, one installer payload scenario and public readback are recorded by the associated PR/workflow receipts. Source/resource/hash checks are not functional cases.

## Boundaries

A Windows hidden attribute is not access control; Explorer's Show hidden items can reveal the working folder. System-reserved or other-app-owned chords require a different assignment. Fixed native control keys are listed but not remapped. No compatibility claim is made for every keyboard layout or global chord. Test settings were isolated under .codex/beta7-ui-data; ordinary user settings were not modified.
