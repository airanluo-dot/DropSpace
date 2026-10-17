# v0.3.2-beta.3 focused execution record

Baseline: clean worktree at main/v0.3.2-beta.2, 523d0dabb399a9610f1a1d0e26986244cb2b6d81.
The owner explicitly authorizes the next Beta and minimal validation. Other open PRs are
excluded. Main requires a PR and Build and test (x64); no protection bypass is used.

Source review found that lyric refresh computed scrolling using the preceding layout's
ActualWidth and detached full-line measurements; exact-width clipping had no glyph inset.
The host already owns an interruptible geometry spring. The fix measures attached text,
adds 2 DIP edge insets to measured width, rounds outward and defers scrolling until layout
and that spring settle. Offsets reset on content/layout/visibility retirement; existing
clips and long-line policies remain. Prefix widths are cached per current lyric line.

The Downloads page used text buttons, not SVG assets. Shared native action content now
uses 16 DIP Fluent glyphs, an 8 DIP gap and 36 DIP minimum button height. Alternative
localized labels reserve their native measured size. Resolving/finalizing states retain
the existing restricted actions; downloading/paused/failed actions keep their positions.
No downloader, persistence, dependency, model or update-channel behavior changes.

Execution ledger:
- Remote main/tags/releases and public rulesets inspected. The legacy protection API
  returned 403; the active ruleset API confirmed PR and required-build protection.
- Focused source inspection covered fitting and overflowing lyrics, long/short changes,
  font/DPI invalidation, hidden-view cancellation and all download action states.
- Linux has no WinUI runtime. No visual observation, screenshots, Windows DPI matrix,
  unit/browser/model/stress suite or full regression is claimed.
- Local release metadata consistency, source/experimental-acceptance binding, workflow
  trigger audit and git diff whitespace checks: passed once. These are integrity checks;
  no functional cases ran. The selected next-Beta PR route compiles with zero cases.
- Windows App/XAML PR build, exact-main packaging and installer payload check: pending.
- Release, assets, actual public downloads and website/update metadata: pending.

Existing signing policy and native package/updater checks remain mandatory. The historical
model acceptance is carried forward unchanged under the owner's corrective-release scope.
