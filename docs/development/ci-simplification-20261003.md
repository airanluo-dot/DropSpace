# CI simplification review — 2026-10-03

The normal PR flow keeps one Windows producer for compilation, Core/Infrastructure/App tests,
portable publication, installer lifecycle and MSIX/identity packages. Both English and Chinese
portable smoke passes now execute sequentially in one additional Windows job, after downloading
and verifying the same run-attempt artifact once. Chinese smoke still executes after an English
failure when the artifact verified successfully. Neither smoke pass is replaced by a cached result.

The existing required names `Build and test (x64, en-US)` and `Build and test (x64, zh-CN)` remain
lightweight Ubuntu compatibility checks. Both require the producer and combined smoke job to
succeed; failure, cancellation, skip or missing results fail them. Their summaries explicitly say
that they ran no additional tests. No repository ruleset changes are needed.

## Encrypted share worker

The required worker job remains present on every CI run. It compares the push's complete
`before`/tested tree, or the PR base/tested merge tree, using NUL-delimited Git paths with rename
detection disabled so both deleted and added names count. It fetches only missing exact revisions
at depth one. Event size, diff bytes, path count and individual Git commands are bounded. Unknown
events, unavailable revisions, failed fetches, malformed or oversized output and timeouts require
actual worker tests rather than allowing a skip. The workflow itself and scope helper are related
inputs. Worker files, dependencies, configuration, shared actions and App sharing protocol inputs
also require tests; the exact worker README and unrelated changes can be not applicable.

A verified unrelated diff produces an explicit **not-applicable** summary, with comparison SHAs
and path counts. It does not claim a test pass. Checkout/scope failures fail the required job;
failed scope checks also attempt the actual worker suite when checkout succeeded.

## Removed and retained work

| Surface | Decision and reason |
| --- | --- |
| CI and Release brand generation | Remove the second generation after clean-checkout `-Verify`; retain verification, no-mutation check and packaged brand asset chain validation. |
| Native crash probe | Run only after a verified portable smoke failure; retain smoke failure and diagnostic upload. |
| Website Release events | Use `published` once instead of three overlapping release events; retain explicit release-workflow dispatch. GitHub-token-created releases require that dispatch. |
| Core/Infrastructure/App and installer tests | Retain the shared producer's single execution and all security, data preservation, clipboard, deletion, startup and install/upgrade/uninstall guards. |
| Both locale smoke passes | Retain actual startup and native lifetime checks in each existing smoke bootstrap; only their setup/download/verification job overhead is consolidated. |
| App build and portable restore | Retain: current test/build and self-contained publish inputs are not demonstrably identical. |
| Signed Release validation | Retain artifact hashes, signed-byte inspection and installer lifecycle: signing changes the inputs. |
| Small source/contract checks | Retain provenance, authorization, localization and publication checks; they do not justify risking false successful checks. |
| Cross-run reuse | Do not add reuse without a complete dependency/toolchain/input fingerprint. Existing same-attempt artifact digest/identity checks remain enforced. |

## Workflow audit

| Workflow | Existing trigger/scope and disposition |
| --- | --- |
| `ci.yml` | Main push, main PR and manual; no branch-push duplicate of the PR producer. Changes above only. |
| `release.yml` | Explicit manual publication/validation, exact main commit and release authorization. Remove duplicate brand generation only. |
| `deploy-website.yml` | Website-path main pushes/PRs, publication and manual dispatch. Consolidate Release publication events only. |
| `secret-scan.yml` | Branch/main pushes, PR, scheduled and manual security scan. Retain: histories/merge trees can differ, including deleted secrets. |
| `publish-release-bridge.yml` | Explicit immutable-main publication bridge. Retain expected commit, actor and new-branch guards. |
| `music-visual-qa.yml` | Scoped visual QA/manual. Retain; not a normal PR build duplicate. |
| `ai-model-diagnostics.yml` | Scoped diagnostics/manual. Retain; no new model runs. |
| `hy-plain-model-diagnostics.yml` | Scoped diagnostics/manual. Retain. |
| `m2m-model-diagnostics.yml` | Scoped diagnostics/manual. Retain. |
| `marian-model-diagnostics.yml` | Scoped diagnostics/manual. Retain. |
| `ct2-runtime-diagnostics.yml` | Scoped diagnostics/manual. Retain. |

GitHub documents [Release publication events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#release)
and [token-triggered workflow behavior](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow).

Verification is limited to targeted Node contracts, actual local Git diff/CLI fixtures and workflow
parsing. No workflow dispatch, Windows full build, model pipeline, deployment or App installation
is performed for this refactor. Windows Actions execution remains to be checked after the final
integration push; local Linux checks do not claim actual WinUI startup or installer acceptance.
