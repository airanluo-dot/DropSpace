# Native quick actions: implementation and evidence

Fixed reference: Windows v0.3.2 Beta7, commit
`1cd4620dd58a67b0963b73487743137813b8864a`. This is a partial acceptance
record, not completion of ACTION-01 or the four preference profiles.

## Parameter ownership and business rules

`QuickActionDialogService.cs` requests parameters only for ResizeImage and
ConvertImage. The native `NativeImageExport` now retains an immutable item/action
snapshot. Changing selection cannot redirect the pending export. Default resize
format follows PNG/JPEG/BMP source extension with PNG fallback; conversion starts
with JPEG. Original100%,75%,50%,25% and custom pixels retain baseline rounding,
paired-dimension validation, optional blank dimensions for conversion and aspect
fitting. Choosing a native directory is explicit; cancelling its picker leaves the
previous destination unchanged. Default output is the app's Exports directory,
independent of the download directory setting.

The owner retains cancelled metadata/codec work until it exits, refuses overlap,
rejects late state publication after dismissal and joins at shutdown. Native codec
calls are cooperative only between synchronous calls; cancellation after the
atomic commit cannot unpublish an already committed file. No hard codec deadline
or in-flight native cancellation acceptance is claimed.

Image size inspection now checks the256MiB encoded bound before metadata decoding.
Re-encoding strips arbitrary metadata. A JPEG metadata-clean action keeps JPEG;
BMP keeps BMP; unsupported source extensions export PNG. Image names carry
`-resized`, `-converted` or `-clean`, JPEG uses `.jpg`, QR uses the record title plus
`.qr.png`, and ZIP uses a single entry name or `DropSpace files`. Native bytes and
compression/codec output need not equal Windows bytes.

`ActionOutputNames` restores1000 exclusive collision attempts and160 UTF16-unit
portable stems. The additional native UTF8 byte cap reserves space for the
extension and collision suffix to fit the filesystem component limit. Native
case-sensitive/case-insensitive collision semantics come from the actual target
filesystem. Atomic publication performs a final cancellation check after fsync,
without replacing an existing file or deleting an already committed output.

The preference editor has four profiles, three nullable slots, six actual registry
choices, automatic/manual mode and profile/all reset. Previously saved enum-only
IDs stay decodable and readable; they are not advertised as installed services.
Duplicate choices clear the newly edited slot. Result labels reuse the ten imported
catalogs; checksum copying now follows the app clipboard owner/self-write path.

## Actual evidence and failures

- `image-export-owner-tests.log`: two targeted owner tests passed. They cover
  source/JPEG defaults, presets, paired-dimension rejection, immutable submitted
  parameters, cancelled operation retaining its slot, late-result rejection and
  closed-owner admission. They use controlled IO closures, not a live codec UI.
- `image-export-native-build.log` passed. Actual isolated
  `ImageExportNativeSmoke` imported an authored400×200 JPEG through the native
  file picker. Resize defaulted to JPEG/original400×200;50% showed200×100. Native
  folder selection retained `Chosen Exports`; Export produced a200×100 JPEG.
  ImageIO inspection found the source's invented GPS field absent from output.
  Source SHA256 remained unchanged. Native Convert/custom100×blank rejected
  incomplete dimensions without closing; Cancel returned to the workspace.
  Normal Quit left no running app. These observations apply to that build.
- Screenshots and AX records are under `build/ImageExportNativeEvidence/`, with
  source hash and `initial-output-receipt.json`. This first output used the old
  filename `Image Export Source.jpeg`; it does not prove the later filename fix.
- Result/profile localization integration first failed compilation because the
  new checksum path omitted `try`. Corrected; `image-export-result-settings-retry-build.log`
  passed. The earlier failed log is retained.
- `image-export-output-contract-tests.log`: four selected cases passed0.113s,
  including the two owner cases, actual filesystem collision/Unicode/cancel,
  oriented image/PNG/BMP/clean-JPEG/GPS/source-preservation and QR decoding.
  `image-export-output-contract-build.log` passed. These are the final source
  build/tests, not a final-build native interaction acceptance.

## Remaining acceptance

Latest filename/default-directory/result/profile changes need actual native UI
verification. Profile persistence/reset/write-failure rollback, full hash/QR/ZIP
native flows, native picker cancellation/quit with nested sheets, concurrent
source replacement, permissions/disk-full/cloud/disconnected volumes, actual
in-flight image cancellation, huge/animated/multipage/color-managed inputs,
all-language VoiceOver/layout and minimum macOS26 remain open. Other quick-action
jobs still require a complete ownership/shutdown audit. No public release or full
1:1 claim follows from these checks.
