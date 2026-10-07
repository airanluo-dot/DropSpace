# Round 3 final release route review

This independent source-only review follows the three complete App reviews. It
checks release orchestration and the actual final Beta18 route; it does not claim
another complete App review or any build, test, native execution or publication.

## Coverage

Read the isolated Windows CI producer and required-check aggregator, the release
promotion/publication and Pages deployment routes, all other workflow triggers,
and the exact Beta18 producer/validation scripts. Also read the packaging,
static bundle/resource inspection, reviewed runtime/language reuse, corrective
release preparation, CUDA metadata staging/binding and update-manifest scripts
called by this route. Confirmed the two selected Core test classes contain eight
nonparameterized methods; the later final lyric-state correction adds two
parameterized Infrastructure cases under one exact method filter. Scope includes:

- `.github/workflows/ci.yml`, `release.yml`, `deploy-website.yml`,
  `cuda-component-contracts.yml`, `secret-scan.yml`, `publish-release-bridge.yml`
  and triggers for the music/model diagnostics workflows.
- `scripts/Build-Beta18Candidate.ps1`, `beta18-release-validation.mjs`,
  `ci-release-promotion.mjs`, `Build-PortableExe.ps1`,
  `Build-UnsignedPackage.ps1`, `Build-IdentityPackage.ps1`, `Build-Installer.ps1`,
  `Test-MsixSymbolPolicy.ps1`, `Extract-StaticBundleAssembly.ps1`,
  `Inspect-AiRuntimePayload.ps1`, `Get-ReviewedAiRuntime.ps1`,
  `Reuse-BundledLyricsLanguageModel.ps1`, `prepare-corrective-release.mjs`,
  `stage-reviewed-cuda-metadata.py`, `Test-CudaBuildBinding.ps1`,
  `New-UpdateManifest.ps1` and `Test-UpdateManifest.ps1`.
- The exact selected Core test definitions and CUDA contract workflow test
  definitions; these were read, not executed.

## Confirmed finding and fix

**R3-REL-01 — unrelated automatic CUDA contract tests bypassed the release
budget.** The PR path filter includes `scripts/test-ai-release-approval*.mjs`,
which this release changes, so the component-contract workflow would run even
though the isolated CI and Pages routes skip their legacy suites. It previously
ran 29 expanded MSTest CUDA cases, nine Python methods and Node contract suites.
The 29 MSTest cases alone exceed the maximum 21 allowed executions before the
then-planned eight focused cases and one installer scenario.

The workflow now reads the actual checked-out `RELEASE_VERSION`. For the exact
`v0.3.1-beta.18` version, every Python/Node/MSTest fixture step and their dedicated
dependency setup/restore steps are explicitly skipped. It stages the unchanged
reviewed component metadata for the actual checkout SHA and checks its App tag,
commit, manifest size/hash and component identity with the existing production
binding checker. The step summary states that fixtures are skipped, not passed,
and that no native worker/model runs. All original tests and their historical
routes remain intact; no cases or denominator were changed.

## Actual required-check scheduling failure

The first real PR run `37655149894` at head
`31cc31e886688b273649f34aeb0074ab72ffdb10` completed as a workflow failure despite
successful scope classification and isolated Windows compilation, with the
legacy producer correctly skipped. The actual workflow file contains the
required-check aggregator, but both the jobs API and check-suite
`102009256636` contain only those three jobs: the literal `Build and test (x64)`
job was never materialized. PR #110 consequently remained blocked. The only
annotations were six cache-action Node deprecation warnings; no App compilation
or workflow syntax error was reported.

The aggregator now uses the explicit `${{ always() }}` job expression while
retaining all three dependencies and its existing checks for actual success,
failure, cancellation and intentional skip results. This is the minimum
scheduling normalization. Bare `always()` is normally supported by GitHub, so
the available API evidence does not establish a parser defect. The next real PR
run must confirm that the required job is created and passes only after its
selected producer succeeds; this source edit alone is not a verified resolution.
No branch protection or synthetic check API was changed.

## Final source assessment

- PR CI compiles the complete Windows App/XAML and both selected test projects
  with `--no-restore`, recording all three build logs in a compilation-only
  receipt. It performs no functional tests.
- Final-main package production verifies a successful merged PR receipt with
  the identical tree, then invokes the selected eight Core cases once, the two
  selected Infrastructure cases once, and one isolated installer/payload-hash/
  uninstall scenario. The plan is 11 executions against the unchanged 2,124
  baseline, below the maximum 21. It has no broad-suite or automatic retest
  fallback.
- Publication requires the exact successful main producer, verifies actual
  separate Core/Infrastructure TRX totals of 8/2, aggregate total 10, both exact
  project filters and immutable package hashes, and rechecks the final receipt
  immediately before publishing. Schema-2 private validation binds both raw TRX
  files and the exact source commit/tree. It promotes existing bytes and does not
  rebuild or repeat tests. Both TRX files are retained through the intermediate
  publication artifact without becoming public Release assets.
- The eight public assets exclude private identity and execution receipts. CUDA
  descriptor/manifest reference the existing independent component; no unchanged
  CUDA archive is republished.
- Pages skips fixture/browser suites for exact Beta18 while preserving static
  build, release synchronization, deployment and published metadata checks.
- Secret scanning is a repository source scan. Music/model diagnostic runs
  require dedicated QA branches or explicit dispatch and do not run for this
  release branch. The publication bridge does not build or test.

No additional concrete blocking defect was found in the inspected route. This
is a source assessment: Windows compilation, actual focused execution, installer
payload verification and public download/version verification remain the real
release checks. This reviewer executed zero cases and made no remote changes.
