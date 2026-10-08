---
name: dropspace-maintainer
description: Maintain DropSpace built-in features, official optional modules, native UI, website, packaging and releases using the implemented host contracts.
---

# DropSpace development standard

This repository copy is the only canonical DropSpace skill. User instructions take precedence.
Use current source and focused documents; historical release evidence does not prove future changes.
Do not create or synchronize a personal duplicate.

## Feature placement and product rules

DropSpace is a local-first Windows workspace with Temporary Space, clipboard history and the native
Dynamic Island. The three host projects remain Core, Infrastructure and App. Keep music/lyrics/AI,
file/clipboard operations, settings, animation, multi-monitor/DPI, hotkeys, downloads/updates and
existing model/CUDA/NetEase mechanisms built in. Do not move/disable features to demonstrate DLC.
The host can continue adding built-in features.

Choose the host for default behavior, shared item/settings ownership, native state/window control,
low-latency platform integration or required availability without an optional install. Choose an
official DLC for independently useful optional code that fits bounded process/data/UI contracts.
Independent processes provide fault/lifetime isolation, not a permission sandbox. v1 accepts only
trusted official modules, not arbitrary third-party plugins.

Read relevant sections of PRODUCT.md, FEATURES.md, UX.md, DESIGN_SYSTEM.md and ARCHITECTURE.md.
[App/UI](references/app-ui.md) and [website/release API](references/website-api.md) are focused maps.

## Actual module owners and reusable template

Follow the single [feature-module contract](../../../docs/dev/feature-modules.md). Start here:

- `src/DropSpace.Core/Dlc/ModuleContracts.cs`: manifest, interface/protocol/UI versions,
  capabilities/dependencies/data format, compatibility and module-local translation resolution.
- `src/DropSpace.Infrastructure/Dlc/OfficialModuleCatalog.cs`, `official-modules.json`: official
  source/hash descriptors and manual future catalog.
- `ModulePackageStore.cs`: confined staging/version/data/state, shared downloads, ZIP verification,
  immutable installs, interrupted recovery and honest pending cleanup.
- `ModuleWorkerClient.cs`/`ModuleProcessJob.cs`: one process/session, bounded IPC, handshake,
  deadlines/cancellation/confirmed exit and Windows job ownership.
- `src/DropSpace.App/Services/Dlc/FeatureModuleRuntime.cs`: installed/enabled/running/transaction
  states, generation retirement, update rollback, settings and expiring Island contributions.
- `Views/MainPage.Modules.cs`/`Views/Settings/FeatureModule*.cs`: host-rendered navigation,
  declared pages/text/boolean settings/actions and safe withdrawal.
- `Views/Island/WidgetsExpandedView.xaml.cs`/`OverlayWindow.xaml.cs`: constrained host Island
  rendering that preserves built-in priority, motion, drag/focus and manual page choices.
- `modules/templates/worker`, `modules/sample`: reusable manifests, worker lifecycle/protocol,
  ten-language resources, production build/pack scripts and a real separately installed example.
- `scripts/register-official-module.mjs`: generates catalog records from actual ZIP bytes; publish to
  the exact official Release URL and keep versioned assets immutable.

Stable lowercase module/page/action/setting IDs and numeric module versions are independent of App
tags. Required capabilities/version/protocol mismatches fail closed; absent optional capabilities
need an explicit `omit` fallback. Check dependencies/OS/x64 and data compatibility before replacing
a usable version. Future official DLC may run on older hosts satisfying the declarations. Additive
fields retain old meanings; breaking changes explicitly advance the relevant contract version.

## Lifecycle, trust and data

Do not combine package IO, worker runtime and UI registration into a single giant service. The
App owns runtime startup/shutdown; runtime owns workers, request cancellation and generations;
Views own presentation/subscriptions only. No installed modules means no worker, idle expiry timer,
startup catalog network or directory creation. Reuse existing providers and singleton downloader
connection/transfer/bandwidth budgets.

