# Focused testing

Minimize testing, keeping only the verification needed for the actual change and concrete risks.
Avoid rerunning checks that already passed while the relevant code and inputs are unchanged,
unrelated full suites, broad matrices, unrelated model or performance evaluations, repeated
security audits, and layers of formal evidence that add no useful confidence.

Use judgment rather than fixed test counts or rounds, and do not maintain a test-budget ledger.
Run tests explicitly requested by the user as requested. Report real failures honestly; never
fabricate passes or mark skipped, unavailable, or unexecuted checks as passed.

## Existing workflow boundaries

A development/release request or ordinary manual release refresh does not opt into broad diagnostics.
Keep this rule independent of App release version numbers.

- Automatic PR: skip broad Core/Infrastructure/App regression suites, stress/native smoke, installer lifecycle and model inference. The next-Beta route compiles App/XAML without functional suites. Legacy routes use the approved Core policy and Infrastructure lyric outcome selection. Source definitions have pinned Git blob identities, so changed definitions fail closed until reviewed.
- Automatic website events and ordinary manual release refresh: Node/Playwright suites are skipped. Static site build, localization integrity, deployment and public metadata verification continue. Diagnostics require explicit `full_tests=true` opt-in.
- Automatic CUDA contract PRs: skip Python/Node/MSTest fixture suites, but keep trusted component metadata/hash/source identity checks.
- Heavy AI/model/native music visual QA workflows: only explicit manual dispatch can start them; QA-branch pushes do not run them.
- Security scans, Windows compilation, version checks, code/source integrity, required package hashes and final release/API readback remain enabled as **non-functional production checks**, never counted as completed functional tests.
- The automated release dispatch bridge sets `allow_unrestricted_fallback=false`. The release job reuses the exact prevalidated CI bundle or fails closed; it cannot run the unbounded fallback suites unless a user explicitly requests `allow_unrestricted_fallback=true` in a manual workflow.

### Manual opt-in

Resource entries are not functional test cases. Localization source/translation checks and
actual static PRI inspection are production integrity checks; reuse valid results and the
hash-bound package receipt when relevant inputs are unchanged. A release request is not broad
diagnostic opt-in.

To run unrestricted CI tests, use `ci.yml` with `full_tests=true` on `workflow_dispatch`. Manual `release.yml`, website, CUDA, AI model and visual diagnostic workflows continue to support the deliberately requested validation suites.

### Existing automation

This documentation update leaves workflow entry points, CI guard scripts and production integrity
checks unchanged. Their implementation-specific test selections do not define a project-wide task
budget. Historical release evidence and test totals retain their original scope and results.
