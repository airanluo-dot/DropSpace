# Beta12 DLC inventory performance

## Confirmed code paths

- Model `InspectAsync` rehashed each final GGUF merely to display installation
  status, including the optional 7B model that was not selected for inference.
- CUDA's provider called `EnsureWorkerAsync` for passive inspection. That rehashed
  all worker/DLL payloads and could attempt embedded materialization.
- AI card loads and DLC page loads both requested a full refresh. A second request
  during inspection scheduled another complete pass.
- CUDA driver initialization was invoked from the catalog getter and multiple AI
  card render/selection paths; `IsNvidia` was a synchronous native probe each time.

These are confirmed redundant work and UI-thread execution paths, not a measured
breakdown of the user's previous startup latency.

## Implementation

- `AiModelPackageService.InspectAsync` now runs in the background, validates safe
  owned paths and the catalog's exact final filename/size, and counts partials and
  range artifacts without reading the payload. No new persistent cache is used.
- `CudaLyricsRuntimePackage.InspectAsync` checks the descriptor's existing App
  release/commit/manifest binding, required component/license paths and sizes, and
  owned artifact sizes under the extraction gate. It never materializes a runtime.
- `CudaRuntimeDlcProvider` probes driver compatibility once per refresh on a worker
  thread. Catalog enumeration and the AI card no longer initialize the driver.
  The card updates CUDA availability from the inventory snapshot; backend settings
  and real worker admission remain independent of the display result.
- `DlcManagerService` starts a background inventory refresh at application startup.
  Concurrent page requests coalesce and ordinary page loads reuse the session
  snapshot for 30 seconds. There is no periodic scan timer. Manual refresh/retry
  and package-change events bypass the interval. Mutation completion still inspects
  the affected package; retained status stays visible during subsequent refreshes.
- Cancellation and existing installation/removal gates remain in force. Ordinary
  file downloads and user files are outside this inventory.

## Integrity boundary

`DlcPackageInspection.IsInstalled` now describes files present at the expected
paths and sizes. It does not prove their content is still valid. Same-size external
modification is detected by full verification before actual use, not passive UI
refresh. No display snapshot is accepted as a worker/model trust token.

`GetInstalledPathAsync`, model download publication, CUDA download/archive/member
verification, `EnsureWorkerAsync` and retained `OpenWorkerLeaseAsync` hashing are
unchanged. Source restrictions, path protection, runtime manifest and exact archive
inventory checks, and safe native-resource maintenance remain in place.

## Validation scope

Source review and one necessary WinUI Debug compilation passed (0 warnings, 0 errors).
No full payload scan,
model download, additional inference, startup timing benchmark or UI regression was
run for this change. The change does not claim a measured startup speedup or extend
the separately recorded successful CUDA inference evidence. Delivery remains Beta12
source only, before packaging/publication.
