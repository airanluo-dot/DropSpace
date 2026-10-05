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
