# 0.3.2 Beta1 project execution ledger

The owner removed the fixed ceiling for this and future tasks on 2026-10-08. Select necessary
functional checks and keep one truthful record across local runs, agents, Actions, failures
and retries. Beta19's sealed 20/20 record is not reused as new execution.
Build, source/resource integrity, package/hash checks and real public metadata/
asset readback follow the existing non-functional production-check classification.

Executed before final-main Actions: **17**. Passes: **12**. Failures: **5**. Retries: **5**.

Reserved before execution:

| ID | Necessary scenario | Runs reserved | Result |
|---|---|---:|---|
| 01–04 | Four named installer override/percent parsing cases | 4 | Passed locally 2026-10-08; `artifacts/beta32/installer-four-cases.txt` |
| 05 | Website history fallback/write recovery/URL priority, one source-runtime scenario | 1 | Passed 2026-10-08T05:39:01Z; `artifacts/beta32/website-storage-case.txt`, actual runtime/JSDOM only |
| 06 | Retained nested resource/literal/raw-number message in a changed language | 1 | Passed locally 2026-10-08; exactly one filtered MSTest, `artifacts/beta32/message-results/retained-message.trx` |
| 07 | Real sample lifecycle in native App: initially no-DLC, install, navigation/action/Island, disable, enable, uninstall, normal shutdown | 1 | Failed launch attempt 2026-10-08T05:59:38Z: packaged output launched outside its package; no diagnostic root/receipt, timed out at 115 seconds. `artifacts/beta32/native-ui-launch-01.json` |
| 08 | Interrupted journal recovery and locked-file cleanup in isolated module storage | 1 | Failed 2026-10-08 before scenario assertions: official catalog field casing mismatch |
| 09 | Owned worker request cancellation and retired results | 1 | Failed 2026-10-08 before scenario assertions: same catalog field casing mismatch |
| 10 | Owned worker timeout and bounded stop | 1 | Failed 2026-10-08 before scenario assertions: same catalog field casing mismatch |
| 11 | Official public sample installation through the shared downloader | 1 | Passed 2026-10-08T06:19:17Z–06:19:36Z; actual anonymous download of committed-source release ZIP, `docs/dev/evidence/beta32/public-module-lifecycle.json` |
| 12 | Existing final-main installer payload extraction/uninstall producer scenario | 1 | Reserved for Actions |
| 18 | Website release selection with the real official DLC tag, Beta19 and Beta1 candidate metadata | 1 | Passed 2026-10-08T06:52:28Z–06:52:29Z; one named case, `docs/dev/evidence/beta32/website-dlc-filter-case.json` |

This narrowed plan selected 12 executions before the owner removed the fixed ceiling.
Keep the focused selection; add a check only when it resolves a concrete risk. Do not rerun
unchanged passing checks or automatically enable unselected probe modes and broad suites.

Reserve three retries (13–15) of 08–10 only after fixing the concrete catalog writer/parser
mismatch. The failed attempts began executable scenarios and count despite no assertion receipt.

All three retries passed locally 2026-10-08. Actual receipts:
`scripts/feature-module-probe/artifacts/recovery-02.json`, `cancel-02.json`, `timeout-02.json`.
The recovery case preserves the committed version/data through an interrupted journal and
records real locked-file pending cleanup before a later successful recovery. Cancel and
timeout each launch a real pathological owned worker and confirm bounded exit/no live session.

Reserve execution 16 as the single native lifecycle retry using the existing self-contained
Portable production configuration. The first native launch is counted despite no receipt.
Native retry (`artifacts/beta32/native-ui-launch-02.json`) failed on optional uniform pixel
readback before installation. Actual no-DLC navigation and normal shutdown were observed:
no module directory/entries/content, zero worker owners/active calls/timer after shutdown,
zero overlay surfaces. No full module lifecycle pass is claimed for this attempt.
Its receipt is retained under its isolated temporary root. Reserve execution 17 for the
same lifecycle retry with honest best-effort pixel capture; capture evidence is not an
extra release condition under the owner's narrowed scope.
Third native attempt passed 2026-10-08T06:11:43Z–06:11:46Z. The actual receipt is preserved
at `docs/dev/evidence/beta32/native-module-lifecycle.json`. It covers real installed navigation,
host button dispatch, manual Files protection, unchanged native widget rectangles, contribution
withdrawal, selected-page return, re-enable/uninstall, retained data and normal owner shutdown.
All four optional pixel readbacks failed content validation and remain explicitly warnings;
no visual pixel-quality pass, multi-display/DPI/language matrix or external media pass is claimed.
The three probe receipts are preserved beside it. Their original inaccurate process-only
`packageSource` labels are disclosed above; they do not establish anonymous downloads.
Cancel/timeout receipts' original `packageSource` label is inaccurate: these process-only
cases do not download any package. Their timestamps/assertions remain unchanged; writer corrected.

No broad unit suite, browser/language matrix, lyric/model evaluation or performance run is
authorized. Final-main package production's existing installer payload scenario costs one
execution and must be reserved before dispatch. Each new scenario is recorded before running
and updated with actual result/time/evidence, including an aborted attempt that began a scenario.
