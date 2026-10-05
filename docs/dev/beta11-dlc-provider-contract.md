# Beta11 DLC provider contract

The shared interface is `DropSpace.Core.Abstractions.IDlcPackageProvider`.
Register each adapter as a singleton `IDlcPackageProvider` in `App.xaml.cs`.
`DlcManagerService` consumes all providers through `IEnumerable<IDlcPackageProvider>`;
the settings DLC page projects its single transient state store.

CUDA integration owns its manifest, actual download/install/delete implementation and
persistent receipts. Its adapter supplies globally unique package IDs (for example
`ai-runtime-cuda`), localized purpose resource key, and nullable manifest download bytes.
`InspectAsync` returns verified installation, presence of owned partial/invalid artifacts,
actual installed bytes and hardware/download availability. Unknown bytes stay null.
Raise `PackagesChanged` after a manifest/catalog or externally performed package change.
Do not register an unreviewed research candidate or a runtime without a real download.

Download requires explicit consent and accepts normalized progress [0, 1]; 1 means transfer
finished, with verification/install possibly still in progress. It must not enable GPU or AI,
switch a model, or perform an automatic transfer. Existing valid packages are reused.
Delete owns cancellation and the existing AI maintenance/native-exit fence before removing
only the package's owned paths. It must not remove lyric caches or user files. CUDA must
reuse its existing GPU/backend downloader rather than introduce a DLC-specific downloader.

The manager serializes operations and inspection, exposes cancel/progress/retry, and keeps
downloads alive when navigation or display language changes recreate the page. Restart
reconstructs installations and resumable artifacts by local provider inspection; DLC does
not persist a second installed flag. Legacy models appear only if owned artifacts exist and
are removal-only. The selectable model menu remains the existing two Q8 profiles.

This branch does not modify GPU settings or implement CUDA packages. Registering the CUDA
provider is the remaining integration point. No tests/model calls/test CI are authorized.

## Page and integration surfaces

Settings navigation order is General, Island, Widgets, **DLC**, System Activities,
Devices, Updates, About. Both shipped languages keep the `DLC` name. The existing
WinUI settings navigation receives an independent first-level page, not a nested
Music section. `DlcPage` puts verified downloads above available downloads; every
card shows its name, short purpose and actual on-disk bytes, plus separate download
bytes where relevant. Unknown bytes display Unknown/未知. Partial downloads remain
resumable and removable; legacy models appear only when owned artifacts exist.

Music's model download/removal controls become a single navigation entry to DLC.
The AI settings card projects the manager's snapshots instead of maintaining its
own installed/partial/download flags. Missing-model enable/selection redirects to
DLC without downloading or changing the active AI setting. GPU settings handlers
remain unchanged; the local CUDA branch retains their ownership.

`AiModelDlcProvider` delegates transfer to `AiLyricsService.DownloadAsync` and
deletion to `DeleteModelAsync`. The selected model is disabled through the existing
settings transaction before deletion. `AiLyricsWorkLifetime` cancels inference and
drains the native resident before the package service touches owned files.
`AiModelPackageService.InspectAsync` serializes hashing and byte-count inspection
with that service's transfer/delete gate. Nothing clears the lyrics cache.

The publication branch's `8589560` CUDA contract distinguishes `download.bytes`
from actual unpacked component sizes. The CUDA adapter must supply those real
sizes rather than a guessed number. No placeholder CUDA card/download is present.
Local commit `65be1cc` was reported to contain another shared DLC service/navigation
event; its branch was not remotely available during this implementation. Before
integration, bind this page to the agreed single service (or adapt the local package
backend to this provider interface). Do not register two transient download managers
or wire GPU download actions to a separate state source.

## Verification boundary (no tests)

Manual source review and compilation only. Core and Infrastructure Release build
completed with zero warnings/errors using locked package restore. Full App Debug
build restored and compiled both referenced projects, then stopped because the Linux
environment cannot execute WinUI's Windows `XamlCompiler.exe` (Exec format error).
The new manager, page and existing dialog-lifetime helper separately compiled as a
library against the App's actual restored reference assemblies with warnings as
errors and the project's existing NoWarn 1701/1702. This is C# compilation only;
it does not establish full Windows App/XAML or visual behavior.

The supplied Library screenshot was resolved, but its image bytes could not be
materialized. Pixel review is therefore unconfirmed. No unit/integration/regression/
UI tests, CI, inference calls, downloads of models, or uninstall operations were run.
