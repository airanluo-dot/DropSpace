# Permanent passive CI test ceiling

Automatically triggered DropSpace workflows execute at most **20 functional test cases or scenarios per passive trigger**, across eligible workflows. The frozen PR selection is **10 cases**, not a target to fill the budget. Explicit operator `workflow_dispatch` diagnostics are exempt. Keep this rule independent of App release version numbers.

- Automatic PR: skip broad Core/Infrastructure/App regression suites, stress/native smoke, installer lifecycle and model inference. The next-Beta route compiles App/XAML and runs zero functional cases. Legacy routes may run the frozen 8 approved Core policy cases plus 2 approved Infrastructure lyric outcome cases, at most once. Source definitions have pinned Git blob identities, so new DataRows trigger fail-closed zero cases until reviewed.
- Automatic website events and ordinary manual release refresh: Node/Playwright suites are skipped. Static site build, localization integrity, deployment and public metadata verification continue. Diagnostics require explicit `full_tests=true` opt-in.
- Automatic CUDA contract PRs: skip Python/Node/MSTest fixture suites, but keep trusted component metadata/hash/source identity checks.
- Heavy AI/model/native music visual QA workflows: only explicit manual dispatch can start them; QA-branch pushes do not run them.
- Security scans, Windows compilation, version checks, code/source integrity, required package hashes and final release/API readback remain enabled as **non-functional production checks**, never counted as completed functional tests.
- The automated release dispatch bridge sets `allow_unrestricted_fallback=false`. The release job reuses the exact prevalidated CI bundle or fails closed; it cannot run the unbounded fallback suites unless a user explicitly requests `allow_unrestricted_fallback=true` in a manual workflow.

### Manual opt-in

Resource entries are not functional test cases. Localization source/translation checks and
actual static PRI inspection are production integrity checks, scoped once per resource set;
reuse the hash-bound package receipt. Keep one 20-case project-wide ledger across languages,
workflows, failures and retries. A release request is not broad diagnostic opt-in.

To run unrestricted CI tests, use `ci.yml` with `full_tests=true` on `workflow_dispatch`. Manual `release.yml`, website, CUDA, AI model and visual diagnostic workflows continue to support the deliberately requested validation suites.

### Enforcement

`scripts/passive-test-budget.mjs audit` classifies the 12 existing workflows on every PR; unknown future workflows or accidental diagnostic auto triggers fail the audit. `preflight` checks frozen test source identities, and `verify` reads actual TRX case counts, rejects repeats/failures or any unapproved case expansion. This permanent policy changes no App source, `RELEASE_VERSION`, existing tags or published assets.
