# Beta.25 Beta.24 audit bug-fix ledger

This ledger maps every issue from the Beta.24 full-project audit to the Beta.25
implementation and available regression evidence. No SQLite schema migration is
required. “代码+测试” means the relevant deterministic test passes in the
current environment; “代码+静态” means the boundary is covered by source and
policy inspection but still needs the real Windows/native matrix.

## Lyrics and media

| ID | Severity | Beta.25 remediation and evidence | Confirmation |
|---|---|---|---|
| LY-01 | High | `LyricsQuery` carries a track identity; cache keys include identity and full duration. `LyricsService` accepts only provider candidates with identity and metadata/duration validation. `LyricsMatcher` rejects title-only authorization. | 代码+测试 |
| LY-02 | High | `MediaSessionSnapshot.TrackIdentity`, `IsSameTrack`, SMTC revision reads, and `MediaExperienceService` clear/bind lyric state at the same track boundary. | 代码+Core/Infrastructure 测试 |
| LY-03 | High | Title normalization preserves Live/Remix/Acoustic/version tokens; only publisher suffixes are removed. | 代码+测试 |
| LY-04 | Medium | Provider documents are rebound to candidate metadata and rescored; `LyricsService` validates the candidate after download before publishing it. | 代码+测试 |
| LY-05 | Medium | Fallback waits for a bounded quality window and selects the highest valid score instead of the first non-empty result; remaining requests are cancelled and drained. | 代码+Infrastructure 测试 |
| LY-06 | Medium | Lyrics HTTP JSON/invalid-data failures are converted into provider failures and remain eligible for fallback. | 代码+测试 |
| LY-07 | Medium | JSON numeric strings are accepted; Kugou milliseconds are normalized to seconds; provider identifiers are required before download. | 代码+测试 |
| LY-08 | Medium | LRCLIB consumes `plainLyrics` as well as `syncedLyrics`; plain text is represented as a bounded duration-bound document. | 代码+Parser/Infrastructure 测试 |
| LY-09 | Medium | TTML roles are case-insensitive and include translation/romanization variants; nested timing supports Apple-style absolute spans and relative child timing. | 代码+Parser 测试 |
| LY-10 | Medium | Enhanced-LRC final word ends at the line end rather than an arbitrary extra second. | 代码+Parser 测试 |
| LY-11 | Medium | The lyric timeline expires the final line at its declared end and does not keep it alive indefinitely. | 代码+Core 测试 |
| LY-12 | High | Invalid Apple/native timelines use a bounded estimated observation gap and retain the last trusted position across long lock/sleep gaps; valid seeks remain authoritative. | 代码+Core 测试；原生回归待真机 |
| LY-13 | High | Expanded Music now includes lyric lines, secondary text, current-line highlighting and status states; `MusicPage` refreshes the same bounded presentation. | 代码+构建；UI 真机待验证 |
| LY-14 | Medium | Local LRC selection checks bounded candidate files and filename metadata; it no longer authorizes a same-title file using only the query artist. | 代码+测试 |
| LY-15 | Low | Compact lyric measurement invalidates on theme, layout, text scale and XamlRoot/DPI changes. | 代码+构建；混合 DPI 待验证 |
| LY-16 | Medium | SMTC source discovery reads candidates independently and falls through after one broken session; subscriber exceptions are isolated. | 代码+测试；SMTC 真机待验证 |
| LY-17 | Medium | `LyricsQueryStatus` exposes Disabled/Loading/Found/NotFound/Failed; stale documents are cleared on reload and failure. | 代码+构建；UI 真机待验证 |
| LY-18 | Low | HTTP response charset/BOM handling and rune-safe truncation avoid UTF-16 surrogate cuts; parser XML is bounded and DTD-disabled. | 代码+Parser/Infrastructure 测试 |

## General application, Windows and release paths

