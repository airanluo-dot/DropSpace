# Permanent passive CI test ceiling

Use one project-wide task ledger with at most **20 actual functional cases/scenarios**, across
local work, agents, PRs and workflows, including failures and retries. The limit never resets per
trigger, language or module. The frozen legacy PR selection is **10 cases**, not a target to fill;
execute only if the task budget permits. Unrestricted diagnostics need an explicit user request;
a manual dispatch or development/release request alone is not an exemption. Keep this independent
of App release version numbers.

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

After an explicit user request for unrestricted tests, use `ci.yml` with `full_tests=true` on
`workflow_dispatch`. Manual release, website, CUDA, model and visual diagnostics support deliberately
requested suites; ordinary development and publication keep them disabled.

### Enforcement

`scripts/passive-test-budget.mjs audit` classifies the 12 existing workflows on every PR; unknown future workflows or accidental diagnostic auto triggers fail the audit. `preflight` checks frozen test source identities, and `verify` reads actual TRX case counts, rejects repeats/failures or any unapproved case expansion. This permanent policy changes no App source, `RELEASE_VERSION`, existing tags or published assets.
