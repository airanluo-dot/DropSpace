# Beta18 verification ledger

## Baseline and budget

Initial main: `00d07b3773dc614587b75fbcf1affd45a136535b`.
Existing solution case denominator, before new tests: Core658, Infrastructure943, App523 =2,124. Count expands DataRow and respects existing project Compile Remove; no dynamic data or Ignore found. NativeLifetime links five already-counted App cases, excluded. Node/PowerShell tests are not added to inflate denominator. Maximum21 executions (floor of1%).

Actual total: **11 executions** (8 Core + 2 Infrastructure + 1 installer scenario), **11/2,124 = 0.518%**. The two unit-test projects ran once each; no case was repeated. The producer ran once. No test denominator was increased; the owner later allowed a small budget excess, which was unnecessary. CUDA fixtures remain explicitly skipped. Source inspection, syntax parsing, compile, production package integrity/version checks and live publication readback are recorded separately and never described as functional tests.

## Executed test cases

| Actual run | Cases | Result | Evidence |
| --- | ---: | --- | --- |
| Final-source Core configuration/content priority selection | 8 | 8 passed, 0 failed, 0 skipped; one invocation | `focused.trx`, private schema-2 receipt; [producer 37656714883](https://github.com/airanluo-dot/DropSpace/actions/runs/37656714883) |
| Lyrics completed-no-match versus all-provider-failure selection | 2 | 2 passed, 0 failed, 0 skipped; one invocation | `focused-infrastructure.trx`, same receipt/producer |
| Isolated real installer installation, installed portable payload hash and uninstall | 1 | Producer completed all exit-code and payload-hash assertions; conservatively counted as one scenario | `installer.json` and public `runtime-publication.json` binding |
| **Total including repeats** | **11** | **No repeats** | **0.518% of original 2,124** |

Private raw TRX totals (8/2), projects, exact filters, commit/tree and file hashes were bound by the producer, then verified again in the release workflow immediately before publication. The public Actions log independently reports both exact pass counts. Public runtime metadata records the actual installed-payload inspection. No native smoke, stress, model/GPU, real-player or broad regression suites were executed. Source/package/public download integrity checks below execute zero App functional test cases.

## Build and production checks

- PR107/108 required full Windows App/XAML compilation succeeded in runs [37643784243](https://github.com/airanluo-dot/DropSpace/actions/runs/37643784243) and [37644096871](https://github.com/airanluo-dot/DropSpace/actions/runs/37644096871). Zero test cases.
- Website deployments after those merges succeeded in runs [37644230224](https://github.com/airanluo-dot/DropSpace/actions/runs/37644230224) and [37644488482](https://github.com/airanluo-dot/DropSpace/actions/runs/37644488482). Zero test cases.
- Full source reads, frozen-manifest hash reconciliation, changed XML/JSON/YAML syntax checks and diff whitespace checks are source/production integrity checks. They execute zero functional test cases.
- Initial Beta18 App/XAML compilation succeeded in PR110 run [37655149894](https://github.com/airanluo-dot/DropSpace/actions/runs/37655149894), zero cases; the workflow failed because its required result job did not materialize. After the subsequent lyrics-state fix, final candidate run 37656012361 completed the fresh App and focused-project compiles before final-main producer 37656714883 executed the selected cases.
- Exact Beta18 CUDA metadata/source binding succeeded in [37655149856](https://github.com/airanluo-dot/DropSpace/actions/runs/37655149856); original Python/Node/MSTest fixtures were all skipped, zero cases.
- Final candidate [PR110 build 37656012361](https://github.com/airanluo-dot/DropSpace/actions/runs/37656012361) succeeded: complete Windows App/XAML plus both selected test-project compiles, zero cases. The actual required `Build and test (x64)` job materialized and succeeded after its condition was made explicit. No unsupported parser root cause is claimed.
- [PR110](https://github.com/airanluo-dot/DropSpace/pull/110) merged to main `e4d6b1b2ac22dd2a70b0bd08bcabd8d21a6d6175`; its source tree `41b89fae2f9df432470acd807f8cc0217dcc0d1d` equals the qualified PR tree.
- Final-main producer [37656714883](https://github.com/airanluo-dot/DropSpace/actions/runs/37656714883) succeeded and created immutable artifact `11498937080` (archive SHA-256 `6d0ccdc9a282d3c353b07e4cf4043c447065c93795371e9d28ec899bd4daacca`). Compilation, package versions, inspected embedded runtime bytes, installer payload and update metadata all passed their actual production checks.
- [Release 37662335914](https://github.com/airanluo-dot/DropSpace/actions/runs/37662335914) succeeded at that same exact main commit. It reused the actual bundle, verified both raw TRXs/receipts, performed no rebuild or test invocation, and published [v0.3.1-beta.18](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.1-beta.18).
- All eight public assets were fully downloaded anonymously (HTTP 200) and their complete hashes match GitHub digests and all seven payload rows in SHA256SUMS. Downloaded portable and installer PE versions and actual MSIX identity are `0.3.1.18`; update metadata is `0.3.1-beta.18`, code `3010018`, Windows build `20348`. See [public-verification.json](public-verification.json).
- The independent CUDA release remains unchanged. The App has no CUDA ZIP; its small descriptor binds the existing component/cache identity and exact App commit. The existing component archive URL also returned a valid ZIP prefix through an anonymous Range GET (HTTP 206); it was not fully downloaded again or executed.
- [Pages 37662657842](https://github.com/airanluo-dot/DropSpace/actions/runs/37662657842) succeeded. Live `api/v1/releases.json` shows Beta18 first and Stable v0.2.1; `api/v1/latest-change.json` identifies Beta18 and includes both features and the corrected lyrics outcome. Publication's end-to-end readback also passed. Website fixture/browser tests were skipped.
- Documentation-only final-record updates do not change the published App source or require another test/package producer. Any mandatory PR compilation for those records executes zero test cases.

## Unverified runtime behavior

This cloud host is Linux without a .NET SDK or a Windows interactive session. Real Windows appearance switching, animation frames/legibility, monitor movement, Shell drag-in/drag-out, media apps, GPU fallback and native AI semantic quality cannot be observed here. Hosted compilation and selected managed policy cases cannot establish those behaviors.
