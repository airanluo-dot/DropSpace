# Beta12 shared file downloads

Source: NovaClip v1.0.0-beta.8, commit b798c571dad1cae03c16129e4b4c43b85b428d7a.
The planner, adaptive scheduler, connection budget, bandwidth limiter, deadline,
retry executor, range worker, output reservation, filename sanitizer and persistence
worker are ported directly. The file-only manager and sequential range transport
adapt the same lifecycle and validator contracts to DropSpace without media types.

Production call chains:

- DLC DownloadPanel → NativeSettingsEditor.Downloads → DownloadManager → HttpRangeDownloader → ParallelHttpFileDownloader / validated sequential fallback.
- DlcManagerService → AiModelDlcProvider → AiLyricsService → AiModelPackageService → the same HttpRangeDownloader singleton → original model verification/publication.
- DlcManagerService → CudaRuntimeDlcProvider → AiLyricsService → CudaLyricsRuntimePackage → the same HttpRangeDownloader singleton → original manifest/hash/extraction checks → maintenance before backend activation.

- UpdateService → HttpUpdateDownloader → same singleton → size/SHA256 → atomic publication → existing installation verification.
- NetEase enhancement → InfLinkDeploymentService → same singleton → pinned loader hash / release plugin digest → transactional installation.
- NetEase enhancement or DLC runtime action → NeteaseRuntimeInstaller → same singleton → Microsoft Authenticode → existing prerequisite installer.

Global defaults: 64 connections (1–256), two active transfers (configurable from 1 to 3), unlimited bandwidth.
The shared transfer budget applies to files, updates, models and components. Increasing it
admits queued transfers immediately; decreasing it lets current transfers finish
before admitting more. The setting is restored before task recovery on startup.
Slider steps are integers: 0 = unlimited, 1–1024 MiB/s, multiplied by 1,048,576 into
bytes/s. The persisted fields join AppSettings.Validate, SettingsChangePolicy.Merge,
NativeSettingsEditor and SettingsApplicationCoordinator with rollback. Sliders
save after 400 ms of inactivity; the application-owned editor drains pending edits.

Ordinary task journals live under AppStoragePaths.Root/Downloads/Tasks, written
before starting/queueing. Per-task staging is under the destination's
.dropspace-downloads/<guid>. Pause retains fragments; cancel touches only known
owned artifacts. Restart restores unfinished tasks as paused, never starts model
downloads automatically. Managed download intent and the DLC pending journal are
saved before waiting on shared transfer slots. Finalizing ordinary tasks record a
SHA256 before atomic rename so a crash after publication can reconcile the result.

Range identity stores a hash of the original URL, strong ETag, size and original
part count. Changing the connection ceiling changes admission, not the saved
partition. Range rejection cancels and awaits all workers before fallback. Each
part validates Content-Range, version and length. Resume without a strong validator
restarts, including old model .partial data unless its complete hash already passes.
Both fragment and assembly disk use are budgeted. Models keep catalog-based paths;
CUDA keeps the unchanged component manifest identity, preserving installed caches.

Signed URLs are retained only where required in the local ordinary-task journal,
not emitted in logs or UI error messages. Managed catalogs reconstruct their own
trust policies; the generic downloader cannot turn an ordinary task into DLC.

Current targeted downloader validation: 10 updater checks and 2 managed-size checks
passed. These use bounded in-memory HTTP responses, covering real range slicing,
shared admission, validated resume/ignored Range, rejected redirects, hash/size,
path containment, cancellation and unknown-length bounds. No real updater installer,
large network download, cross-restart download, theme/scaling or 256-connection stress
run. Existing CUDA/lyrics live evidence remains in the separate Beta12 reports.

The owner subsequently authorized packaging/publication after code review. Beta11
assets remain frozen. The final Beta12 descriptor must bind to the final App source
commit and reuse the unchanged CUDA manifest/cache identity. New feature/dependency
downloads must join both the singleton engine and the DLC registry. App-version
update payloads use the engine but stay on the update page.
