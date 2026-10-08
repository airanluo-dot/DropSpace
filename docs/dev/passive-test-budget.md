# Focused testing and execution records

Current and future development tasks have **no fixed functional case/scenario ceiling**. Minimize
unnecessary testing and select checks that resolve concrete risks in the affected behavior. Keep
one truthful project-wide execution record across local work, agents, PRs and workflows, including
failures and retries. Historical Beta18/Beta19 records retain their original limits and actual
counts; do not rewrite them or attribute new executions to those releases.

The frozen legacy automatic PR selection remains **10 cases**, not a target to fill. Its exact
selection guards against accidental automatic suite expansion; it is not a cap on necessary task
testing. Full suites and heavy diagnostics remain disabled by default and need an explicit user
request. A manual dispatch or development/release request alone does not enable them.

- Automatic PR: skip broad Core/Infrastructure/App regression suites, stress/native smoke, installer lifecycle and model inference. The next-Beta route compiles App/XAML and runs zero functional cases. Legacy routes may run the frozen 8 approved Core policy cases plus 2 approved Infrastructure lyric outcome cases, at most once. Source definitions have pinned Git blob identities, so new DataRows trigger fail-closed zero cases until reviewed.
- Automatic website events and ordinary manual release refresh: Node/Playwright suites are skipped. Static site build, localization integrity, deployment and public metadata verification continue. Diagnostics require explicit `full_tests=true` opt-in.
- Automatic CUDA contract PRs: skip Python/Node/MSTest fixture suites, but keep trusted component metadata/hash/source identity checks.
- Heavy AI/model/native music visual QA workflows: only explicit manual dispatch can start them; QA-branch pushes do not run them.
- Security scans, Windows compilation, version checks, code/source integrity, required package hashes and final release/API readback remain enabled as **non-functional production checks**, never counted as completed functional tests.
- The automated release dispatch bridge sets `allow_unrestricted_fallback=false`. The release job reuses the exact prevalidated CI bundle or fails closed; it cannot run the unbounded fallback suites unless a user explicitly requests `allow_unrestricted_fallback=true` in a manual workflow.

### Manual opt-in

Resource entries are not functional test cases. Localization source/translation checks and
actual static PRI inspection are production integrity checks, scoped once per resource set;
reuse the hash-bound package receipt. Record actual functional cases across languages, workflows,
failures and retries without a fixed ceiling. A release request is not broad diagnostic opt-in.

After an explicit user request for unrestricted tests, use `ci.yml` with `full_tests=true` on
`workflow_dispatch`. Manual release, website, CUDA, model and visual diagnostics support deliberately
requested suites; ordinary development and publication keep them disabled.

### Enforcement

`scripts/passive-test-budget.mjs audit` classifies the 12 existing workflows on every PR; unknown
future workflows or accidental diagnostic auto triggers fail the audit. `preflight` checks frozen
test source identities, and `verify` reads actual TRX counts and requires the exact selected PR
cases to pass without automatic expansion. These guards do not enforce a task-wide numerical
ceiling or prohibit necessary focused testing. This policy changes no App source,
`RELEASE_VERSION`, historical receipts, existing tags or published assets.
