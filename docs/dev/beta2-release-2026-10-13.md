# v0.3.2-beta.2 publication — 2026-10-13

The owner's latest instruction authorizes completion of the current local task followed by
publication of the next Beta with minimal testing. This replaces the earlier local-only
restriction for the release phase. Round 25 is saved in `6b39136`; its sole new real-SQLite
retention scenario passed once without restore or retry. The complete prior local execution
history, including failures/retries and target-platform gaps, remains in
[the iteration record](local-iterations-2026-10-13.md).

## Baseline and scope

Live main and latest App release `v0.3.2-beta.1` both resolved to
`461be3d10b72b6ae1b872643fd6d00541e794ef8`. Its eight assets were uploaded. Neither the
`v0.3.2-beta.2` tag nor Release existed. Five other open PRs (#118–121 and #123) remain
outside this candidate; #123 has independent module/dependency/process changes and must
not replace this session's module startup retirement protections. No known regression
introduced by this candidate was found in the current official sample's reachable scope.
This is not a claim that every existing or future module configuration is defect-free.

Main requires a PR and the `Build and test (x64)` check. No protection bypass, force push,
historical tag/asset replacement or stable installation/user-data mutation is planned.
The existing next-Beta path compiles the PR with zero functional cases, then packages the
identical merged-main tree. It retains one isolated installer payload scenario and
source/resource/package/hash verification. Release publication reuses that exact bundle;
`allow_unrestricted_fallback` and all broad diagnostics remain false.

## Execution ledger

- Remote baseline, collision and protection reads: completed; no functional cases.
- Source review and corrective-release preparation: completed. The existing experimental
  model acceptance is carried forward without new inference or semantic approval. The
  27 changed currently fingerprinted source paths are explicitly rebound; model/runtime,
  prompts, fixtures/admission and targets are unchanged. Full source identity is also
  bound by the candidate Git tree, including helpers outside that specific fingerprint.
- Local release metadata/approval/workflow integrity: passed once each. These are
  production integrity checks, not functional executions. The workflow audit retains
  the frozen legacy selection; the selected next-Beta route runs zero PR cases.
- PR full Windows App/XAML compilation: pending; no runtime result claimed.
- Exact-main packages and isolated installer payload scenario: pending.
- Immutable Release, real public asset downloads and website/update/module readback: pending.

The complete release notes identify implemented behavior and the remaining Windows/NetEase,
PDF/UI interaction gaps. Packaging success cannot close those gaps. No large regression,
browser, model or native stress suite is authorized or claimed.
