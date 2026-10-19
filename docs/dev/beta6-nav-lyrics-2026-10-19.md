# Beta 6 navigation and compact-lyrics repair

## Baseline and scope

Remote main was verified as `e4a020dd8613b5f651cc4b276ce4acea8de97d79`, the published Beta 5 source. The local checkout was older; the repair branch was created directly from current main. Unrelated untracked `.codex/` and `scripts/__pycache__/` content was preserved. No other open PR is included.

Only the transfer-button presentation and compact music layout/scroll continuity change. Widget editing, storage, transfer execution, media clocks, provider selection and animation timing are unchanged. The owner subsequently authorized normal publication after local verification; Beta 6 had no existing tag at release preparation.

## Reproduction and repair

Apple Music actually played The Way You Felt, then Happier. The reported symptom was a small horizontal nudge within the same lyric, rather than a large shake. A temporary, opt-in observer sampled the real WinUI compact control at CompositionTarget.Rendering, buffering samples in memory and writing once after 45 seconds. It did not drive playback or alter animation timing. Each sample included monotonic time, playback position, original-line identity, text X, scroll offsets, measured text width, actual viewport width, pending layout and host geometry-settled state.

At 125% display scaling, independently rounded child dimensions consumed more width than the fractional-DIP dynamic-width calculation reserved. For example, a 206.992-DIP line received a 205.600-DIP viewport. This false overflow scrolled by 1.392 DIP despite the island being sized for the full line. When the timed frame became empty between lines, the display retained the previous text but the old scroll calculation returned zero, causing a rightward reset.

The repair preserves fractional music layout through the scale host, content root and compact control; outer surface pixel coverage is unchanged. Other compact content keeps its existing rounding. Timed waiting retains the original row's offset. Root notifications only invalidate glyph measurement when rasterization scale changes; text/font/theme changes still use their existing invalidation paths.

The transfer button uses the system Switch glyph at 20 DIPs in a centered 40-by-36-DIP subtle button, replacing the oversized Upload button. Its tooltip, badge, flyout and event wiring are preserved.

## Local execution ledger

Local evidence is under `artifacts/nav-lyrics-fix/` (ignored development output, not release assets):

| Run | Actual scope | Result |
| --- | --- | --- |
| First Debug build | Native App/XAML inside sandbox | Failed without actionable compiler errors; no pass claimed |
| Unsandboxed baseline build | Native App/XAML | Failed on stale local CUDA App-source binding; original log retained |
| Baseline with temporary observer | Full Debug App/XAML; copied CUDA descriptor bound to verified current source, unchanged component hashes | Passed, zero warnings/errors |
| Actual baseline playback | 44.993 seconds, 10,589 frames, original dynamic width | 16 settled same-line movement steps; six rightward resets over 0.5 DIP; false overflow up to 1.413 DIP |
| Repaired observer build | Full Debug App/XAML | Passed, zero warnings/errors |
| Matching repaired playback | 44.995 seconds, 10,548 frames, same song and dynamic width | Zero settled same-line movement steps; all sampled fitting rows have zero original scroll offset |
| Actual long-line playback | 44.985 seconds, 9,988 frames, temporary fixed 180-DIP viewport | 263 forward offset steps, zero same-line backward steps over 0.5 DIP; sung-line offset reached 126.570 DIP; credits were also encountered |
| Navigation presentation | Collapsed, expanded, mouse hover and existing empty transfer flyout | Observed in modified executable; screenshots retained |
| Final local build and launch | Observer removed; full Debug App/XAML and normal playback | Passed, zero warnings/errors; process 42152 ran the repository-built executable, not the installed Beta 5 |

Movement comparisons require unchanged text/line identity and settled geometry in both adjacent frames; intentional animated line transitions are excluded, not called jitter. Captures start at slightly different playback times and are not frame-for-frame identical. The same fitting lyric rows are present in both. Temporary fixed width was restored to the user's original dynamic-width setting and read back as true. No user cache or model was cleared.

The diagnostic source copy, three raw captures and `summary.json` remain local for reproduction. No observer, environment switch or per-frame logging is shipped. No new unit suite was added or run; native measurements resolve the actual fractional-layout failure more directly.

## Release qualification and limits

Publication uses the existing next-Beta route: PR Release App/XAML compilation without functional cases; exact merged-main packaging with one isolated installer-payload scenario; unchanged-byte promotion and public Release/website/update checks. Actual CI status is established by the associated PR and workflow receipts, not this preparation record.

The live exercise used Apple Music, the current monitor at 125% scaling, and the existing user theme/font settings. Other scaling factors, text-size settings, themes, playback providers, prolonged playback, GPU/model inference and two-device file transfers were not exhaustively tested. Existing widget interactions were preserved by diff review, not re-exercised. Local visual acceptance used a Debug build; the published Release packages receive the separate required build/integrity qualification.

PR CI 39245986830 stopped before compilation because the Beta 5 publication record bound the previous version and UI source hashes. Updated the exact Beta constant and carried forward the unchanged runtime/model observations with the current owner publication instruction; no semantic/model pass is asserted. The local binding check first rejected millisecond timestamps, then passed after canonical UTC formatting. No workflow or gate was weakened.