Install/update: stage download → verify official source, size/hash/inventory and
compatibility → controlled switch/handshake → durable commit → register UI. Keep the old program
until success. v1 requires an unchanged data format; cross-format migration is unsupported until
forward migration and program/data rollback are both implemented. Postcommit cleanup failure must
never delete the new usable version or pretend to have restored an unreadable old data format.

Disable/uninstall: withdraw entry/Island immediately, reject new operations, cancel/retire requests,
stop within a deadline, confirm process exit, release locks, then delete only scoped package files.
Unresponsive workers are ended/isolated. Locked/unsafe cleanup remains `PendingCleanup` with retry
and startup recovery. Retain data by default; clearing it is separate and confirmed after disabling.
Never delete host DB/settings, other modules, symlink targets or original user files.

IPC has explicit request/session IDs, bounded inputs/outputs, error codes, deadlines and stale-result
checks. UI threads never synchronously wait for workers; faults require explicit retry and cannot
spin. Modules receive no host service container/shared DB. Native workers still run with the user's
permissions: do not describe process/job isolation as full sandboxing.

## Host UI and localization

Modules declare data-only names/icons/pages/settings/actions; the host renders validated controls.
No arbitrary XAML, code expressions or UI callbacks. Register entries only while enabled/running;
withdraw selected pages safely to a built-in page. Host owns Island state, windows, geometry,
animation, drag/focus, Music/Space priority and manual choice. v1 module content is lower priority:
idle resident compact text and bounded Widgets chips/flyouts; it never wakes or steals the Island.

Follow [interface-localization.md](../../../docs/dev/interface-localization.md) and
`localization/languages.json` for exactly ten offline UI languages. Module resources travel in
isolated catalogs; fallback is module English, never host overrides. Required visible declarations
need all ten translations. Host additions use `.resw` and explicit changed-ID reviews through
`scripts/check-localization.mjs`; do not bulk-refresh stale fingerprints to pass. App UI language
and permanent Simplified Chinese/English lyric translation targets stay independent. New release
summaries, notes and website highlights use English only; keep unified website URLs.

Long-lived host status owns `AppUiMessage` keys/copied raw arguments or raw bytes and renders in
the current language. Nested reasons remain messages; file names, external errors, lyrics/user
text are explicit literals. Never restore rendered-string reverse lookup, a global retained
registry or an unbounded cache. Installer main `[Messages]`/`[CustomMessages]` overrides are forbidden;
owned locale ISL resources and pinned section-aware Inno checks remain the single review path.

## Regression, budget and release

Establish the actual baseline: feature entry points/defaults, settings/DB schemas, startup/shutdown,
background/native owners, resource release and package/upgrade contracts. Review each affected
invariant against the final combined diff. Never mask regression by deleting/closing features,
changing defaults, clearing data or requiring reconfiguration. Preserve lyric/providers/models/
prompts/admission/cache/acceleration unless explicitly authorized.

Use the single [passive budget](../../../docs/dev/passive-test-budget.md): at most **20 actual
functional cases/scenarios for the whole project task**, including agents, local/Actions execution,
failures and retries. Never reset per module/PR/workflow/trigger. Freeze past release ledgers; reserve
each current scenario before running. A development/release or ordinary manual dispatch is not
permission for full suites. Source/resource/contract checks, builds, hashes and real public readback
are production integrity checks, not functional passes. Spend scenarios on high-risk lifecycle,
recovery, UI retirement, upgrade and no-module regression; document actual coverage boundaries.

Verify live main/PRs/tags/assets, reuse completed work and repeat collision checks before publishing.
Preserve signing/installer/portable/MSIX/updater and independent component contracts; reuse unchanged
model/CUDA bytes. `Build-NextBetaCandidate.ps1`/`next-beta-release-validation.mjs` qualify full PR
App/XAML compilation then exact merged-main packaging; `ci-release-promotion.mjs` binds the reusable
bundle. Never enable broad fallback diagnostics without explicit authorization. Known regressions,
compatibility failures block publication; report them plainly.
Verify real Release/downloads/update/API/Pages/module catalog status after completion; metadata
alone is not an actual successful download. Report outcome/files, checks actually run, failures/
retries, links, implemented module limits and material risks in Chinese.