| ID | Severity | Beta.25 remediation and evidence | Confirmation |
|---|---|---|---|
| G-01 | Low | Main-page `Ctrl+,` accelerator is explicitly wired to Settings through the numeric Win32 key value in code, avoiding a WinUI XAML parse failure; `Ctrl+Shift+S` remains available. | 代码+构建+Portable Smoke |
| G-02 | Medium | Escape first clears search and then returns focus to the items surface when already clear. | 代码+构建 |
| G-03 | Medium | Live clipboard projection is capped at 250 items while canonical retention remains repository-owned. | 代码+Core/Infrastructure 测试 |
| G-04 | Medium | Main-window manual drop enumeration and batch intake are capped at 2,048 unique paths with overflow feedback. | 代码+构建 |
| G-05 | Medium | Batch expansion is stored by `DropBatchId`, restored after reload/paging, and does not invent a header for ordinary keyset pages; search-only pages keep a usable representative. | 代码+构建 |
| G-06 | Medium | Overlay geometry, monitor/DPI conversion and native placement are inside checked exception boundaries; failure leaves the surface recoverably hidden. | 代码+构建；多屏/DPI 待真机 |
| G-07 | Medium | Visible text/URL drops always reset visual drag ownership and run common cleanup. | 代码+构建；原生拖放待真机 |
| G-08 | Medium | Rejected/cancelled DragOver paths reset the visual active state. | 代码+构建；原生拖放待真机 |
| G-09 | Medium | Mixed storage/text packages prioritize file-like StorageItems before text fallback. | 代码+构建；Explorer/Share 待真机 |
| G-10 | High | Clipboard and drag detector event delivery isolates each subscriber, and worker signal failures are recovered per event. | 代码+构建；原生回调待真机 |
| G-11 | Medium | Clipboard signals retain the newest sequence, retry read failures, and mark processing only after the bounded read/commit path succeeds. | 代码+构建；WM_CLIPBOARDUPDATE 待真机 |
| G-12 | Medium | Clipboard text is preserved without destructive `Trim`; classification and display use bounded content. | 代码+测试 |
| G-13 | Medium | Self-write suppression uses a bounded marker queue and rechecks the marker immediately before the durable clipboard commit. | 代码+测试；原生剪贴板待真机 |
| G-14 | Medium | Pause persistence is awaited and failure leaves visible state consistent with the durable settings snapshot. | 代码+构建 |
| G-15 | Medium | Clipboard file batches use one repository transaction and owned-payload cleanup on rollback. | 代码+Infrastructure 测试 |
| G-16 | Medium | Retention runs after successful capture and through the durable cleanup coordinator/outbox, including when no new item was captured. | 代码+构建；重启清理待真机 |
| G-17 | Low | Payload cleanup handles unauthorized access as a recoverable cleanup failure and does not falsely advance completion state. | 代码+构建 |
| G-18 | High | Virtual OLE materialization copies bounded medium ownership before non-async Drop returns, performs byte copy off the callback path, and rolls back confined staging on failure. | 代码+App 构建；Explorer/OLE 待真机 |
| G-19 | High | Startup recovery waits for a usable XamlRoot, activates the main window, and exits with a diagnostic marker if the UI cannot be created. | 代码+构建；Windows App SDK smoke 待环境 |
| G-20 | Medium | Preview dialog/XamlRoot construction paths dispose preview resources even when setup fails before the normal dialog lifetime. | 代码+构建 |
| G-21 | Low | Main-page text/URL dialog handler delegates through the common async exception boundary. | 代码+构建 |
| G-22 | Medium | DropLink manifest carries the negotiated chunk size; the host validates and uses it rather than assuming the default. | 代码+Infrastructure 测试 |
| G-23 | Medium | Remote clipboard loop guards are removed on failed local import/side effect and remote imports do not bounce back onto the network. | 代码+构建 |
| G-24 | Medium | Loop-guard identity includes origin device/event/sequence and payload dimensions, reducing false suppression of legitimate equal content. | 代码+Core 测试 |
| G-25 | Medium | Audio source identity is committed only after resolver success, so a failed Apple renderer lookup can retry. | 代码+构建；Apple Music 待真机 |
| G-26 | Medium | Related SMTC sessions are attempted independently; one broken candidate no longer publishes Empty over a valid candidate. | 代码+构建；SMTC 真机待验证 |
| G-27 | Low | Session/process discovery caps now cover the raw session list up to 512 candidates and no longer hide Apple’s renderer behind the first 128. | 代码+构建；多会话待真机 |
| G-28 | Low | Current Beta.25 compatibility docs and project description use the declared x64 build 20348 baseline; historical Preview claims remain labelled historical. | 代码+静态 |
| G-29 | Low | Compatibility checks and the current release ledger point to Beta.25 while preserving the historical Preview matrix as non-current evidence. | 代码+脚本测试 |
| G-30 | Low | PowerShell 5-compatible string comparisons replace overloads unavailable on Windows PowerShell. | `pwsh` 与 Windows PowerShell 脚本测试 |
| G-31 | Low | Update waiters own cancellation independently; the shared download/install operation is cancelled only when its last waiter leaves, with bounded underlying tokens. | 代码+Infrastructure 测试 |
| G-32 | Low | Clear-history preview uses repository SQL `COUNT(*)` instead of loading 100,000 rows into memory. | 代码+Infrastructure 测试 |
| G-33 | Medium | Music icon refresh awaits predecessor work inside its guarded path and disposes/cancels per-icon work so one failure cannot poison future refreshes. | 代码+构建 |
| G-34 | Low | Pre-cancelled staged imports still execute deterministic lease cleanup, and final lease cleanup isolates failures per lease. | 代码+Infrastructure 测试 |

## Test and release evidence

Current deterministic evidence:

- `dotnet build DropSpace.sln --no-restore --configuration Debug --verbosity minimal`: 0 warnings, 0 errors.
- `DropSpace.Core.Tests`: 226 passed.
- `DropSpace.Infrastructure.Tests`: 156 passed.
- Latest self-contained Portable Smoke reached the complete startup, single-instance,
  clipboard, Overlay, deletion-stress and hidden-startup flows in both `en-US` and
  `zh-CN`; the current development profile has `StartWithWindows=false`, so the
  script's fresh-profile default-registration assertion was evaluated separately
  without changing the user's setting.
- The non-self-contained App test host remains environment-conditional: the current
  machine has no registered Windows App SDK Runtime for its WinRT activation path,
  producing `REGDB_E_CLASSNOTREG` before 39 native smoke test bodies. This is not
  converted into a product-pass claim.
- Beta.25 Portable EXE, Inno 7.0.2 installer, unsigned MSIX and identity MSIX were
  generated; the MSIX package contains `Version=0.3.0.25`, and the update manifest
  passed strict version, size and SHA-256 verification.

The remaining release gates are the real Windows OS/DPI/multi-monitor/OLE,
Apple Music/SMTC, lock/sleep, installer/uninstaller and packaged/portable
artifact checks described in `compatibility-baseline.md` and
`WINDOWS_INTEGRATION.md`.
