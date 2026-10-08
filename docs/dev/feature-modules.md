# Official feature modules, contract v1

The three production projects remain Core, Infrastructure and App. Existing item operations,
music/lyrics/AI, native windows/state, settings, downloads/updates and model/CUDA/NetEase packages
remain built in and independently evolvable. Optional features use a separate worker; no code
is loaded into WinUI. The canonical [project skill](../../.agents/skills/dropspace-maintainer/SKILL.md)
maps actual owners and must be followed for future features.

## Declaration and compatibility

`modules/templates/worker/module.template.json` is the reusable declaration. Production build/pack
scripts materialize `manifest.json` and exact payload byte/hash inventory. IDs are stable lowercase
names; versions are numeric three-part values. Declare host-interface range, protocol/UI versions,
required/optional capabilities, Windows build/x64, dependencies, data/readable-format range, entry
point, whitelisted icon/pages/settings/actions and module-local ten-language resources.
`Files` excludes the manifest itself; the official catalog descriptor hashes the complete ZIP.

`ui.pages`, `ui.settings`, `module.data`, `island.content` version 1 are implemented capabilities.
Unknown required capability/interface/protocol/UI/manifest versions fail closed. Unknown optional
capabilities require `Fallback="omit"`; unused additive v1 manifest fields may be ignored while old
field meanings remain stable. Breaking changes must advance the appropriate contract version.
Compatibility is independent of App tags: older hosts with the right interface/capabilities may
accept newer official modules through manual catalog refresh. An incompatible update preserves the
old installed/available version. Dependencies cannot be uninstalled while an enabled module needs them.

## Official catalog and module storage

The descriptor URL must exactly name the official repository's `dlc-<id>-<version>` Release and
`<id>-<version>-win-x64.zip` asset. Download redirects stay within trusted HTTPS GitHub asset
hosts. Official repository HTTPS is the source policy; lengths and SHA-256 check integrity.
No third-party catalog, arbitrary user package, publisher signature or permission sandbox is
provided. `modules/catalog.json` is manually refreshed from the fixed official repository URL;
invalid refreshes retain the previous catalog. Embedded records provide the initial catalog and
per-version saved records allow offline restart independently of future catalog changes.

After building a fresh module ZIP, run
`node scripts/register-official-module.mjs <ZIP> <module-id> <version>` to update both catalog copies
from its actual bytes. Publish the ZIP under the descriptor's exact official Release/asset name,
then publish the catalog. Never replace an existing version's assets.

Under `%LOCALAPPDATA%/DropSpace/Modules`, state, immutable packages, resumable staging and retained
module data are separate. Extraction rejects traversal/absolute/alternate-stream/device paths,
duplicates/case aliases, reparse paths, unknown types, extra/omitted files and oversized/hash-invalid
payloads. `ModulePackageStore` retains the official descriptor and original archive and checks
execution trust. `data/<id>/settings.json` contains raw module values. No AppSettings/schema/DB
migration occurs. User data is retained on uninstall; clearing it is a separate confirmed operation.

v1 updates must keep the same data format. Cross-format migration is rejected even if a broader
read range is declared. Do not claim automatic migration/rollback until both are implemented.
Original host files, other modules and user source files are never deletion targets.

## Lifecycle, IPC and resources

`ModuleInstallation`, `ModuleRunState` and `ModuleTransactionState` separately express installed,
enabled, real process and durable transaction state. Install/update downloads to staging, verifies
all compatibility/integrity before a controlled switch, handshakes, then commits/registers.
The old version remains until new activation succeeds. Postcommit cleanup cannot roll back a good
new version. Locked cleanup is honestly `PendingCleanup`, with retry and startup recovery.
Interrupted activation returns to the last committed program with a compatible data format.

Disable/uninstall withdraws UI/Island immediately, cancels the old generation and rejects new
operations before async stop. Each process/session and bounded request has explicit ownership.
The App retires/drains modules before shared downloads/DI/windows are disposed. Views detach their
own subscriptions and ignore expired callbacks. No installed module means no worker, idle expiry
timer, catalog network request or new startup directory. Faults never trigger an infinite restart.

`ModuleEnvelope` defines protocol/session/request ID, method, JSON payload and error code. Methods
are hello/action/settings/stop; result payload uses `ModuleReply`. Actual C# record field names are
PascalCase; documented method data is lower camelcase. One outstanding exchange per worker,
65,536-byte UTF-8 messages, eight-second request deadline, caller/module/App cancellation and
generation checks prevent late results reaching a replacement. Stop requests graceful exit for
two seconds, ends the process tree, then waits two seconds to confirm exit. Unconfirmed exit keeps
ownership and prevents claiming physical deletion. Stderr has a finite drain-volume bound.
The Windows job closes the worker tree with its host owner.
This **is not a permissions sandbox**: native official modules retain current-user file/network access.

## Host presentation and language

The host renders validated data through WinUI cards/lists/text and boolean forms/named actions;
modules supply no XAML, expressions, delegates, service container, shared database or windows.
MainPage registers only enabled/running entries. Withdrawal cancels a selected view and returns
focus/selection to an available built-in surface. Settings-only modules use the same host renderer.

Island submissions contain local compact/expanded resource keys, declared actions and an expiry
no more than 60 seconds ahead. The runtime expires them without an idle polling loop. Compact
text only occupies an already-visible idle resident host; it cannot wake/steal music/files/activity
or change manual pages. Expanded chips/flyouts use the Widgets padding without rearranging native
tiles. These bounded placements are v1 limits; arbitrary module layouts are unsupported.

Module catalogs cannot override host resources. Required visible declarations need all ten UI
languages; optional runtime lookup falls back to module English. Preserve independent permanent
Simplified Chinese/English lyric targets. Host long-lived messages store `AppUiMessage.Resource`
keys/raw/nested arguments or `Literal`/`Bytes`, never rendered-string reverse lookup. New release
summaries, notes and highlights are English. Follow the single interface-localization contract.

## Template and evidence

`modules/templates/worker` and `modules/sample` provide real build/pack entry points and a benign
independent executable. It acknowledges a boolean setting and returns localized expiring content,
does not touch existing features/user data, is never installed by default, and adds no built-in debug
panel. Review the final combined tree against the actual baseline and test high-risk boundaries.
Maintain one truthful task ledger including agents, Actions, failures/retries. There is no fixed
ceiling; select checks for concrete risks and minimize unnecessary execution.
Compilation/static checks do not prove lifecycle, UI, upgrade or no-module behavior. Use focused
checks; a known regression or compatibility failure blocks publication. Record actual limitations
without expanding into a full matrix or claiming zero bugs.
