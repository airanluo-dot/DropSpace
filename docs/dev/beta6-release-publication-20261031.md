# Beta6 release publication preparation — 2026-10-31

## Scope and source

The user authorized publication of the current Windows `v1.1.0-beta.6` candidate,
including all four maintenance rounds and the latest interface wording changes.
The immediate published upgrade baseline is `v1.1.0-beta.5`; the local starting
HEAD is `3ecea1207dc93df5383e4b061966da8d1843e3e4`.

This source record captures the publication preparation state before the PR build.
The actual run identities, retries and final public readback are recorded in
`artifacts/beta6-release-publication/publication-state.json` during execution and in
the public GitHub Actions runs and release assets. This record tracks this publication attempt. It does not replace or extend the
execution claims in the [first](beta6-maintenance-20261031.md),
[second](beta6-round2-maintenance-20261031.md),
[third](beta6-round3-maintenance-20261031.md) or
[fourth](beta6-round4-maintenance-20261031.md) maintenance records.

| Publication identity | Result |
| --- | --- |
| Candidate commit / PR | Pending |
| Final merged commit | Pending |
| Windows qualification run | Not started at record creation |
| Reused package bundle and receipt | Pending |
| Release / tag | Not published at record creation |
| Website / update API / public downloads | Not verified for Beta6 at record creation |

## Latest wording scope and completed local checks

- Updated 68 labels/messages with 405 localized value changes across ten interface
  languages; removed 10 unused resource IDs (100 localized values). Seven reviewed
  entries retained their original wording. No resource IDs were added.
- Corrected removal-warning selection to use the reference-only warning only when
  `card.IsFileReference && card.Item.Payload is null`. This does not change removal
  behavior.
- Resource XML, placeholders, exact change scope, localization integrity and
  independent scope/meaning/removal reviews passed. The nine non-English review
  ID lists match the affected entries. All 9,069 unaffected fingerprint pairs were
  preserved, and no production access to the ten deleted keys remains.
- The macOS catalog is an independent pinned historical baseline and was kept
  unchanged; it is outside this Windows wording review.
- The pre-existing static assertion for `AiLyricsPlainBetaHelp` expects
  `meaning errors`, while the original value is `Local translation model.`.
  The selected source check failed once on the current candidate and once on exact
  original HEAD sources/resources. Both failures are recorded; neither is a pass.
  The separate obsolete `AiLyricsDescription` assertion was removed alongside its
  unused resource.
- The wording review ran zero functional, native or model scenarios. Its local
  evidence is under `artifacts/beta6-copy-implementation/`, including
  `validation.json`, `static-check-execution.json`, `review-execution.json` and
  `final-removal-review.json`.

## Windows CI and packaging execution

No Windows CI, native App/XAML compilation or packaging run had completed for the
combined candidate when this record was created. Append exact commit, workflow
URL, outcome, retry reason and artifact identity as each action completes. Record
functional case counts separately from compilation/resource/package integrity
checks. A normal publication request does not opt into broad suites or model tests.

| Action | Commit / run / artifact | Outcome and retries |
| --- | --- | --- |
| App/XAML qualification | Pending | Not run |
| Package language and PRI inspection | Pending | Not run |
| Installer / Portable / MSIX packaging | Pending | Not run |
| Exact merged-commit bundle reuse | Pending | Not run |
| Release asset hashes and signing checks | Pending | Not run |

## Publication and public readback

| Surface | Identity / URL | Actual result |
| --- | --- | --- |
| Beta6 release and tag | Pending | Not published |
| Installer / Portable / MSIX downloads | Pending | Not downloaded or verified |
| Beta update metadata and API | Pending | Not read back |
| Website release and download links | Pending | Not read back |
| Optional component / module compatibility | Pending | No new qualification claimed |

## Remaining validation boundaries and known issues

Windows GUI/system integration, UNC behavior, upgrades from Beta5, packaged offline
language loading, representative long-text layout and both kinds of removal warning
still need target-system validation. Managed fixtures and static checks do not
establish native desktop, deployed Worker, real browser or two-device acceptance.

The existing native heap stability investigation and the overlapping staging-lease /
legacy-import ownership issue remain unresolved. The fourth-round record also notes
a possible remote pairing offer remaining until its TTL after local confirmation is
canceled. This publication record does not claim these issues are fixed. The Beta6 owner-request evidence and exact host-source binding carry forward the
existing experimental-model disclosures; they are not new model or native-stability
qualification.

## Final result

This is the pre-publication source snapshot. Final source/package identities and
public readback must be checked against the actual GitHub release and its linked
Windows CI, Release and Pages workflow runs; the execution ledger above retains
the local observations. No pending field in this preparation snapshot is a claim
that a later publication succeeded or failed.

## Release preparation review

- Live main and latest published Windows release were both Beta5 at preparation.
- The Beta6 owner request is recorded verbatim in the model-evidence directory.
  The exact-source approval check passed once, retaining `semanticApproved=false`.
- Six local source-only compilations passed with zero warnings/errors: Infrastructure
  and five linked-source probes. They executed no functional scenarios and do not
  replace the full Windows build.
- The overlapping-staging ownership risk was compared with Beta5 and is inherited,
  not a newly introduced candidate regression. It remains disclosed.
- The Worker source is protocol-compatible and needs no App-coordinated migration;
  no live Worker deployment is claimed.
